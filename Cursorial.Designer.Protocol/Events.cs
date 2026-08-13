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
[JsonDerivedType(typeof(DependenciesEvent), "dependencies")]
[JsonDerivedType(typeof(HitTestResultEvent), "hitTestResult")]
[JsonDerivedType(typeof(ChildrenEvent), "children")]
[JsonDerivedType(typeof(PropertiesEvent), "properties")]
[JsonDerivedType(typeof(CellSamplesEvent), "cellSamples")]
[JsonDerivedType(typeof(CompletionsEvent), "completions")]
[JsonDerivedType(typeof(HoverInfoEvent), "hoverInfo")]
[JsonDerivedType(typeof(DefinitionEvent), "definition")]
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

    /// <summary>
    /// The version of the <c>Cursorial.Core</c> the session resolves (e.g. <c>0.5.0.0</c>),
    /// read from assembly metadata before anything loads. Additive; absent from older hosts
    /// and when no framework is present at all.
    /// </summary>
    public string? FrameworkVersion { get; init; }

    /// <summary>
    /// Where the framework comes from: <c>user</c> when the session prefers the user project's
    /// build output, <c>bundled</c> when it runs on the designer's own copies. Additive.
    /// </summary>
    public string? FrameworkSource { get; init; }

    /// <summary>The directory <c>Cursorial.Core</c> resolves from (no trailing separator). Additive.</summary>
    public string? FrameworkPath { get; init; }

    /// <summary>
    /// Human-readable reason when the user's framework was present but NOT used (e.g. older
    /// than the bundled floor). Null on the normal paths — its presence is the IDE's cue to
    /// narrate the fallback. Additive.
    /// </summary>
    public string? FallbackReason { get; init; }

    /// <summary>
    /// True when the host was GIVEN a user directory but found no assemblies there (directory
    /// absent, or empty of dlls) — the project was never built or was cleaned, and the preview
    /// runs entirely on the designer's bundled framework. Deliberately a separate flag from
    /// <see cref="FallbackReason"/>: the IDE renders this condition louder (a dismissible cue —
    /// user types will silently fail to resolve until a build exists) than the quiet
    /// wrong-vintage note. Omitted when false. Additive.
    /// </summary>
    public bool? UserDirMissing { get; init; }
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

    /// <summary>Classified token ranges for semantic highlighting; present only when the
    /// <c>analyze</c> command asked for them (<c>classify: true</c>).</summary>
    public IReadOnlyList<TokenInfo>? Tokens { get; init; }
}

/// <summary>One classified token range for semantic highlighting (1-based line/column).</summary>
public sealed class TokenInfo
{
    [JsonPropertyName("l")]
    public required int Line { get; init; }

    [JsonPropertyName("c")]
    public required int Column { get; init; }

    [JsonPropertyName("n")]
    public required int Length { get; init; }

    /// <summary><c>element</c>, <c>attached</c>, <c>directive</c>, or <c>extension</c>.</summary>
    [JsonPropertyName("k")]
    public required string Kind { get; init; }
}

/// <summary>
/// The on-disk files the most recent <c>loadXaml</c> actually consumed beyond the document
/// itself (linked resource dictionaries, live project files preferred over baked embedded
/// copies). The IDE watches these and reloads the preview when one changes.
/// </summary>
public sealed class DependenciesEvent : PreviewEvent
{
    public required IReadOnlyList<string> Files { get; init; }
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

    /// <summary><c>element</c>, <c>attribute</c>, <c>event</c>, <c>attached</c>, or <c>value</c> — drives the
    /// IDE's icon and insert handling. <c>event</c> is a routed/CLR event and <c>attached</c> is an attached
    /// property (<c>Grid.Column</c>): both insert like an <c>attribute</c> (an <c>="…"</c> attribute) but carry
    /// a distinct icon.</summary>
    public required string Kind { get; init; }

    /// <summary>Optional detail shown alongside (e.g. the declaring CLR namespace, or the enum type).</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// True when this candidate is a PARENT-CONTEXT attached property — an attached property whose owner is
    /// (or is a base of) the element's actual parent, e.g. <c>Grid.Column</c> on a child of a <c>Grid</c>.
    /// The plugin's completion sorter lifts these to the top band (most-relevant). Omitted (null) otherwise.
    /// Additive, non-breaking: consumers that ignore it see the item ranked normally.
    /// </summary>
    public bool? ParentContext { get; init; }

    /// <summary>
    /// True when this candidate is one of the element's OWN members — declared on, or AddOwner'd onto, the
    /// element's exact type (e.g. <c>Content</c> on a <c>Button</c>, or <c>Foreground</c> AddOwner'd onto a
    /// <c>TextBlock</c>), as opposed to a member merely inherited from a base type. The plugin's completion
    /// sorter ranks these in the middle band, below parent-context attached properties and above the
    /// alphabetical body. Omitted (null) otherwise. Additive, non-breaking.
    /// </summary>
    public bool? OwnMember { get; init; }

    /// <summary>
    /// Text to insert when it differs from <see cref="Text"/> (which then serves as the display
    /// and match string) — e.g. resource keys display as <c>ThemeKeys.ElevationDesktop</c> but
    /// insert <c>{x:Static ThemeKeys.ElevationDesktop}</c>.
    /// </summary>
    public string? Insert { get; init; }

    /// <summary>Caret position within <see cref="Insert"/> after insertion, when it should not
    /// land at the end — e.g. inside the closing brace of a stubbed nested extension.</summary>
    public int? Caret { get; init; }
}

/// <summary>Answer to <c>hover</c>: symbol information at the position. All-null members mean "nothing here".</summary>
public sealed class HoverInfoEvent : PreviewEvent
{
    /// <summary>A code-ish one-liner (e.g. <c>class Cursorial.UI.Controls.Button : ContentControl</c>).</summary>
    public string? Signature { get; init; }

    /// <summary>The symbol's XML-doc <c>&lt;summary&gt;</c> text, when the assembly ships a doc file.</summary>
    public string? Summary { get; init; }

    /// <summary>Extra fact worth surfacing (e.g. a constant's value, the declaring assembly).</summary>
    public string? Detail { get; init; }
}

/// <summary>Answer to <c>definition</c>: a source location from portable PDB sequence points, or all-null.</summary>
public sealed class DefinitionEvent : PreviewEvent
{
    /// <summary>Absolute path as recorded in the PDB; the IDE should verify it exists locally.</summary>
    public string? File { get; init; }

    public int? Line { get; init; }

    public int? Column { get; init; }

    /// <summary>Display name of the resolved symbol (for the IDE's action presentation).</summary>
    public string? Symbol { get; init; }
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
