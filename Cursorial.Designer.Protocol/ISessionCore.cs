namespace Cursorial.Designer.Protocol;

/// <summary>
/// The process-internal seam between the preview host's launcher and its framework-bound core —
/// NOT a wire shape. The launcher activates the core's implementation across an
/// <c>AssemblyLoadContext</c> boundary and drives it with deserialized
/// <see cref="PreviewCommand"/>s; the core answers through the <see cref="PreviewEvent"/> emitter
/// it was constructed with. Declared in Protocol because Protocol is the one assembly both sides
/// share in the DEFAULT load context — which is what makes this interface, and every command and
/// event that crosses it, reference-identical on both sides of the boundary.
/// </summary>
public interface ISessionCore : IDisposable
{
    /// <summary>Executes one protocol command. The calling thread is the session's UI thread.</summary>
    void Execute(PreviewCommand command);
}
