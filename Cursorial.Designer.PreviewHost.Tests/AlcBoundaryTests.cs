using System.Runtime.Loader;

using Cursorial.Designer.PreviewHost;
using Cursorial.Designer.Protocol;

namespace Cursorial.Designer.Tests.PreviewHost;

/// <summary>
/// The launcher/core split's load-context contract, proven in-process (no spawn): the launcher's
/// <see cref="CoreLoader"/> puts the core and the entire framework into a child context while
/// Protocol stays in the default context — which is exactly what makes the commands this test
/// sends and the events it receives reference-identical on both sides of the boundary. A real
/// frame renders through the child-context framework to prove the pipeline, not just the loads.
/// </summary>
public class AlcBoundaryTests
{
    [Fact]
    public void Framework_loads_into_child_context_and_protocol_types_unify_across_the_boundary()
    {
        var events = new List<PreviewEvent>();
        using var session = CoreLoader.CreateSession(events.Add);

        // The seam cast already succeeded inside CreateSession — ISessionCore (Protocol) is one
        // type on both sides — and the session's concrete type lives in the child context, as a
        // DIFFERENT assembly instance than the Core this test project references directly.
        var childContext = AssemblyLoadContext.GetLoadContext(session.GetType().Assembly);
        Assert.NotNull(childContext);
        Assert.NotSame(AssemblyLoadContext.Default, childContext);
        Assert.NotSame(typeof(SessionCore).Assembly, session.GetType().Assembly);

        // Protocol types are reference-unified: the launcher-side (here: test-side) Protocol
        // assembly is the default-context one, and it is the SAME assembly the child-context
        // core binds — its events deserialize into our types below.
        Assert.Same(AssemblyLoadContext.Default, AssemblyLoadContext.GetLoadContext(typeof(PreviewEvent).Assembly));

        // Drive the session across the boundary with default-context command instances and
        // render a real frame with the child-context framework.
        session.Execute(new InitializeCommand { ProtocolVersion = PreviewProtocol.Version, Columns = 40, Rows = 10 });
        session.Execute(new LoadXamlCommand
        {
            Id = 1,
            Xaml = """
                   <StackPanel xmlns="https://cursorial.dev/ui" xmlns:x="https://cursorial.dev/xaml">
                       <TextBlock Text="alc boundary works"/>
                   </StackPanel>
                   """,
        });

        // The framework itself loaded into the child context — every Cursorial.* the session
        // touched resolves there, not in the default context (where this test project holds its
        // own, distinct copies).
        var frameworkAssembly = Assert.Single(childContext!.Assemblies, a => a.GetName().Name == "Cursorial.UI");
        Assert.Same(childContext, AssemblyLoadContext.GetLoadContext(frameworkAssembly));
        Assert.NotSame(typeof(Cursorial.UI.UIElement).Assembly, frameworkAssembly);

        // The events the child-context core emitted are our Protocol types (the IsType casts ARE
        // the unification proof), and the rendered frame carries the document's text.
        Assert.DoesNotContain(events, e => e is ErrorEvent);
        var text = string.Join('\n', events.OfType<FrameEvent>().Select(frame => frame.Delta == true
            ? string.Join('\n', (frame.Changed ?? []).Select(c => string.Concat(c.Runs.Select(r => r.Text))))
            : string.Join('\n', frame.Lines.Select(runs => string.Concat(runs.Select(r => r.Text))))));
        Assert.Contains("alc boundary works", text);
    }
}
