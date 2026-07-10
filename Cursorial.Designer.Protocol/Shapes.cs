using System.Text.Json.Serialization;

namespace Cursorial.Designer.Protocol;

/// <summary>Cursor state for a frame, in cell coordinates (0-based, row 0 at the top).</summary>
public sealed class CursorInfo
{
    public required int Row { get; init; }

    public required int Column { get; init; }

    public required bool Visible { get; init; }

    /// <summary><c>default</c>, <c>block</c>, <c>underline</c>, or <c>bar</c> (host-defined names, lower-cased).</summary>
    public required string Shape { get; init; }
}

/// <summary>
/// One entry in a frame's style table. Colors are <c>#RRGGBB</c>; an absent color member means
/// the viewer's terminal-default foreground/background. Absent members mean "not set".
/// </summary>
public sealed class StyleInfo
{
    /// <summary>Foreground color as <c>#RRGGBB</c>; omitted for the terminal default.</summary>
    public string? Fg { get; init; }

    /// <summary>Background color as <c>#RRGGBB</c>; omitted for the terminal default.</summary>
    public string? Bg { get; init; }

    /// <summary>Any of: bold, dim, italic, underline, blink, reverse, hidden, strikethrough, overline.</summary>
    public IReadOnlyList<string>? Attrs { get; init; }

    /// <summary>Underline style when not the plain one: <c>double</c>, <c>curly</c>, <c>dotted</c>, <c>dashed</c>.</summary>
    public string? Underline { get; init; }

    /// <summary>Underline color when it differs from the foreground: <c>#RRGGBB</c>.</summary>
    public string? UnderlineColor { get; init; }

    /// <summary>Hyperlink target, when the cell participates in an OSC 8 hyperlink.</summary>
    public string? Link { get; init; }
}

/// <summary>
/// A run of consecutive same-styled cells within one row. <see cref="Text"/> is the concatenated
/// grapheme clusters; <see cref="Width"/> is the number of cells covered (wide graphemes cover
/// two cells, so width can exceed the number of clusters).
/// </summary>
public sealed class TextRun
{
    [JsonPropertyName("t")]
    public required string Text { get; init; }

    [JsonPropertyName("s")]
    public required int StyleIndex { get; init; }

    [JsonPropertyName("w")]
    public required int Width { get; init; }
}

/// <summary>One XAML diagnostic, positions 1-based as reported by the Cursorial XAML front end.</summary>
public sealed class DiagnosticInfo
{
    /// <summary>Stable Cursorial diagnostic code (CUR1xxx parse, CUR2xxx resolution, CUR3xxx instantiation).</summary>
    public required string Code { get; init; }

    public required string Message { get; init; }

    public required int Line { get; init; }

    public required int Column { get; init; }

    /// <summary><c>error</c>, <c>warning</c>, or <c>info</c>.</summary>
    public required string Severity { get; init; }
}

/// <summary>A rectangle in absolute screen cells (0-based origin at the top-left of the preview surface).</summary>
public sealed class CellRectInfo
{
    public required int Column { get; init; }

    public required int Row { get; init; }

    public required int Columns { get; init; }

    public required int Rows { get; init; }
}

/// <summary>An element surfaced by hit-testing, identified by a session-stable id.</summary>
public sealed class ElementRef
{
    /// <summary>Stable for the lifetime of the currently loaded document; invalidated by the next <c>loadXaml</c>.</summary>
    public required int ElementId { get; init; }

    /// <summary>The element's short type name (e.g. <c>Button</c>).</summary>
    public required string ElementType { get; init; }

    /// <summary>The element's x:Name, when it has one.</summary>
    public string? Name { get; init; }

    public required CellRectInfo Bounds { get; init; }

    /// <summary>The URI of the XAML document the element was instantiated from, when tracked.</summary>
    public string? SourceUri { get; init; }

    /// <summary>1-based line of the element's tag in <see cref="SourceUri"/>, when tracked.</summary>
    public int? Line { get; init; }

    /// <summary>1-based column of the element's tag, when tracked.</summary>
    public int? Column { get; init; }

    /// <summary>
    /// True when the span comes from the currently loaded document (direct caret sync is valid);
    /// false for foreign spans (e.g. a template's defining document); absent when untracked.
    /// </summary>
    public bool? InDocument { get; init; }
}

/// <summary>One row of a property-grid answer: a property that currently contributes a non-default value.</summary>
public sealed class PropertyEntry
{
    public required string Name { get; init; }

    /// <summary>Display string of the effective value (host-formatted).</summary>
    public string? Value { get; init; }

    /// <summary>The winning lane's kind: e.g. Local, StyleSetter, StyleWhen, Inherited, Animation.</summary>
    public string? ValueSource { get; init; }

    /// <summary>Declaring owner type of the property, when it isn't the element's own type.</summary>
    public string? DeclaringType { get; init; }

    /// <summary>Human-readable provenance derivation from the styling diagnostics, when available.</summary>
    public string? Explanation { get; init; }

    /// <summary>The winning binding priority (e.g. LocalValue, Style, Template, Inherited).</summary>
    public string? Priority { get; init; }

    /// <summary>The pre-animation base priority, when it differs from <see cref="Priority"/>.</summary>
    public string? BasePriority { get; init; }

    /// <summary>True when an animation currently owns the value.</summary>
    public bool? IsAnimated { get; init; }

    /// <summary>The theme/resource key the effective value resolved through, when resource-backed.</summary>
    public string? ResourceKey { get; init; }

    /// <summary>
    /// Every style frame attempting to control this property (winning and losing alike), in
    /// evaluation order — the expandable provenance tree. Absent for plain local/inherited values.
    /// </summary>
    public IReadOnlyList<StyleFrameInfo>? Frames { get; init; }
}

/// <summary>One style frame contending for a property value (from the styling diagnostics).</summary>
public sealed class StyleFrameInfo
{
    /// <summary>The style layer the frame belongs to (e.g. Theme, App, Local — host-defined names).</summary>
    public string? Layer { get; init; }

    /// <summary>The frame's selector, as authored (e.g. <c>Button.accent:pointerover</c>).</summary>
    public string? Selector { get; init; }

    /// <summary>Whether the selector currently matches.</summary>
    public required bool IsActive { get; init; }

    /// <summary>Whether the frame currently produces a value.</summary>
    public required bool HasValue { get; init; }

    /// <summary>The value the frame last produced, host-formatted.</summary>
    public string? Value { get; init; }

    /// <summary>The frame's disposition: e.g. Winning, Overridden, Inactive (host-defined names).</summary>
    public string? Status { get; init; }

    /// <summary>The resource key the frame's value resolves through, when resource-backed.</summary>
    public string? ResourceKey { get; init; }

    /// <summary>The frame's packed specificity sort key (hex string).</summary>
    public string? SortKey { get; init; }
}
