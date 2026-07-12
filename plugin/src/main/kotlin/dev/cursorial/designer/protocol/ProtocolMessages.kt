package dev.cursorial.designer.protocol

/**
 * Cursorial Designer preview protocol, version 1.
 *
 * Transport: newline-delimited JSON over the preview host's stdio.
 * Commands (plugin -> host) go to stdin; events (host -> plugin) arrive on stdout;
 * stderr carries free-form host logs.
 *
 * These DTOs are (de)serialized with Gson, which maps field names 1:1 to the JSON
 * property names below — do not rename fields without changing the protocol.
 *
 * Note on Gson + Kotlin: Gson instantiates classes without invoking constructors, so a
 * field absent from the JSON ends up null even when declared non-null. Fields that the
 * protocol marks optional are declared nullable here; treat non-null fields on received
 * events as "expected but verify when it matters".
 */
object Protocol {
    const val VERSION: Int = 1
}

// ---------------------------------------------------------------------------
// Commands: plugin -> host
// ---------------------------------------------------------------------------

sealed interface PreviewerCommand {
    /** JSON discriminator; fixed per concrete command type. */
    val type: String
}

data class InitializeCommand(
    val protocolVersion: Int = Protocol.VERSION,
    val columns: Int,
    val rows: Int,
    val capabilities: String = "kitty-truecolor",
    val themeBase: String = ThemeBase.DARK,
    /** e.g. "truecolor", "ansi256", "ansi16", "nocolor"; null = the host's default for the profile. */
    val colorTier: String? = null,
) : PreviewerCommand {
    override val type: String = "initialize"
}

data class LoadXamlCommand(
    val xaml: String,
    val sourceUri: String,
    val assemblies: List<String> = emptyList(),
) : PreviewerCommand {
    override val type: String = "loadXaml"
}

data class ResizeCommand(
    val columns: Int,
    val rows: Int,
) : PreviewerCommand {
    override val type: String = "resize"
}

data class PointerCommand(
    /** One of [PointerKind]. */
    val kind: String,
    val column: Int,
    val row: Int,
    /** One of [PointerButton]; null for pure move events. */
    val button: String? = null,
) : PreviewerCommand {
    override val type: String = "pointer"
}

data class KeyCommand(
    val key: String,
    val modifiers: List<String> = emptyList(),
    /** "down"/"up" for real transitions; null = complete press (down + up). */
    val kind: String? = null,
) : PreviewerCommand {
    override val type: String = "key"
}

data class TextCommand(
    val text: String,
) : PreviewerCommand {
    override val type: String = "text"
}

data class AdvanceTimeCommand(
    val milliseconds: Long,
) : PreviewerCommand {
    override val type: String = "advanceTime"
}

data class HitTestCommand(
    /** Correlation id echoed back as `replyTo` on the [HitTestResultEvent]. */
    val id: Int,
    val column: Int,
    val row: Int,
) : PreviewerCommand {
    override val type: String = "hitTest"
}

data class GetPropertiesCommand(
    /** Correlation id echoed back as `replyTo` on the [PropertiesEvent]. */
    val id: Int,
    val elementId: Int,
    /** Also report properties still at their metadata defaults (normally omitted). */
    val includeDefaults: Boolean = false,
) : PreviewerCommand {
    override val type: String = "getProperties"
}

data class SampleCellCommand(
    /** Correlation id echoed back as `replyTo` on the [CellSamplesEvent]. */
    val id: Int,
    val column: Int,
    val row: Int,
) : PreviewerCommand {
    override val type: String = "sampleCell"
}

data class GetChildrenCommand(
    /** Correlation id echoed back as `replyTo` on the [ChildrenEvent]. */
    val id: Int,
    val elementId: Int,
) : PreviewerCommand {
    override val type: String = "getChildren"
}

