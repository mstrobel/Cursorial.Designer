using System.Text.Json.Serialization;

namespace Cursorial.Designer.Protocol;

/// <summary>
/// A message sent from the preview host to the IDE plugin, one JSON object per line on stdout.
/// Events answering a specific command echo its id via <see cref="ReplyTo"/>; unsolicited events
/// (frames pushed after input or animation, logs) leave it null.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ReadyEvent), "ready")]
[JsonDerivedType(typeof(FrameEvent), "frame")]
[JsonDerivedType(typeof(DiagnosticsEvent), "diagnostics")]
[JsonDerivedType(typeof(HitTestResultEvent), "hitTestResult")]
[JsonDerivedType(typeof(ChildrenEvent), "children")]
[JsonDerivedType(typeof(PropertiesEvent), "properties")]
[JsonDerivedType(typeof(CellSamplesEvent), "cellSamples")]
[JsonDerivedType(typeof(CompletionsEvent), "completions")]
[JsonDerivedType(typeof(ErrorEvent), "error")]
[JsonDerivedType(typeof(LogEvent), "log")]
public abstract class PreviewEvent
{
    /// <summary>The id of the command this event answers, when it answers one.</summary>
    public long? ReplyTo { get; init; }
}

/// <summary>Emitted once at startup, before any command is processed.</summary>
public sealed class ReadyEvent : PreviewEvent
{
    public required int ProtocolVersion { get; init; }

    public required int Pid { get; init; }
}

/// <summary>
/// A full composited frame. Cell content is run-length encoded per row: each row is a list of
/// <see cref="TextRun"/>s whose <c>s</c> indexes into <see cref="Styles"/> (deduplicated per
/// frame). Run widths (<c>w</c>) always sum to <see cref="Columns"/> for every row.
/// </summary>
public sealed class FrameEvent : PreviewEvent
{
    public required int Columns { get; init; }

    public required int Rows { get; init; }

    public required CursorInfo Cursor { get; init; }

    /// <summary>The frame's deduplicated style table, referenced by <see cref="TextRun.StyleIndex"/>.</summary>
    public required IReadOnlyList<StyleInfo> Styles { get; init; }

    /// <summary>One entry per row, top to bottom; each row is its runs, left to right. Empty on delta frames.</summary>
    public required IReadOnlyList<IReadOnlyList<TextRun>> Lines { get; init; }

    /// <summary>
    /// True when this is a row-level DELTA against the previously emitted frame: only
    /// <see cref="Changed"/> rows differ (style indices reference THIS event's
    /// <see cref="Styles"/> table). Unchanged frames are not emitted at all.
    /// </summary>
    public bool? Delta { get; init; }

    /// <summary>The changed rows of a delta frame.</summary>
    public IReadOnlyList<ChangedRowInfo>? Changed { get; init; }
}

/// <summary>One changed row of a delta frame.</summary>
public sealed class ChangedRowInfo
{
    /// <summary>0-based row index.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("i")]
    public required int Index { get; init; }

    public required IReadOnlyList<TextRun> Runs { get; init; }
}

/// <summary>Parse/load diagnostics for the most recent <c>loadXaml</c>. Always emitted, even when empty.</summary>
public sealed class DiagnosticsEvent : PreviewEvent
{
    public string? SourceUri { get; init; }

    public required IReadOnlyList<DiagnosticInfo> Items { get; init; }
}

/// <summary>Answer to <c>hitTest</c>: the element chain at the position, innermost first. Empty when nothing hit.</summary>
public sealed class HitTestResultEvent : PreviewEvent
{
    public required IReadOnlyList<ElementRef> Elements { get; init; }
}

/// <summary>Answer to <c>getChildren</c>: the element's visual children, in visual order.</summary>
public sealed class ChildrenEvent : PreviewEvent
{
    public required int ParentId { get; init; }

    public required IReadOnlyList<ElementRef> Elements { get; init; }
}

