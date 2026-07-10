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
) : PreviewerCommand {
    override val type: String = "getProperties"
}

data class SetThemeCommand(
    /** One of [ThemeBase]. */
    val themeBase: String,
    /** e.g. "truecolor". */
    val colorTier: String,
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
    val lines: List<List<Run>>,
) : PreviewerEvent

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
    /** Any of: "bold", "italic", "underline", "dim", "strikethrough", "reverse". */
    val attrs: List<String>?,
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
)

/** A rectangle in cell coordinates. */
data class CellRect(
    val column: Int,
    val row: Int,
    val columns: Int,
    val rows: Int,
)

data class PropertiesEvent(
    val replyTo: Int?,
    val elementId: Int,
    val items: List<PropertyItem>,
) : PreviewerEvent

data class PropertyItem(
    val name: String,
    val value: String?,
    /** e.g. "Local", "Style", "Default". */
    val valueSource: String?,
)

data class ErrorEvent(
    val replyTo: Int?,
    val message: String?,
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