/** Editor service: parse-only diagnostics for a (possibly mid-edit) document. Valid before initialize. */
data class AnalyzeCommand(
    /** Correlation id echoed back as `replyTo` on the answering [DiagnosticsEvent]. */
    val id: Int,
    val xaml: String,
    val sourceUri: String? = null,
    val assemblies: List<String> = emptyList(),
    /** When true, the answering [DiagnosticsEvent] also carries semantic [DiagnosticsEvent.tokens]. */
    val classify: Boolean? = null,
) : PreviewerCommand {
    override val type: String = "analyze"
}

/** Editor service: symbol info (signature/docs/value) at a 1-based position. Valid before initialize. */
data class HoverCommand(
    /** Correlation id echoed back as `replyTo` on the [HoverInfoEvent]. */
    val id: Int,
    val xaml: String,
    val line: Int,
    val column: Int,
    val assemblies: List<String> = emptyList(),
    /** Local path of the edited document (in-document targets report openable locations). */
    val filePath: String? = null,
) : PreviewerCommand {
    override val type: String = "hover"
}

/** Editor service: source location of the symbol at a 1-based position (portable PDB). Valid before initialize. */
data class DefinitionCommand(
    /** Correlation id echoed back as `replyTo` on the [DefinitionEvent]. */
    val id: Int,
    val xaml: String,
    val line: Int,
    val column: Int,
    val assemblies: List<String> = emptyList(),
    /** Local path of the edited document (in-document targets report openable locations). */
    val filePath: String? = null,
) : PreviewerCommand {
    override val type: String = "definition"
}

/** Editor service: completion at a 1-based position. Valid before initialize. */
data class CompleteCommand(
    /** Correlation id echoed back as `replyTo` on the [CompletionsEvent]. */
    val id: Int,
    val xaml: String,
    val line: Int,
    val column: Int,
    val assemblies: List<String> = emptyList(),
) : PreviewerCommand {
    override val type: String = "complete"
}

data class SetThemeCommand(
    /** One of [ThemeBase]; null = leave unchanged. */
    val themeBase: String? = null,
    /** e.g. "truecolor", "ansi256", "ansi16", "nocolor"; null = leave unchanged. */
    val colorTier: String? = null,
) : PreviewerCommand {
    override val type: String = "setTheme"
}

class ShutdownCommand : PreviewerCommand {
    override val type: String = "shutdown"
}

object PointerKind {
    const val MOVE: String = "move"
    const val DOWN: String = "down"
    const val UP: String = "up"
}

object PointerButton {
    const val LEFT: String = "left"
    const val RIGHT: String = "right"
    const val MIDDLE: String = "middle"
}

object ThemeBase {
    const val DARK: String = "dark"
    const val LIGHT: String = "light"
}

// ---------------------------------------------------------------------------
// Events: host -> plugin
// ---------------------------------------------------------------------------

sealed interface PreviewerEvent

data class ReadyEvent(
    val protocolVersion: Int,
    val pid: Long,
) : PreviewerEvent

/**
 * One rendered frame of the character-cell grid.
 *
 * [lines] has [rows] entries; each entry is the list of runs covering that row in order.
 * A run is [Run.t] text drawn with style [Run.s] (index into [styles]) covering [Run.w]
 * cell columns (which may exceed `t.length` for wide glyphs).
 */
data class FrameEvent(
    val columns: Int,
    val rows: Int,
    val cursor: CursorInfo?,
    val styles: List<StyleDefinition>,
    /** Empty on delta frames. */
    val lines: List<List<Run>>,
    /** True = row-level delta: only [changed] rows differ; style indices reference THIS event's [styles]. */
    val delta: Boolean? = null,
    val changed: List<ChangedRow>? = null,
) : PreviewerEvent

/** One changed row of a delta frame. */
data class ChangedRow(
    /** 0-based row index. */
    val i: Int,
    val runs: List<Run>,
)

data class CursorInfo(
    val row: Int,
    val column: Int,
    val visible: Boolean,
    /** e.g. "default", "block", "bar", "underline". */
    val shape: String?,
)

