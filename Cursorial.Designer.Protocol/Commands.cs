using System.Text.Json.Serialization;

namespace Cursorial.Designer.Protocol;

/// <summary>
/// A message sent from the IDE plugin to the preview host, one JSON object per line on stdin.
/// The <c>type</c> discriminator selects the derived shape. Commands that expect a correlated
/// reply (hit tests, property queries) carry an <see cref="Id"/> the host echoes back as
/// <see cref="PreviewEvent.ReplyTo"/>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(InitializeCommand), "initialize")]
[JsonDerivedType(typeof(LoadXamlCommand), "loadXaml")]
[JsonDerivedType(typeof(ResizeCommand), "resize")]
[JsonDerivedType(typeof(PointerCommand), "pointer")]
[JsonDerivedType(typeof(KeyCommand), "key")]
[JsonDerivedType(typeof(TextCommand), "text")]
[JsonDerivedType(typeof(AdvanceTimeCommand), "advanceTime")]
[JsonDerivedType(typeof(HitTestCommand), "hitTest")]
[JsonDerivedType(typeof(GetChildrenCommand), "getChildren")]
[JsonDerivedType(typeof(GetPropertiesCommand), "getProperties")]
[JsonDerivedType(typeof(SampleCellCommand), "sampleCell")]
[JsonDerivedType(typeof(SetThemeCommand), "setTheme")]
[JsonDerivedType(typeof(ShutdownCommand), "shutdown")]
public abstract class PreviewCommand
{
    /// <summary>Optional correlation id, echoed back on the answering event's <c>replyTo</c>.</summary>
    public long? Id { get; init; }
}

/// <summary>
/// The first command of a session: establishes protocol version, surface size, and the synthetic
/// terminal profile the preview renders against. Must precede <see cref="LoadXamlCommand"/>.
/// </summary>
public sealed class InitializeCommand : PreviewCommand
{
    public required int ProtocolVersion { get; init; }

    public required int Columns { get; init; }

    public required int Rows { get; init; }

    /// <summary>Synthetic terminal profile name (e.g. <c>kitty-truecolor</c>, <c>ansi16</c>). Host default when omitted.</summary>
    public string? Capabilities { get; init; }

    /// <summary>Theme base to start in: <c>dark</c> or <c>light</c>. Host default when omitted.</summary>
    public string? ThemeBase { get; init; }

    /// <summary>Color tier to start in (host-defined names). Host default when omitted.</summary>
    public string? ColorTier { get; init; }
}

/// <summary>
/// Load (or re-load) a XAML document and show its root as the preview content. The host always
/// answers with a <c>diagnostics</c> event (possibly empty) and, when the document produced a
/// showable root, a <c>frame</c> event.
/// </summary>
public sealed class LoadXamlCommand : PreviewCommand
{
    public required string Xaml { get; init; }

    /// <summary>Source URI used in diagnostics (typically the file:// path of the edited document).</summary>
    public string? SourceUri { get; init; }

    /// <summary>
    /// Absolute paths of user assemblies to load and register for XAML type resolution before
    /// parsing (the project's build output). Loaded once per path; repeats are ignored.
    /// </summary>
    public IReadOnlyList<string>? Assemblies { get; init; }
}

/// <summary>Resize the preview surface (in cells).</summary>
public sealed class ResizeCommand : PreviewCommand
{
    public required int Columns { get; init; }

    public required int Rows { get; init; }
}

/// <summary>Synthetic mouse input at a cell position.</summary>
public sealed class PointerCommand : PreviewCommand
{
    /// <summary><c>move</c>, <c>down</c>, or <c>up</c>.</summary>
    public required string Kind { get; init; }

    public required int Column { get; init; }

    public required int Row { get; init; }

    /// <summary><c>left</c>, <c>right</c>, or <c>middle</c>; defaults to <c>left</c>.</summary>
    public string? Button { get; init; }
}

/// <summary>Synthetic keyboard input (a named key or a single printable character).</summary>
public sealed class KeyCommand : PreviewCommand
{
    /// <summary>A printable character, or a name: Enter, Tab, Escape, Up, Down, Left, Right, Backspace, Delete, Home, End, PageUp, PageDown, F1..F12, Space.</summary>
    public required string Key { get; init; }

    /// <summary>Any of <c>ctrl</c>, <c>alt</c>, <c>shift</c>.</summary>
    public IReadOnlyList<string>? Modifiers { get; init; }

    /// <summary>
    /// <c>down</c> or <c>up</c> for real press/release transitions (holding a key holds the
    /// pressed state; a repeated <c>down</c> while held is delivered as a key repeat). Omitted:
    /// a complete press (down immediately followed by up).
    /// </summary>
    public string? Kind { get; init; }
}

/// <summary>Synthetic text entry (a sequence of printable characters).</summary>
public sealed class TextCommand : PreviewCommand
{
    public required string Text { get; init; }
}

/// <summary>Advance the preview's frozen clock (drives animations deterministically).</summary>
public sealed class AdvanceTimeCommand : PreviewCommand
{
    public required int Milliseconds { get; init; }
}

/// <summary>Hit-test a cell position; answered by a <c>hitTestResult</c> event (innermost element first).</summary>
public sealed class HitTestCommand : PreviewCommand
{
    public required int Column { get; init; }

    public required int Row { get; init; }
}

/// <summary>Query the non-default properties of a previously hit-tested element; answered by a <c>properties</c> event.</summary>
public sealed class GetPropertiesCommand : PreviewCommand
{
    public required int ElementId { get; init; }
}

/// <summary>
/// Enumerate an element's visual children (descend below a hit-test anchor, explore siblings);
/// answered by a <c>children</c> event carrying the same element shape as hit tests.
/// </summary>
public sealed class GetChildrenCommand : PreviewCommand
{
    public required int ElementId { get; init; }
}

/// <summary>
/// Sample what every composited layer contributes at a screen cell — including occluded
/// glyphs; answered by a <c>cellSamples</c> event. The per-cell composition inspector.
/// </summary>
public sealed class SampleCellCommand : PreviewCommand
{
    public required int Column { get; init; }

    public required int Row { get; init; }
}

/// <summary>Switch theme base and/or color tier; answered by a fresh <c>frame</c> event.</summary>
public sealed class SetThemeCommand : PreviewCommand
{
    public string? ThemeBase { get; init; }

    public string? ColorTier { get; init; }
}

/// <summary>Orderly shutdown; the host exits after acknowledging with a final <c>log</c> event.</summary>
public sealed class ShutdownCommand : PreviewCommand;
