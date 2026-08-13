using System.Reflection;

using Cursorial.Designer.PreviewHost;

namespace Cursorial.Designer.Tests.PreviewHost;

/// <summary>
/// The launcher's spawn-time framework resolution, unit-level: metadata-only reads, the
/// per-assembly preference switch, and the framework-checkout probe's layout detection.
/// (The version gate's fail path is covered with its own fixture in the gate slice; the E2E
/// suites cover the same decisions through the real spawned host.)
/// </summary>
public class FrameworkResolutionTests
{
    private static string BundleDir => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    [Fact]
    public void No_user_dir_resolves_bundled()
    {
        var resolution = FrameworkResolution.Resolve(AppContext.BaseDirectory);

        Assert.Equal("bundled", resolution.FrameworkSource);
        Assert.Equal(BundleDir, resolution.FrameworkDirectory);
        Assert.Null(resolution.UserDirectory);
        Assert.Null(resolution.Checkout);
        Assert.Null(resolution.FallbackReason);

        var bundledVersion = AssemblyName
            .GetAssemblyName(Path.Combine(BundleDir, "Cursorial.Core.dll")).Version?.ToString();
        Assert.Equal(bundledVersion, resolution.FrameworkVersion);
    }

    [Fact]
    public void Healthy_user_dir_wins_the_gate_and_turns_preference_on()
    {
        // The fixture ships the same framework vintage as the bundle — "user >= bundled" holds.
        var userDir = HostProcessHarness.FixturePath("UserAppOutputDirectory");

        var resolution = FrameworkResolution.Resolve(AppContext.BaseDirectory, userDir);

        Assert.Equal("user", resolution.FrameworkSource);
        Assert.Equal(userDir, resolution.FrameworkDirectory);
        Assert.Equal(userDir, resolution.UserDirectory);
        Assert.Null(resolution.FallbackReason);
        Assert.False(resolution.UserDirMissing);
        Assert.Null(resolution.Checkout); // the designer repo is not a framework checkout
    }

    [Fact]
    public void Older_user_core_fails_the_gate_and_disables_preference()
    {
        // The FakeOldCore fixture: a blank assembly stamped Cursorial.Core 0.1.0.0 — identity
        // and version are all the metadata-only gate reads.
        var fakeOldDir = HostProcessHarness.FixturePath("FakeOldCoreOutputDirectory");

        var resolution = FrameworkResolution.Resolve(AppContext.BaseDirectory, fakeOldDir);

        Assert.Equal("bundled", resolution.FrameworkSource);
        Assert.Equal(BundleDir, resolution.FrameworkDirectory);
        Assert.Null(resolution.UserDirectory); // bundled-ONLY: preference off everywhere
        Assert.Null(resolution.Checkout);
        Assert.NotNull(resolution.FallbackReason);
        Assert.Contains("0.1.0.0", resolution.FallbackReason);
        Assert.Contains("older", resolution.FallbackReason);
        Assert.False(resolution.UserDirMissing); // the gate note and the not-built cue are disjoint
    }

    [Fact]
    public void Missing_user_dir_resolves_bundled_only()
    {
        var resolution = FrameworkResolution.Resolve(
            AppContext.BaseDirectory,
            Path.Combine(Path.GetTempPath(), "cursorial-does-not-exist-" + Guid.NewGuid().ToString("N")));

        Assert.Equal("bundled", resolution.FrameworkSource);
        Assert.Null(resolution.UserDirectory);
        Assert.True(resolution.UserDirMissing);
        Assert.Null(resolution.FallbackReason);
    }

    [Fact]
    public void Empty_user_dir_resolves_bundled_only()
    {
        var empty = Directory.CreateTempSubdirectory("cursorial-empty-").FullName;
        try
        {
            var resolution = FrameworkResolution.Resolve(AppContext.BaseDirectory, empty);

            Assert.Equal("bundled", resolution.FrameworkSource);
            Assert.Null(resolution.UserDirectory);
            Assert.True(resolution.UserDirMissing);
            Assert.Null(resolution.FallbackReason);
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    [Fact]
    public void User_dir_without_core_keeps_preference_but_reports_bundled()
    {
        // A framework-free output: assemblies present, no Cursorial.Core to gate on. The
        // project's own dlls must still resolve (preference on) while the framework report
        // stays honest about where Cursorial.Core comes from.
        var dir = Directory.CreateTempSubdirectory("cursorial-no-core-").FullName;
        try
        {
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, "Cursorial.Designer.Protocol.dll"),
                Path.Combine(dir, "SomeUserLibrary.dll"));

            var resolution = FrameworkResolution.Resolve(AppContext.BaseDirectory, dir);

            Assert.Equal("bundled", resolution.FrameworkSource);
            Assert.Equal(BundleDir, resolution.FrameworkDirectory);
            Assert.Equal(Path.TrimEndingDirectorySeparator(dir), resolution.UserDirectory);
            Assert.Null(resolution.FallbackReason);
            Assert.False(resolution.UserDirMissing); // assemblies exist; nothing is missing
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Checkout_probe_detects_framework_repo_layout()
    {
        var root = Directory.CreateTempSubdirectory("cursorial-checkout-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Cursorial.Core"));
            File.WriteAllText(
                Path.Combine(root, "Cursorial.Core", "Cursorial.Core.csproj"),
                """<Project Sdk="Microsoft.NET.Sdk" />""");

            var themesOutput = Path.Combine(root, "Cursorial.UI.Themes", "bin", "Debug", "net10.0");
            Directory.CreateDirectory(themesOutput);
            File.WriteAllBytes(Path.Combine(themesOutput, "Cursorial.UI.Themes.dll"), [0]);

            var userDir = Path.Combine(root, "DemoApp", "bin", "Debug", "net10.0");
            Directory.CreateDirectory(userDir);

            var probe = CheckoutProbe.Detect(userDir);
            Assert.NotNull(probe);
            Assert.Equal(root, probe.Root);
            Assert.Equal("Debug", probe.Configuration);
            Assert.Equal("net10.0", probe.TargetFramework);

            // Present output → the probed path; absent output → null (per-assembly bundle fallback).
            Assert.Equal(
                Path.Combine(themesOutput, "Cursorial.UI.Themes.dll"),
                probe.ProbeFor("Cursorial.UI.Themes"));
            Assert.Null(probe.ProbeFor("Cursorial.UI.Hosting.Headless"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Checkout_probe_requires_the_sdk_output_shape()
    {
        // Without .../bin/<Configuration>/<tfm> there is no configuration/TFM to mirror — the
        // probe must stay off rather than guess.
        var root = Directory.CreateTempSubdirectory("cursorial-shapeless-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Cursorial.Core"));
            File.WriteAllText(
                Path.Combine(root, "Cursorial.Core", "Cursorial.Core.csproj"),
                """<Project Sdk="Microsoft.NET.Sdk" />""");

            var flatDir = Path.Combine(root, "DemoApp", "output");
            Directory.CreateDirectory(flatDir);

            Assert.Null(CheckoutProbe.Detect(flatDir));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
