using System.Reflection;

using Cursorial.Designer.Protocol;

namespace Cursorial.Designer.Tests.PreviewHost;

/// <summary>
/// The task's headline demo, end-to-end: the split host spawned with a user directory previews
/// the USER's Cursorial assemblies. The fixture (<c>Cursorial.Designer.PreviewHost.Tests.UserApp</c>)
/// is a real project build output, not a mock; the checkout test builds a miniature framework
/// repo layout on disk to prove the G5 probe.
/// </summary>
public class UserAssembliesEndToEndTests
{
    [Fact]
    public async Task User_dir_session_previews_user_control_on_user_framework()
    {
        var userDir = HostProcessHarness.FixturePath("UserAppOutputDirectory");
        Assert.True(
            File.Exists(Path.Combine(userDir, "Cursorial.Core.dll")),
            $"Fixture output at {userDir} is missing its framework copy — the build-ordering reference broke.");

        using var host = new HostProcessHarness("--user-dir", userDir);

        var ready = Assert.IsType<ReadyEvent>(await host.NextEvent());
        Assert.Equal("user", ready.FrameworkSource);
        Assert.Equal(userDir, ready.FrameworkPath);
        Assert.Null(ready.FallbackReason);
        var userCoreVersion = AssemblyName
            .GetAssemblyName(Path.Combine(userDir, "Cursorial.Core.dll")).Version?.ToString();
        Assert.Equal(userCoreVersion, ready.FrameworkVersion);
        Assert.NotNull(ready.FrameworkVersion);

        await host.Send(new InitializeCommand { ProtocolVersion = 1, Columns = 40, Rows = 10 });
        Assert.IsType<FrameEvent>(await host.NextEvent());

        await host.Send(new LoadXamlCommand
        {
            Id = 1,
            Xaml = """
                   <StackPanel xmlns="https://cursorial.dev/ui" xmlns:x="https://cursorial.dev/xaml"
                               xmlns:user="clr-namespace:Cursorial.Designer.Tests.UserApp;assembly=Cursorial.Designer.PreviewHost.Tests.UserApp">
                       <user:UserBadge/>
                   </StackPanel>
                   """,
            Assemblies = [Path.Combine(userDir, "Cursorial.Designer.PreviewHost.Tests.UserApp.dll")],
        });

        Assert.IsType<DependenciesEvent>(await host.NextEvent());
        var diagnostics = Assert.IsType<DiagnosticsEvent>(await host.NextEvent());
        Assert.Empty(diagnostics.Items);

        var frame = Assert.IsType<FrameEvent>(await host.NextEvent());
        Assert.Contains("user badge", HostProcessHarness.FrameText(frame));

        // Hit-testing the badge's cell reports the USER-DEFINED type — the user assembly and
        // the framework it derives from are one graph in one context.
        await host.Send(new HitTestCommand { Id = 2, Column = 1, Row = 0 });
        var hit = Assert.IsType<HitTestResultEvent>(await host.NextEvent());
        Assert.Contains(hit.Elements, element => element.ElementType == "UserBadge");

        // The framework itself came from the user's output: the launcher narrates every
        // non-bundle resolution, and Cursorial.UI must be among them.
        Assert.Contains(host.Logs, log =>
            log.Message != null &&
            log.Message.Contains("Resolved Cursorial.UI from user output") &&
            log.Message.Contains(userDir));

        await host.ShutdownCleanly();
    }

