using System.Reflection;
using System.Runtime.Loader;

using Cursorial.Designer.Protocol;

namespace Cursorial.Designer.PreviewHost;

/// <summary>
/// Loads the framework-bound core (<c>Cursorial.Designer.PreviewHost.Core</c>) into its own
/// non-collectible <see cref="AssemblyLoadContext"/> and hands back the Protocol-typed session
/// seam. The split exists so that exactly ONE copy of every <c>Cursorial.*</c> assembly exists
/// per session: the core, the framework, and the user's assemblies all resolve inside that one
/// context — sourced per-assembly from the user's build output, a framework checkout's own
/// outputs, or the bundle, per the spawn-time <see cref="FrameworkResolution"/> — while the
/// Protocol types the launcher exchanges with the core stay in the default context,
/// reference-unified on both sides of the boundary.
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
    /// Builds the session's load context from <paramref name="resolution"/> (defaulting to a
    /// bundled-only resolution over the launcher's own directory), loads the core into it, and
    /// activates the session seam. Called on the command-loop thread, which thereby stays the
    /// UI thread exactly as before the split.
    /// </summary>
    public static ISessionCore CreateSession(Action<PreviewEvent> emit, FrameworkResolution? resolution = null)
    {
        resolution ??= FrameworkResolution.Resolve(AppContext.BaseDirectory);
        var context = new PreviewLoadContext(resolution, emit);

        // The reverse bridge. Name-based resolution that STARTS in a default-context frame —
        // TypeDescriptor reading a [TypeConverter] attribute's assembly-qualified string is the
        // canonical case — never consults this context's Load, and the launcher's TPA
        // deliberately lacks the framework, so without help the lookup FAILS and the caller
        // degrades silently (a gesture string reaches SetValue unconverted). Handing back this
        // context's copy UNIFIES identity instead of duplicating it; Protocol and non-Cursorial
        // names are declined so nothing new leaks into the session context. One session per
        // process (the plugin spawns a host per preview tab), so the subscription lives for the
        // process lifetime by design.
        AssemblyLoadContext.Default.Resolving += (_, name) => context.TryLoadCursorial(name);

        // The core is designer-owned and never appears in user output: it always loads from the
        // bundle, into the session context — its Cursorial.* references then resolve through the
        // context's per-assembly preference.
        var core = context.LoadFromAssemblyPath(Path.Combine(resolution.BundleDirectory, CoreAssemblyName + ".dll"));
        var entryType = core.GetType(SessionCoreTypeName, throwOnError: true)!;
        return (ISessionCore)Activator.CreateInstance(entryType, emit)!;
    }
}

/// <summary>
/// The load context that owns everything framework-bound. Resolution policy: Protocol falls
/// through to the default context (the type-identity bridge for the seam), <c>Cursorial.*</c>
/// loads into THIS context — preferring the user's build output per-assembly, then a framework
/// checkout's own outputs, then the bundled copies — and BCL/shared-framework names fall
/// through to the default context so runtime types are never duplicated.
/// </summary>
internal sealed class PreviewLoadContext : AssemblyLoadContext
{
    private static readonly string ProtocolAssemblyName = typeof(PreviewProtocol).Assembly.GetName().Name!;

    private readonly FrameworkResolution _resolution;
    private readonly Action<PreviewEvent>? _emit;

    public PreviewLoadContext(FrameworkResolution resolution, Action<PreviewEvent>? emit = null)
        : base(name: "cursorial-preview", isCollectible: false)
    {
        _resolution = resolution;
        _emit = emit;
        Resolving += ResolveFallbackDependency;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // Everything Cursorial-flavored — the core, the framework, the user's assemblies —
        // belongs to this context; BCL and shared-framework assemblies fall through to the
        // default context.
        return TryLoadCursorial(assemblyName);
    }

    /// <summary>
    /// The one resolution rule, shared by <see cref="Load"/> (requests arriving IN this context)
    /// and the launcher's <see cref="AssemblyLoadContext.Default"/>.Resolving bridge (name-based
    /// requests arriving in a default-context frame, which the launcher's framework-free TPA
    /// cannot satisfy): a <c>Cursorial.*</c> name loads into THIS context from the first source
    /// that has it — user output, framework checkout output, bundle — except Protocol, whose
    /// types ARE the boundary and must stay where the launcher binds them, in the default
    /// context, which is the mechanism that unifies them on both sides. Returns null rather
    /// than throwing for anything it does not own.
    /// </summary>
    internal Assembly? TryLoadCursorial(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;

        if (name is null || name == ProtocolAssemblyName)
            return null;

        if (!name.StartsWith("Cursorial.", StringComparison.Ordinal))
            return null;

        if (_resolution.UserDirectory is { } userDirectory)
        {
            var userPath = Path.Combine(userDirectory, name + ".dll");
            if (File.Exists(userPath))
                return LoadResolved(name, userPath, "user output");
        }

        if (_resolution.Checkout?.ProbeFor(name) is { } checkoutPath)
            return LoadResolved(name, checkoutPath, "framework checkout output");

        var bundledPath = Path.Combine(_resolution.BundleDirectory, name + ".dll");
        if (File.Exists(bundledPath))
            return LoadFromAssemblyPath(bundledPath); // the default source; not narrated

        return null;
    }

    /// <summary>
    /// Reached only after both <see cref="Load"/> and the default-context fallback declined:
    /// third-party dependencies outside the launcher's own dependency closure (e.g.
    /// <c>Microsoft.Extensions.TimeProvider.Testing</c>, the headless host's time source).
    /// Same user-first, bundle-fallback rule, WITHOUT the checkout probe — framework
    /// class-library outputs don't materialize their NuGet closure, so probing them for
    /// third-party names would never hit. Shared-framework assemblies never get this far —
    /// the default context supplies them first — so nothing runtime-owned is ever duplicated
    /// into this context.
    /// </summary>
    private Assembly? ResolveFallbackDependency(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        if (assemblyName.Name is not { } name)
            return null;

        if (_resolution.UserDirectory is { } userDirectory)
        {
            var userPath = Path.Combine(userDirectory, name + ".dll");
            if (File.Exists(userPath))
                return LoadResolved(name, userPath, "user output");
        }

        var bundledPath = Path.Combine(_resolution.BundleDirectory, name + ".dll");
        if (File.Exists(bundledPath))
            return LoadFromAssemblyPath(bundledPath);

        return null;
    }

    /// <summary>
    /// Loads a non-bundle resolution and narrates it (debug) — the ready report names where the
    /// FRAMEWORK comes from; these lines name everyone else's origin, so a mixed-vintage
    /// diagnosis never needs a debugger. Emission is thread-safe (the launcher's writer locks)
    /// and resolution-safe (Protocol and its serializer live in the default context, fully
    /// loaded before this context exists).
    /// </summary>
    private Assembly LoadResolved(string name, string path, string origin)
    {
        _emit?.Invoke(new LogEvent { Level = "debug", Message = $"Resolved {name} from {origin}: {path}" });
        return LoadFromAssemblyPath(path);
    }
}