data class StyleDefinition(
    /** "#RRGGBB" or "default". */
    val fg: String?,
    /** "#RRGGBB" or "default". */
    val bg: String?,
    /** Any of: "bold", "italic", "underline", "dim", "strikethrough", "reverse", "overline". */
    val attrs: List<String>?,
    /** Underline shape when not the plain single one: "double", "curly", "dotted", "dashed". */
    val underline: String? = null,
    /** Underline color when it differs from the foreground. */
    val underlineColor: String? = null,
    /** OSC 8 hyperlink target. */
    val link: String? = null,
)

data class Run(
    /** Text of the run. */
    val t: String,
    /** Style index into [FrameEvent.styles]. */
    val s: Int,
    /** Cell columns covered by the run. */
    val w: Int,
)

data class DiagnosticsEvent(
    val replyTo: Int?,
    val sourceUri: String?,
    val items: List<DiagnosticItem>,
    /** Semantic token ranges; present only when the analyze command set [AnalyzeCommand.classify]. */
    val tokens: List<TokenRange>? = null,
) : PreviewerEvent

/** One classified token range for semantic highlighting (1-based line/column). */
data class TokenRange(
    /** Line (1-based). */
    val l: Int,
    /** Column (1-based). */
    val c: Int,
    /** Length in characters. */
    val n: Int,
    /** "element", "attached", "directive", or "extension". */
    val k: String,
)

/** Answer to [HoverCommand]; all-null members mean "nothing at this position". */
data class HoverInfoEvent(
    val replyTo: Int?,
    /** Code-ish one-liner, e.g. `class Cursorial.UI.Controls.Button : ContentControl`. */
    val signature: String?,
    /** XML-doc summary text, when the assembly ships a doc file. */
    val summary: String?,
    /** Extra fact (a constant's value, the declaring assembly). */
    val detail: String?,
) : PreviewerEvent

/** Answer to [DefinitionCommand]; all-null members mean "no source location". */
data class DefinitionEvent(
    val replyTo: Int?,
    /** Absolute path as recorded in the PDB — verify it exists locally before navigating. */
    val file: String?,
    val line: Int?,
    val column: Int?,
    /** Display name of the resolved symbol. */
    val symbol: String?,
) : PreviewerEvent

data class DiagnosticItem(
    val code: String?,
    val message: String,
    val line: Int,
    val column: Int,
    /** "error", "warning" or "info". */
    val severity: String,
)

data class HitTestResultEvent(
    val replyTo: Int?,
    val elements: List<HitTestElement>,
) : PreviewerEvent

data class HitTestElement(
    val elementId: Int,
    val elementType: String?,
    val name: String?,
    val bounds: CellRect?,
    /** The XAML document the element came from, when the host tracked sources. */
    val sourceUri: String? = null,
    /** 1-based tag line in [sourceUri]. */
    val line: Int? = null,
    /** 1-based tag column in [sourceUri]. */
    val column: Int? = null,
    /** True = span is from the loaded document (direct caret sync); false = foreign (template). */
    val inDocument: Boolean? = null,
)

/** A rectangle in cell coordinates. */
data class CellRect(
    val column: Int,
    val row: Int,
    val columns: Int,
    val rows: Int,
)

/** Answer to [GetChildrenCommand]: the element's visual children, in visual order. */
data class ChildrenEvent(
    val replyTo: Int?,
    val parentId: Int,
    val elements: List<HitTestElement>,
) : PreviewerEvent

/** Answer to [SampleCellCommand]: every composited layer's contribution, bottom-to-top. */
data class CellSamplesEvent(
    val replyTo: Int?,
    val column: Int,
    val row: Int,
    val layers: List<LayerSampleItem>,
) : PreviewerEvent

data class LayerSampleItem(
    val surfaceZ: Int,
    /** Owning element description (e.g. "DockPanel", "Backstage"). */
    val element: String?,
    /** The grapheme at the cell; null when the cell is outside this layer's footprint. */
    val grapheme: String?,
    /** "Single", "WideLeft", "WideContinuation"; null when outside the footprint. */
    val kind: String?,
    val parameters: CompositeParametersItem,
    /** The exact style the layer carries at the cell (pre-quantization intent). */
    val style: StyleDefinition?,
)

