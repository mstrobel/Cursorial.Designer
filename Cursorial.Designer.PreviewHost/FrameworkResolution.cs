using System.Reflection;

namespace Cursorial.Designer.PreviewHost;

/// <summary>
/// The launcher's spawn-time decision about where the Cursorial framework comes from, computed
/// from assembly METADATA only (no assembly ever loads here) so it is available before the
/// session's load context exists — the <c>ready</c> event reports it, and the load context is
/// built from it. Deterministic per spawn by design: the plugin restarts the host when the
/// user's build output changes, so a resolution never has to change mid-process.
/// </summary>
internal sealed class FrameworkResolution
{
    /// <summary>The assembly whose vintage stands for the framework's: the root every other Cursorial.* depends on.</summary>
    internal const string GateAssemblyName = "Cursorial.Core";

    internal const string UserSource = "user";
    internal const string BundledSource = "bundled";

    /// <summary>The launcher's own directory — the designer-shipped framework copies.</summary>
    public required string BundleDirectory { get; init; }

    /// <summary><see cref="UserSource"/> or <see cref="BundledSource"/>; mirrors <c>ready.frameworkSource</c>.</summary>
    public required string FrameworkSource { get; init; }

    /// <summary>The directory <c>Cursorial.Core</c> resolves from (no trailing separator); mirrors <c>ready.frameworkPath</c>.</summary>
    public required string FrameworkDirectory { get; init; }

    /// <summary>The resolved framework's <c>Cursorial.Core</c> version; mirrors <c>ready.frameworkVersion</c>.</summary>
    public string? FrameworkVersion { get; init; }

    /// <summary>Why the user's framework was present but not used; mirrors <c>ready.fallbackReason</c>.</summary>
    public string? FallbackReason { get; init; }

    /// <summary>
    /// A user directory was given but held no assemblies (never built, or cleaned); mirrors
    /// <c>ready.userDirMissing</c>. Disjoint from <see cref="FallbackReason"/> by design — the
    /// IDE renders this condition louder (dismissible cue) than the quiet wrong-vintage note.
    /// </summary>
    public bool UserDirMissing { get; init; }

    /// <summary>
    /// The user project's build-output directory (no trailing separator) when the per-assembly
    /// preference is ON — the directory exists, holds assemblies, and the version gate did not
    /// fail. Null means bundled-only resolution: no user probing anywhere.
    /// </summary>
    public string? UserDirectory { get; init; }

    /// <summary>
    /// Set when <see cref="UserDirectory"/> sits inside a Cursorial framework source checkout:
    /// the checkout's own per-project build outputs are probed for assemblies the user output
    /// lacks (host-only assemblies like Themes and Hosting.Headless), so framework-source
    /// development previews against the source tree's bits rather than the bundle's.
    /// </summary>
    public CheckoutProbe? Checkout { get; init; }

    /// <summary>
    /// Resolves the session's framework source. The gate is a FLOATING FLOOR: the user's copies
    /// win when their <see cref="GateAssemblyName"/> is at least as new as the bundle's — the
    /// minimum rises with every designer release instead of a constant going stale.
    /// </summary>
    public static FrameworkResolution Resolve(string bundleDirectory, string? userDirectory = null)
    {
        bundleDirectory = Path.TrimEndingDirectorySeparator(bundleDirectory);
        var bundledVersion = ReadVersion(Path.Combine(bundleDirectory, GateAssemblyName + ".dll"));

        FrameworkResolution Bundled(string? fallbackReason = null, bool userDirMissing = false) => new()
        {
            BundleDirectory = bundleDirectory,
            FrameworkSource = BundledSource,
            FrameworkDirectory = bundleDirectory,
            FrameworkVersion = bundledVersion?.ToString(),
            FallbackReason = fallbackReason,
            UserDirMissing = userDirMissing,
        };

        if (userDirectory is null)
            return Bundled();

        userDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(userDirectory));

        // Missing or assembly-less directory: the project was never built (or was cleaned).
        // Bundled-only — there is nothing to prefer and nothing to gate — but flagged: user
        // types will silently fail to resolve until a build exists, and the IDE must be able
        // to say so.
        if (!Directory.Exists(userDirectory) || !Directory.EnumerateFiles(userDirectory, "*.dll").Any())
            return Bundled(userDirMissing: true);