    [Fact]
    public async Task Framework_checkout_outputs_supply_host_only_assemblies()
    {
        // A miniature framework checkout: the repo-root signature directory, per-project build
        // outputs for the two HOST-ONLY assemblies (the ones a real user output never carries),
        // and an app inside the checkout whose output has the app-shaped closure. All real
        // assemblies — copied from this test's output, which holds the full framework.
        var root = Path.Combine(Path.GetTempPath(), "cursorial-designer-checkout-" + Guid.NewGuid().ToString("N"));
        string[] hostOnly = ["Cursorial.UI.Themes", "Cursorial.UI.Hosting.Headless"];
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Cursorial.Core"));
            File.WriteAllText(
                Path.Combine(root, "Cursorial.Core", "Cursorial.Core.csproj"),
                """<Project Sdk="Microsoft.NET.Sdk" />""");

            foreach (var name in hostOnly)
            {
                var outputDir = Path.Combine(root, name, "bin", "Debug", "net10.0");
                Directory.CreateDirectory(outputDir);
                File.Copy(
                    Path.Combine(AppContext.BaseDirectory, name + ".dll"),
                    Path.Combine(outputDir, name + ".dll"));
            }

            // The app output: every framework assembly EXCEPT the host-only set (the exact
            // shape of a real user output, verified against Cursorial.Samples in the scoping).
            var userDir = Path.Combine(root, "DemoApp", "bin", "Debug", "net10.0");
            Directory.CreateDirectory(userDir);
            foreach (var dll in Directory.EnumerateFiles(AppContext.BaseDirectory, "Cursorial.*.dll"))
            {
                var name = Path.GetFileNameWithoutExtension(dll);
                if (name.StartsWith("Cursorial.Designer.", StringComparison.Ordinal))
                    continue;
                if (name is "Cursorial.UI.Themes" or "Cursorial.UI.Hosting.Headless" or "Cursorial.UI.DataViews")
                    continue;
                File.Copy(dll, Path.Combine(userDir, Path.GetFileName(dll)));
            }

            using var host = new HostProcessHarness("--user-dir", userDir);

            var ready = Assert.IsType<ReadyEvent>(await host.NextEvent());
            Assert.Equal("user", ready.FrameworkSource);
            Assert.Equal(userDir, ready.FrameworkPath);

            // Rendering REQUIRES Hosting.Headless (the engine): a frame proves it resolved.
            await host.Send(new InitializeCommand { ProtocolVersion = 1, Columns = 40, Rows = 10 });
            Assert.IsType<FrameEvent>(await host.NextEvent());

            await host.Send(new LoadXamlCommand
            {
                Id = 1,
                Xaml = """
                       <StackPanel xmlns="https://cursorial.dev/ui" xmlns:x="https://cursorial.dev/xaml">
                           <TextBlock Text="checkout works"/>
                       </StackPanel>
                       """,
            });
            Assert.IsType<DependenciesEvent>(await host.NextEvent());
            var diagnostics = Assert.IsType<DiagnosticsEvent>(await host.NextEvent());
            Assert.Empty(diagnostics.Items);
            var frame = Assert.IsType<FrameEvent>(await host.NextEvent());
            Assert.Contains("checkout works", HostProcessHarness.FrameText(frame));

            // Themes is LAZY — today's session never touches the assembly unless a document
            // references it (ThemeKeys are consts in Cursorial.UI; the built-in theme is
            // code-first). Reference it through its clr-namespace to force the bind. The
            // x:Static member cannot resolve (Themes ships only loader METHODS, nothing
            // XAML-instantiable), so ONE diagnostic is expected — but the assembly must load
            // to discover that, and that load is the probe's proof.
            await host.Send(new LoadXamlCommand
            {
                Id = 2,
                Xaml = """
                       <StackPanel xmlns="https://cursorial.dev/ui" xmlns:x="https://cursorial.dev/xaml"
                                   xmlns:themes="clr-namespace:Cursorial.UI.Themes.Default;assembly=Cursorial.UI.Themes">
                           <TextBlock Text="{x:Static themes:CursorialDefaultTheme.LoadTheme}"/>
                       </StackPanel>
                       """,
            });
            Assert.IsType<DependenciesEvent>(await host.NextEvent());
            var themesDiagnostics = Assert.IsType<DiagnosticsEvent>(await host.NextEvent());
            var item = Assert.Single(themesDiagnostics.Items);
            Assert.Contains("x:Static", item.Message);

            // Both host-only assemblies resolved from the CHECKOUT's per-project outputs, not
            // the bundle — the framework-source workflow the probe exists for.
            foreach (var name in hostOnly)
            {
                var probed = Path.Combine(root, name, "bin", "Debug", "net10.0", name + ".dll");
                Assert.Contains(host.Logs, log =>
                    log.Message == $"Resolved {name} from framework checkout output: {probed}");
            }

            // The app's own closure still resolves from the app's output, not the checkout's.
            Assert.Contains(host.Logs, log =>
                log.Message != null && log.Message.Contains("Resolved Cursorial.Core from user output"));

            await host.ShutdownCleanly();
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // Best effort — a leaked temp directory must not fail the test.
            }
        }
    }
}
