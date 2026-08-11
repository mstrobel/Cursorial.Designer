using System.Reflection;
using System.Runtime.Loader;

using Cursorial.Designer.Protocol;

namespace Cursorial.Designer.PreviewHost;

/// <summary>
/// Loads the framework-bound core (<c>Cursorial.Designer.PreviewHost.Core</c>) into its own
/// non-collectible <see cref="AssemblyLoadContext"/> and hands back the Protocol-typed session
/// seam. The split exists so that exactly ONE copy of every <c>Cursorial.*</c> assembly exists
/// per session: the core and the framework (and, in later slices, the user's assemblies) all
/// resolve inside that one context, while the Protocol types the launcher exchanges with the
/// core stay in the default context — reference-unified on both sides of the boundary.
/// </summary>
internal static class CoreLoader
{
    /// <summary>
    /// The core assembly's simple name. Not a compile-time reference by design: the launcher
    /// must stay framework-free, so the core is known here only as a file next to the launcher.
    /// </summary>
    private const string CoreAssemblyName = "Cursorial.Designer.PreviewHost.Core";

    /// <summary>The core's entry type: its single public type, implementing <see cref="ISessionCore"/>.</summary>
    private const string SessionCoreTypeName = "Cursorial.Designer.PreviewHost.SessionCore";

    /// <summary>
    /// Builds the session's load context over the bundled directory (the launcher's own — this
    /// slice is bundled-only), loads the core into it, and activates the session seam. Called on
    /// the command-loop thread, which thereby stays the UI thread exactly as before the split.
    /// </summary>
    public static ISessionCore CreateSession(Action<PreviewEvent> emit)
    {
        var bundleDirectory = AppContext.BaseDirectory;
        var context = new PreviewLoadContext(bundleDirectory);
        var core = context.LoadFromAssemblyPath(Path.Combine(bundleDirectory, CoreAssemblyName + ".dll"));
        var entryType = core.GetType(SessionCoreTypeName, throwOnError: true)!;
        return (ISessionCore)Activator.CreateInstance(entryType, emit)!;
    }
}

/// <summary>
/// The load context that owns everything framework-bound. Resolution policy: Protocol falls
/// through to the default context (the type-identity bridge for the seam), <c>Cursorial.*</c>
/// loads from the bundled directory into THIS context, and BCL/shared-framework names fall
/// through to the default context so runtime types are never duplicated.
/// </summary>
internal sealed class PreviewLoadContext : AssemblyLoadContext
{
    private static readonly string ProtocolAssemblyName = typeof(PreviewProtocol).Assembly.GetName().Name!;

    private readonly string _bundleDirectory;

    public PreviewLoadContext(string bundleDirectory)
        : base(name: "cursorial-preview", isCollectible: false)
    {
        _bundleDirectory = bundleDirectory;
        Resolving += ResolveBundledDependency;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;

        // Protocol types ARE the boundary: the commands and events crossing between the launcher
        // and the core must agree on identity, and falling through to the default context (where
        // the launcher itself binds Protocol) is the mechanism that unifies them.
        if (name is null || name == ProtocolAssemblyName)
            return null;

        // Everything Cursorial-flavored — the core and the framework — belongs to this context.
        if (name.StartsWith("Cursorial.", StringComparison.Ordinal))
        {
            var path = Path.Combine(_bundleDirectory, name + ".dll");
            if (File.Exists(path))
                return LoadFromAssemblyPath(path);
        }

        // BCL and shared-framework assemblies: default context.
        return null;
    }

    /// <summary>
    /// Reached only after both <see cref="Load"/> and the default-context fallback declined:
    /// third-party dependencies that ship in the bundle but are outside the launcher's own
    /// dependency closure (e.g. <c>Microsoft.Extensions.TimeProvider.Testing</c>, the headless
    /// host's time source). Shared-framework assemblies never get this far — the default context
    /// supplies them first — so nothing runtime-owned is ever duplicated into this context.
    /// </summary>
    private Assembly? ResolveBundledDependency(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        if (assemblyName.Name is { } name)
        {
            var path = Path.Combine(_bundleDirectory, name + ".dll");
            if (File.Exists(path))
                return LoadFromAssemblyPath(path);
        }

        return null;
    }
}