data class CompositeParametersItem(
    val offsetColumn: Int,
    val offsetRow: Int,
    /** 0 (transparent) - 255 (opaque). */
    val opacity: Int,
    val clip: String?,
    val mode: String?,
)

/** Answer to [CompleteCommand]. */
data class CompletionsEvent(
    val replyTo: Int?,
    val items: List<CompletionItem>,
) : PreviewerEvent

data class CompletionItem(
    /** Display/match text (also the inserted text when [insert] is null). */
    val text: String,
    /** "element", "attribute", or "value" — drives icon and insert handling. */
    val kind: String?,
    /** Optional detail (declaring CLR namespace, enum type, "directive"). */
    val detail: String? = null,
    /** Text to insert when it differs from [text] (e.g. an {x:Static …} resource reference). */
    val insert: String? = null,
    /** Caret position within [insert] after insertion (e.g. inside a stub's closing brace). */
    val caret: Int? = null,
)

data class PropertiesEvent(
    val replyTo: Int?,
    val elementId: Int,
    /** The element's active style classes/pseudo-classes (e.g. ":pointerover, .accent"). */
    val classes: String? = null,
    val items: List<PropertyItem>,
) : PreviewerEvent

data class PropertyItem(
    val name: String,
    val value: String?,
    /** e.g. "Local", "StyleSetter", "Inherited". */
    val valueSource: String?,
    /** The declaring owner type when it isn't the element's own (attached properties). */
    val declaringType: String? = null,
    /** Multi-line provenance derivation from the styling diagnostics, when available. */
    val explanation: String? = null,
    /** Winning binding priority (e.g. "LocalValue", "Style"). */
    val priority: String? = null,
    /** Pre-animation base priority, only when it differs. */
    val basePriority: String? = null,
    val isAnimated: Boolean? = null,
    /** Theme/resource key the effective value resolved through. */
    val resourceKey: String? = null,
    /** Every style frame contending for the value — the expandable provenance tree. */
    val frames: List<StyleFrameItem>? = null,
    /** Inline color swatch ("#RRGGBB" or "#RRGGBBAA"), when the value is color-like. */
    val swatch: String? = null,
    /** Binding target descriptor (e.g. "ContentPresenter#PART_Icon.Content"), when bound. */
    val bindingTarget: String? = null,
    /** Every binding expression tracked for the property, strongest first, when bound. */
    val bindings: List<BindingExpressionItem>? = null,
)

data class BindingExpressionItem(
    /** "LocalValue", "FrameHosted", "WatchOnly", or "DirectProperty". */
    val lane: String?,
    val path: String?,
    /** e.g. "Active". */
    val status: String?,
    /** e.g. "OneWay", "TwoWay". */
    val effectiveMode: String?,
    /** Human-readable resolved source/anchor chain. */
    val resolvedSourceChain: String?,
    /** The last value produced to the target. */
    val value: String?,
    /** Last failure kind; null when none. */
    val lastFailure: String?,
)

data class StyleFrameItem(
    val layer: String?,
    val selector: String?,
    val isActive: Boolean,
    val hasValue: Boolean,
    val value: String?,
    /** e.g. "Winning", "Overridden", "Inactive". */
    val status: String?,
    val resourceKey: String?,
    /** Packed specificity sort key (hex). */
    val sortKey: String?,
    /** Inline color swatch for the frame's value, when color-like. */
    val swatch: String? = null,
)

data class ErrorEvent(
    val replyTo: Int?,
    val message: String?,
    /** Typically an exception ToString — for logs/tooltips, not end-user display. */
    val detail: String? = null,
) : PreviewerEvent

data class LogEvent(
    val level: String?,
    val message: String?,
) : PreviewerEvent

/** An event whose `type` this plugin version does not know; kept for forward compatibility. */
data class UnknownEvent(
    val type: String,
    val rawJson: String,
) : PreviewerEvent
