using System.Reflection;

using Cursorial.Designer.Protocol;

namespace Cursorial.Designer.Tests.PreviewHost;

/// <summary>
/// The G4 version gate through the real spawned host: a user directory whose
/// <c>Cursorial.Core</c> is STAMPED older than the bundle (the FakeOldCore fixture — a blank
/// assembly with the right identity, which is all the metadata-only gate ever reads) must fall
/// back to the bundled framework, say so on the ready payload, and still render.
/// </summary>
public class VersionGateEndToEndTests
{
    [Fact]
    public async Task Older_user_framework_falls_back_to_bundle_with_reason_and_still_renders()
    {
        var fakeOldDir = HostProcessHarness.FixturePath("FakeOldCoreOutputDirectory");
        var fakeCorePath = Path.Combine(fakeOldDir, "Cursorial.Core.dll");
        Assert.True(File.Exists(fakeCorePath), $"FakeOldCore fixture output missing at {fakeCorePath}.");
        Assert.Equal(new Version(0, 1, 0, 0), AssemblyName.GetAssemblyName(fakeCorePath).Version);

        var bundleDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var bundledVersion = AssemblyName
            .GetAssemblyName(Path.Combine(bundleDir, "Cursorial.Core.dll")).Version!.ToString();

        using var host = new HostProcessHarness("--user-dir", fakeOldDir);

        var ready = Assert.IsType<ReadyEvent>(await host.NextEvent());
        Assert.Equal("bundled", ready.FrameworkSource);
        Assert.Equal(bundleDir, ready.FrameworkPath);
        Assert.Equal(bundledVersion, ready.FrameworkVersion);
        Assert.NotNull(ready.FallbackReason);
        Assert.Contains("0.1.0.0", ready.FallbackReason);
        Assert.Contains(bundledVersion, ready.FallbackReason);

        // Gate failure means bundled-ONLY: nothing may resolve from the user directory.
        Assert.DoesNotContain(host.Logs, log =>
            log.Message != null && log.Message.Contains(fakeOldDir));

        // The session is degraded, not dead: it initializes, loads, and renders on the bundle.
        await host.Send(new InitializeCommand { ProtocolVersion = 1, Columns = 40, Rows = 10 });
        Assert.IsType<FrameEvent>(await host.NextEvent());

        await host.Send(new LoadXamlCommand
        {
            Id = 1,
            Xaml = """
                   <StackPanel xmlns="https://cursorial.dev/ui" xmlns:x="https://cursorial.dev/xaml">
                       <TextBlock Text="gate fell back"/>
                   </StackPanel>
                   """,
        });
        Assert.IsType<DependenciesEvent>(await host.NextEvent());
        var diagnostics = Assert.IsType<DiagnosticsEvent>(await host.NextEvent());
        Assert.Empty(diagnostics.Items);
        var frame = Assert.IsType<FrameEvent>(await host.NextEvent());
        Assert.Contains("gate fell back", HostProcessHarness.FrameText(frame));

        // Post-session re-check: no user-directory resolution ever happened.
        Assert.DoesNotContain(host.Logs, log =>
            log.Message != null && log.Message.Contains(fakeOldDir));

        await host.ShutdownCleanly();
    }
}