/// <summary>Answer to <c>getProperties</c>: the element's non-default property values with provenance.</summary>
public sealed class PropertiesEvent : PreviewEvent
{
    public required int ElementId { get; init; }

    /// <summary>The element's active style classes and pseudo-classes (e.g. <c>:pointerover, .accent</c>), when any.</summary>
    public string? Classes { get; init; }

    public required IReadOnlyList<PropertyEntry> Items { get; init; }
}

/// <summary>
/// Answer to <c>sampleCell</c>: every composited layer's contribution at the cell, ordered
/// bottom→top (display usually reverses). Layers whose footprint excludes the cell carry no
/// cell payload but still report their surface and composite parameters.
/// </summary>
public sealed class CellSamplesEvent : PreviewEvent
{
    public required int Column { get; init; }

    public required int Row { get; init; }

    public required IReadOnlyList<LayerSampleInfo> Layers { get; init; }
}

/// <summary>One composited layer's contribution at a sampled cell.</summary>
public sealed class LayerSampleInfo
{
    /// <summary>The layer's z-order within its surface.</summary>
    public required int SurfaceZ { get; init; }

    /// <summary>Description of the element that owns the layer (e.g. <c>DockPanel</c>, <c>Backstage</c>).</summary>
    public string? Element { get; init; }

    /// <summary>The grapheme at the cell in this layer, when the cell is inside the layer's footprint.</summary>
    public string? Grapheme { get; init; }

    /// <summary>The cell kind: <c>Single</c>, <c>WideLeft</c>, <c>WideContinuation</c>; absent when outside the footprint.</summary>
    public string? Kind { get; init; }

    /// <summary>The layer's composite parameters (offset, opacity, clip, blend mode).</summary>
    public required CompositeParametersInfo Parameters { get; init; }

    /// <summary>The exact style the layer carries at the cell (pre-quantization intent).</summary>
    public StyleInfo? Style { get; init; }
}

/// <summary>Composite parameters a layer was blended with.</summary>
public sealed class CompositeParametersInfo
{
    public required int OffsetColumn { get; init; }

    public required int OffsetRow { get; init; }

    /// <summary>0 (transparent) – 255 (opaque).</summary>
    public required int Opacity { get; init; }

    /// <summary>The clip rectangle in target coordinates, host-formatted; absent when unclipped.</summary>
    public string? Clip { get; init; }

    /// <summary>The blend mode name; absent for the default (source-over).</summary>
    public string? Mode { get; init; }
}

/// <summary>Answer to <c>complete</c>: completion items for the requested position.</summary>
public sealed class CompletionsEvent : PreviewEvent
{
    public required IReadOnlyList<CompletionItemInfo> Items { get; init; }
}

/// <summary>One completion item.</summary>
public sealed class CompletionItemInfo
{
    /// <summary>The text to insert (may carry an xmlns prefix, e.g. <c>bars:Ribbon</c>).</summary>
    public required string Text { get; init; }

    /// <summary><c>element</c>, <c>attribute</c>, or <c>value</c> — drives the IDE's icon and insert handling.</summary>
    public required string Kind { get; init; }

    /// <summary>Optional detail shown alongside (e.g. the declaring CLR namespace, or the enum type).</summary>
    public string? Detail { get; init; }
}

/// <summary>A command failed. The session stays alive; the previous content keeps rendering.</summary>
public sealed class ErrorEvent : PreviewEvent
{
    public required string Message { get; init; }

    /// <summary>Optional detail (typically an exception ToString) for the IDE's log, not for end-user display.</summary>
    public string? Detail { get; init; }
}

/// <summary>Host-side log line surfaced to the IDE's log. Levels: <c>debug</c>, <c>info</c>, <c>warn</c>, <c>error</c>.</summary>
public sealed class LogEvent : PreviewEvent
{
    public required string Level { get; init; }

    public required string Message { get; init; }
}
