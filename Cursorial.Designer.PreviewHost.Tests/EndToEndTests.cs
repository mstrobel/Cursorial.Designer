using System.Diagnostics;

using Cursorial.Designer.Protocol;

namespace Cursorial.Designer.Tests.PreviewHost;

/// <summary>
/// Spawns the real preview host executable and speaks the wire protocol over stdio — the exact
/// integration surface the Rider plugin uses. The host dll is next to the test assembly because
/// the test project takes a ProjectReference on it.
/// </summary>
public class EndToEndTests
{
    private static readonly string HostDll =
        Path.Combine(AppContext.BaseDirectory, "Cursorial.Designer.PreviewHost.dll");

    [Fact]
    public async Task Full_stdio_session_initialize_load_frame_shutdown()
    {
        Assert.True(File.Exists(HostDll), $"Host dll not found at {HostDll}.");

        using var process = Process.Start(new ProcessStartInfo("dotnet", [HostDll])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

            async Task<PreviewEvent> NextEvent()
            {
                var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                Assert.NotNull(line);
                return PreviewProtocol.DeserializeEvent(line);
            }

            async Task Send(PreviewCommand command)
            {
                await process.StandardInput.WriteLineAsync(PreviewProtocol.Serialize(command));
                await process.StandardInput.FlushAsync(timeout.Token);
            }

            var ready = Assert.IsType<ReadyEvent>(await NextEvent());
            Assert.Equal(PreviewProtocol.Version, ready.ProtocolVersion);

            await Send(new InitializeCommand { ProtocolVersion = 1, Columns = 40, Rows = 10 });
            var initialFrame = Assert.IsType<FrameEvent>(await NextEvent());
            Assert.Equal(40, initialFrame.Columns);

            await Send(new LoadXamlCommand
            {
                Id = 1,
                Xaml = """
                       <StackPanel xmlns="https://cursorial.dev/ui" xmlns:x="https://cursorial.dev/xaml">
                           <TextBlock Text="stdio works"/>
                       </StackPanel>
                       """,
            });

            // The load first reports its consumed on-disk dependencies (none for inline text).
            var dependencies = Assert.IsType<DependenciesEvent>(await NextEvent());
            Assert.Empty(dependencies.Files);

            var diagnostics = Assert.IsType<DiagnosticsEvent>(await NextEvent());
            Assert.Empty(diagnostics.Items);

            // After initialize's full frame, the load emits a row-level DELTA (same dimensions).
            var frame = Assert.IsType<FrameEvent>(await NextEvent());
            var text = frame.Delta == true
                ? string.Join('\n', (frame.Changed ?? []).Select(c => string.Concat(c.Runs.Select(r => r.Text))))
                : string.Join('\n', frame.Lines.Select(runs => string.Concat(runs.Select(r => r.Text))));
            Assert.Contains("stdio works", text);

            // A malformed line must produce an error event without killing the session.
            await process.StandardInput.WriteLineAsync("this is not json");
            await process.StandardInput.FlushAsync(timeout.Token);
            Assert.IsType<ErrorEvent>(await NextEvent());

            await Send(new ResizeCommand { Columns = 50, Rows = 12 });
            var resized = Assert.IsType<FrameEvent>(await NextEvent());
            Assert.Equal(50, resized.Columns);

            await Send(new ShutdownCommand());
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
    }
}