        var userCorePath = Path.Combine(userDirectory, GateAssemblyName + ".dll");

        // An output with assemblies but no framework copy: the project doesn't ship Cursorial
        // (framework-free library, or an exotic reference shape). Nothing to gate — preference
        // stays ON so the project's own satellite assemblies resolve, and every framework name
        // falls through to the bundle by simple absence.
        if (!File.Exists(userCorePath))
        {
            return new FrameworkResolution
            {
                BundleDirectory = bundleDirectory,
                FrameworkSource = BundledSource,
                FrameworkDirectory = bundleDirectory,
                FrameworkVersion = bundledVersion?.ToString(),
                UserDirectory = userDirectory,
                Checkout = CheckoutProbe.Detect(userDirectory),
            };
        }

        var userVersion = ReadVersion(userCorePath);
        if (userVersion is null)
        {
            return Bundled(
                $"Previewing with the designer's Cursorial {bundledVersion?.ToString() ?? "(unknown)"} — " +
                $"the version of this project's {GateAssemblyName}.dll could not be read.");
        }

        // The gate. A user framework OLDER than the bundle predates seams the host requires;
        // silent fallback is the one thing the design forbids, so the reason rides the ready
        // payload. (An unreadable BUNDLED version means the designer's own payload is broken —
        // trust the user's copies rather than refuse.)
        if (bundledVersion is not null && userVersion < bundledVersion)
        {
            return Bundled(
                $"Previewing with the designer's Cursorial {bundledVersion} — " +
                $"this project targets the older {userVersion}.");
        }

        return new FrameworkResolution
        {
            BundleDirectory = bundleDirectory,
            FrameworkSource = UserSource,
            FrameworkDirectory = userDirectory,
            FrameworkVersion = userVersion.ToString(),
            UserDirectory = userDirectory,
            Checkout = CheckoutProbe.Detect(userDirectory),
        };
    }

    /// <summary>
    /// Metadata-only version read — <see cref="AssemblyName.GetAssemblyName(string)"/> parses
    /// the file without loading it into any context. Null (rather than a launch failure) for a
    /// missing or unreadable file: the report degrades, the session does not.
    /// </summary>
    private static Version? ReadVersion(string assemblyPath)
    {
        try
        {
            return AssemblyName.GetAssemblyName(assemblyPath).Version;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>
/// The framework checkout's own per-project build outputs (ruling G5). Detection requires the
/// user directory to have the SDK output shape <c>…/bin/&lt;Configuration&gt;/&lt;tfm&gt;</c>
/// and an ancestor directory carrying the framework repo root's signature
/// (<c>Cursorial.Core/Cursorial.Core.csproj</c>); the probe then mirrors the user output's own
/// configuration and target framework into <c>&lt;root&gt;/&lt;assembly&gt;/bin/…</c>, so a
/// Debug app never picks up Release framework bits or another TFM's.
/// </summary>
internal sealed class CheckoutProbe
{
    public required string Root { get; init; }

    public required string Configuration { get; init; }

    public required string TargetFramework { get; init; }

    /// <summary>Detects a framework checkout above <paramref name="userDirectory"/>; null when the layout doesn't match.</summary>
    public static CheckoutProbe? Detect(string userDirectory)
    {
        var tfmDirectory = new DirectoryInfo(userDirectory);
        var configurationDirectory = tfmDirectory.Parent;
        var binDirectory = configurationDirectory?.Parent;
        if (binDirectory is null || !string.Equals(binDirectory.Name, "bin", StringComparison.OrdinalIgnoreCase))
            return null;

        for (var ancestor = binDirectory.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (File.Exists(Path.Combine(ancestor.FullName, "Cursorial.Core", "Cursorial.Core.csproj")))
            {
                return new CheckoutProbe
                {
                    Root = ancestor.FullName,
                    Configuration = configurationDirectory!.Name,
                    TargetFramework = tfmDirectory.Name,
                };
            }
        }

        return null;
    }

    /// <summary>The checkout's built output for the assembly, when it exists; null otherwise (per-assembly bundle fallback).</summary>
    public string? ProbeFor(string assemblyName)
    {
        var path = Path.Combine(Root, assemblyName, "bin", Configuration, TargetFramework, assemblyName + ".dll");
        return File.Exists(path) ? path : null;
    }
}
