using System.Diagnostics;
using System.Reflection;

using Cursorial.Designer.Protocol;

namespace Cursorial.Designer.Tests.PreviewHost;

/// <summary>
/// Spawns the real preview host executable with arbitrary CLI arguments and drives the wire
/// protocol — the shared engine of the user-directory end-to-end tests. Debug/info
/// <see cref="LogEvent"/>s (the launcher narrates non-bundle assembly resolutions) are
/// collected into <see cref="Logs"/> rather than returned from <see cref="NextEvent"/>, so
/// tests read the meaningful event stream positionally AND can assert on resolution origins.
/// </summary>
internal sealed class HostProcessHarness : IDisposable
{
    public static readonly string HostDll =
        Path.Combine(AppContext.BaseDirectory, "Cursorial.Designer.PreviewHost.dll");

    private readonly Process _process;
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(60));

    /// <summary>Every log event read so far, in arrival order.</summary>
    public List<LogEvent> Logs { get; } = [];

    public HostProcessHarness(params string[] arguments)
    {
        Assert.True(File.Exists(HostDll), $"Host dll not found at {HostDll}.");
        _process = Process.Start(new ProcessStartInfo("dotnet", [HostDll, .. arguments])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
    }

    /// <summary>The next non-log event (log events are diverted into <see cref="Logs"/>).</summary>
    public async Task<PreviewEvent> NextEvent()
    {
        while (true)
        {
            var line = await _process.StandardOutput.ReadLineAsync(_timeout.Token);
            Assert.NotNull(line);
            var @event = PreviewProtocol.DeserializeEvent(line);
            if (@event is LogEvent log)
            {
                Logs.Add(log);
                continue;
            }

            return @event;
        }
    }

    public async Task Send(PreviewCommand command)
    {
        await _process.StandardInput.WriteLineAsync(PreviewProtocol.Serialize(command));
        await _process.StandardInput.FlushAsync(_timeout.Token);
    }

    /// <summary>Requests shutdown and asserts the process exits cleanly.</summary>
    public async Task ShutdownCleanly()
    {
        await Send(new ShutdownCommand());
        await _process.WaitForExitAsync(_timeout.Token);
        Assert.Equal(0, _process.ExitCode);
    }

    public void Dispose()
    {
        if (!_process.HasExited)
            _process.Kill(entireProcessTree: true);
        _process.Dispose();
        _timeout.Dispose();
    }

    /// <summary>The frame's visible text (full or row-level delta), rows joined with newlines.</summary>
    public static string FrameText(FrameEvent frame) => frame.Delta == true
        ? string.Join('\n', (frame.Changed ?? []).Select(c => string.Concat(c.Runs.Select(r => r.Text))))
        : string.Join('\n', frame.Lines.Select(runs => string.Concat(runs.Select(r => r.Text))));

    /// <summary>
    /// Reads a fixture path baked into the test assembly by the csproj (AssemblyMetadata),
    /// normalized to a full path with no trailing separator.
    /// </summary>
    public static string FixturePath(string metadataKey)
    {
        var value = typeof(HostProcessHarness).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == metadataKey)?.Value;
        Assert.False(string.IsNullOrEmpty(value), $"AssemblyMetadata '{metadataKey}' is missing from the test assembly.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value!));
    }
}
