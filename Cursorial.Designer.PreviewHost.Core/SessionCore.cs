using Cursorial.Designer.Protocol;

namespace Cursorial.Designer.PreviewHost;

/// <summary>
/// The launcher-facing entry point: the core's only public type, activated reflectively by the
/// launcher (by this exact full name — see <c>CoreLoader</c>) after it loads this assembly into
/// the session's <c>AssemblyLoadContext</c>. Constructing it is the first framework touch in the
/// process, so every framework static <see cref="PreviewSession"/>'s initializer pins —
/// <c>XamlSchemaContext.Default</c>, the <c>XamlModule</c> hooks, the captured metadata
/// provider — comes to life inside that context, never the launcher's default context.
/// </summary>
public sealed class SessionCore : ISessionCore
{
    private readonly PreviewSession _session;

    public SessionCore(Action<PreviewEvent> emit) => _session = new PreviewSession(emit);

    public void Execute(PreviewCommand command) => _session.Execute(command);

    public void Dispose() => _session.Dispose();
}
