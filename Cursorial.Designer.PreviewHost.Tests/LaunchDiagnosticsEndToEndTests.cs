using Cursorial.Designer.Protocol;

namespace Cursorial.Designer.Tests.PreviewHost;

/// <summary>
/// The three degradable launch conditions, distinguished deterministically on the ready payload
/// through the real spawned host (see docs/protocol.md, "Framework report"):
/// (a) user dir given but missing/empty → <c>userDirMissing</c>, the IDE's dismissible-cue
/// condition; (b) user dir present but gate-failed → <c>fallbackReason</c>, the quiet-note
/// condition; (c) healthy user dir → <c>frameworkSource == "user"</c>, nothing to narrate.
/// The discriminators are DISJOINT — each condition sets exactly one of them.
/// </summary>
public class LaunchDiagnosticsEndToEndTests
{
    [Fact]
    public async Task Missing_user_dir_flags_the_cue_condition_and_still_renders()
    {
        var missing = Path.Combine(Path.GetTempPath(), "cursorial-never-built-" + Guid.NewGuid().ToString("N"));
        using var host = new HostProcessHarness("--user-dir", missing);

        var ready = Assert.IsType<ReadyEvent>(await host.NextEvent());
        Assert.True(ready.UserDirMissing);
        Assert.Equal("bundled", ready.FrameworkSource);
        Assert.Null(ready.FallbackReason); // the cue and the quiet note never fire together

        // The preview still works — framework-only markup previews fine before a first build.
        await host.Send(new InitializeCommand { ProtocolVersion = 1, Columns = 40, Rows = 10 });
        Assert.IsType<FrameEvent>(await host.NextEvent());

        await host.Send(new LoadXamlCommand
        {
            Id = 1,
            Xaml = """
                   <StackPanel xmlns="https://cursorial.dev/ui" xmlns:x="https://cursorial.dev/xaml">
                       <TextBlock Text="not built yet"/>
                   </StackPanel>
                   """,
        });
        Assert.IsType<DependenciesEvent>(await host.NextEvent());
        Assert.IsType<DiagnosticsEvent>(await host.NextEvent());
        var frame = Assert.IsType<FrameEvent>(await host.NextEvent());
        Assert.Contains("not built yet", HostProcessHarness.FrameText(frame));

        await host.ShutdownCleanly();
    }

    [Fact]
    public async Task Empty_user_dir_is_the_same_cue_condition()
    {
        // An existing directory with no assemblies (e.g. right after a clean) is
        // indistinguishable from a never-built one for the user: same flag.
        var empty = Directory.CreateTempSubdirectory("cursorial-cleaned-").FullName;
        try
        {
            using var host = new HostProcessHarness("--user-dir", empty);

            var ready = Assert.IsType<ReadyEvent>(await host.NextEvent());
            Assert.True(ready.UserDirMissing);
            Assert.Equal("bundled", ready.FrameworkSource);
            Assert.Null(ready.FallbackReason);

            await host.ShutdownCleanly();
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    [Fact]
    public async Task Gate_failure_is_the_quiet_note_condition_not_the_cue()
    {
        var fakeOldDir = HostProcessHarness.FixturePath("FakeOldCoreOutputDirectory");
        using var host = new HostProcessHarness("--user-dir", fakeOldDir);

        var ready = Assert.IsType<ReadyEvent>(await host.NextEvent());
        Assert.NotNull(ready.FallbackReason);
        Assert.Null(ready.UserDirMissing); // omitted from the wire, not false
        Assert.Equal("bundled", ready.FrameworkSource);

        await host.ShutdownCleanly();
    }

    [Fact]
    public async Task Healthy_user_dir_reports_source_user_with_no_degradation_fields()
    {
        var userDir = HostProcessHarness.FixturePath("UserAppOutputDirectory");
        using var host = new HostProcessHarness("--user-dir", userDir);

        var ready = Assert.IsType<ReadyEvent>(await host.NextEvent());
        Assert.Equal("user", ready.FrameworkSource);
        Assert.Null(ready.FallbackReason);
        Assert.Null(ready.UserDirMissing);

        await host.ShutdownCleanly();
    }
}
