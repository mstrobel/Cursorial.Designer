package dev.cursorial.designer.editor

import com.intellij.openapi.editor.colors.EditorColorsManager
import com.intellij.openapi.editor.colors.EditorFontType
import com.intellij.util.ui.GraphicsUtil
import dev.cursorial.designer.protocol.CellRect
import dev.cursorial.designer.protocol.FrameEvent
import dev.cursorial.designer.protocol.PointerButton
import dev.cursorial.designer.protocol.PointerKind
import dev.cursorial.designer.protocol.Run
import java.awt.Color
import java.awt.Dimension
import java.awt.Font
import java.awt.FontMetrics
import java.awt.Graphics
import java.awt.Graphics2D
import java.awt.event.ComponentAdapter
import java.awt.event.ComponentEvent
import java.awt.event.KeyAdapter
import java.awt.event.KeyEvent
import java.awt.event.MouseAdapter
import java.awt.event.MouseEvent
import javax.swing.JComponent
import javax.swing.SwingConstants
import javax.swing.SwingUtilities

/**
 * Paints a Cursorial frame as a colored monospace character-cell grid, and translates
 * Swing geometry/mouse events back into cell-based protocol commands.
 *
 * Threading: all mutators must be called on the EDT (they trigger repaints).
 */
class CellGridPanel : JComponent(), javax.swing.Scrollable {

    /** Called when the panel's size, measured in cells, changes. Send a `resize` command. */
    var resizeListener: ((columns: Int, rows: Int) -> Unit)? = null

    /**
     * Pairs the terminal-default fg/bg with the PREVIEW's theme base rather than the IDE editor
     * scheme: a light-base preview inside a dark IDE must still read like a light terminal.
     * Values match the host's base-paired ANSI palettes (classic xterm dark / Light+ light).
     */
    var lightBase: Boolean = false
        set(value) {
            if (field == value) return
            field = value
            repaint()
        }

    /** Called for mouse activity over the grid. Send a `pointer` command. */
    var pointerListener: ((kind: String, column: Int, row: Int, button: String?) -> Unit)? = null

    /** Called on Alt+Click; the editor issues a `hitTest` and later calls [showSelection]. */
    var hitTestListener: ((column: Int, row: Int) -> Unit)? = null

    /** Called for keyboard activity while the grid is focused. Send a `key` command.
     *  [kind] is "down"/"up" for real transitions, or null for a complete press. */
    var keyListener: ((key: String, modifiers: List<String>, kind: String?) -> Unit)? = null

    /**
     * When true, plain clicks hit-test (element selection) instead of driving the previewed app,
     * and keyboard input stays in the designer ('[' / ']' walk the selection up/down the element
     * chain) instead of forwarding; Alt+Click hit-tests in either mode. Toggled from the toolbar.
     */
    var selectMode: Boolean = false

    /** Called in select mode for '[' (outward = true, toward the root) and ']' (inward). */
    var treeWalkListener: ((outward: Boolean) -> Unit)? = null

    // The current screen state as resolved rows — full frames replace it, delta frames patch
    // individual rows (each frame event carries its own style table, so runs resolve at ingest).
    private var gridColumns = 0
    private var gridRows = 0
    private var rowRuns: MutableList<List<ResolvedRun>> = mutableListOf()
    private var cursor: dev.cursorial.designer.protocol.CursorInfo? = null
    private var selection: CellRect? = null

    private var lastNotifiedColumns = -1
    private var lastNotifiedRows = -1
    private var lastPointerCell: Pair<Int, Int>? = null

    init {
        isOpaque = true
        isFocusable = true
        // Tab must reach the preview (it's focus navigation *inside* the rendered UI), not move
        // Swing focus to the next IDE component.
        focusTraversalKeysEnabled = false

        addComponentListener(object : ComponentAdapter() {
            override fun componentResized(e: ComponentEvent) = notifyGridSizeIfChanged()
        })

        addKeyListener(object : KeyAdapter() {
            override fun keyPressed(e: KeyEvent) {
                // Select mode is inspection: nothing forwards to the app (keyTyped handles [ / ]).
                if (selectMode) {
                    e.consume()
                    return
                }

                val modifiers = modifiersOf(e)
                // Named keys forward as real down/up transitions: holding Space keeps the
                // focused button pressed, Swing auto-repeat becomes a key repeat downstream,
                // and bare modifier presses drive the access-key display (Alt gate).
                namedKey(e)?.let { named ->
                    keyListener?.invoke(named, modifiers, "down")
                    e.consume()
                    return
                }

                // Ctrl/Alt/Cmd chords don't produce a useful keyTyped (macOS Alt composes,
                // Ctrl yields control characters) — reconstruct the base character from the key
                // code so access keys (Alt+C) and shortcuts reach the preview.
                if (e.isControlDown || e.isAltDown || e.isMetaDown) {
                    val base = when (e.keyCode) {
                        in KeyEvent.VK_A..KeyEvent.VK_Z -> ('a' + (e.keyCode - KeyEvent.VK_A)).toString()
                        in KeyEvent.VK_0..KeyEvent.VK_9 -> ('0' + (e.keyCode - KeyEvent.VK_0)).toString()
                        else -> return
                    }
                    keyListener?.invoke(base, modifiers, null)
                    e.consume()
                }
            }

            override fun keyReleased(e: KeyEvent) {
                if (selectMode) {
                    e.consume()
                    return
                }

                val named = namedKey(e) ?: return
                keyListener?.invoke(named, modifiersOf(e), "up")
                e.consume()
            }

            override fun keyTyped(e: KeyEvent) {
                if (selectMode) {
                    when (e.keyChar) {
                        '[' -> treeWalkListener?.invoke(true)
                        ']' -> treeWalkListener?.invoke(false)
                    }
                    e.consume()
                    return
                }

                if (e.isControlDown || e.isMetaDown || e.isAltDown) return // keyPressed handled these
                val ch = e.keyChar
                // Space arrives as the named "Space" key via keyPressed; control chars are named too.
                if (ch == KeyEvent.CHAR_UNDEFINED || ch < ' ' || ch.code == 127 || ch == ' ') return
                keyListener?.invoke(ch.toString(), emptyList(), null)
                e.consume()
            }
        })

        val mouseHandler = object : MouseAdapter() {
            override fun mousePressed(e: MouseEvent) {
                requestFocusInWindow()
                val (column, row) = cellAt(e) ?: return
                if (selectMode || e.isAltDown) {
                    hitTestListener?.invoke(column, row)
                    return
                }
                pointerListener?.invoke(PointerKind.DOWN, column, row, buttonOf(e))
            }

            override fun mouseReleased(e: MouseEvent) {
                val (column, row) = cellAt(e) ?: return
                if (selectMode || e.isAltDown) return
                pointerListener?.invoke(PointerKind.UP, column, row, buttonOf(e))
            }

            override fun mouseMoved(e: MouseEvent) = pointerMove(e)
            override fun mouseDragged(e: MouseEvent) = pointerMove(e)

            private fun pointerMove(e: MouseEvent) {
                val cell = cellAt(e) ?: return
                if (cell == lastPointerCell) return // one move event per cell, not per pixel
                lastPointerCell = cell
                pointerListener?.invoke(PointerKind.MOVE, cell.first, cell.second, null)
            }
        }
        addMouseListener(mouseHandler)
        addMouseMotionListener(mouseHandler)
    }

    // ------------------------------------------------------------------
    // Model updates
    // ------------------------------------------------------------------

    /** Applies a frame — a full replacement or a row-level delta. EDT only. */
    fun render(newFrame: FrameEvent) {
        val resolved = newFrame.styles.map { style ->
            ResolvedStyle(
                fg = parseColor(style.fg),
                bg = parseColor(style.bg),
                attrs = StyleAttrs.of(style.attrs),
            )
        }

        fun resolveRuns(runs: List<Run>): List<ResolvedRun> =
            runs.map { ResolvedRun(it.t, it.w, resolved.getOrNull(it.s)) }

        if (newFrame.delta == true) {
            val metrics = cellMetrics()
            val previousCursorRow = cursor?.row
            for (change in newFrame.changed.orEmpty()) {
                if (change.i in rowRuns.indices) {
                    rowRuns[change.i] = resolveRuns(change.runs)
                    repaint(0, change.i * metrics.cellHeight, width, metrics.cellHeight)
                }
            }
            cursor = newFrame.cursor
            for (row in listOfNotNull(previousCursorRow, cursor?.row))
                repaint(0, row * metrics.cellHeight, width, metrics.cellHeight)
            return
        }

        gridColumns = newFrame.columns
        gridRows = newFrame.rows
        rowRuns = newFrame.lines.map(::resolveRuns).toMutableList()
        cursor = newFrame.cursor
        revalidate()
        repaint()
    }

    /** Shows (or clears, with null) the selection-rectangle overlay from a hit-test result. */
    fun showSelection(bounds: CellRect?) {
        selection = bounds
        repaint()
    }

    /** The visible preview size in cells (the viewport extent inside a scroll pane, else the
     *  panel bounds); falls back to 80x24 before layout. */
    fun gridSize(): Pair<Int, Int> {
        val metrics = cellMetrics()
        val viewport = parent as? javax.swing.JViewport
        val visibleWidth = viewport?.extentSize?.width ?: width
        val visibleHeight = viewport?.extentSize?.height ?: height
        if (visibleWidth <= 0 || visibleHeight <= 0) return DEFAULT_COLUMNS to DEFAULT_ROWS
        val columns = (visibleWidth / metrics.cellWidth).coerceAtLeast(1)
        val rows = (visibleHeight / metrics.cellHeight).coerceAtLeast(1)
        return columns to rows
    }

    /** Re-evaluates the visible grid size and notifies on change — call when the viewport resizes. */
    fun refreshGridSize() = notifyGridSizeIfChanged()

    // ── Scrollable: fill the viewport while the frame fits (auto-fit), scroll when it doesn't ──

    override fun getPreferredScrollableViewportSize(): Dimension = preferredSize

    override fun getScrollableUnitIncrement(visibleRect: java.awt.Rectangle, orientation: Int, direction: Int): Int =
        if (orientation == SwingConstants.HORIZONTAL) cellMetrics().cellWidth else cellMetrics().cellHeight

    override fun getScrollableBlockIncrement(visibleRect: java.awt.Rectangle, orientation: Int, direction: Int): Int =
        if (orientation == SwingConstants.HORIZONTAL) visibleRect.width else visibleRect.height

    override fun getScrollableTracksViewportWidth(): Boolean =
        (parent as? javax.swing.JViewport)?.let { it.width >= preferredSize.width } ?: true

    override fun getScrollableTracksViewportHeight(): Boolean =
        (parent as? javax.swing.JViewport)?.let { it.height >= preferredSize.height } ?: true

    // ------------------------------------------------------------------
    // Painting
    // ------------------------------------------------------------------

    override fun paintComponent(g: Graphics) {
        val g2 = g.create() as Graphics2D
        try {
            GraphicsUtil.setupAntialiasing(g2)

            val defaultBg = if (lightBase) LIGHT_DEFAULT_BG else DARK_DEFAULT_BG
            val defaultFg = if (lightBase) LIGHT_DEFAULT_FG else DARK_DEFAULT_FG

            g2.color = defaultBg
            g2.fillRect(0, 0, width, height)

            if (rowRuns.isEmpty()) return
            val metrics = cellMetrics()
            val cw = metrics.cellWidth
            val ch = metrics.cellHeight

            for ((rowIndex, runs) in rowRuns.withIndex()) {
                val y = rowIndex * ch
                var column = 0
                for (run in runs) {
                    val style = run.style
                    val attrs = style?.attrs ?: StyleAttrs.NONE

                    var fg = style?.fg ?: defaultFg
                    var bg = style?.bg ?: defaultBg
                    if (attrs.reverse) {
                        val swap = fg
                        fg = bg
                        bg = swap
                    }
                    if (attrs.dim) {
                        fg = blend(fg, bg)
                    }

                    val x = column * cw
                    val runWidthPx = run.width * cw

                    if (bg != defaultBg || attrs.reverse) {
                        g2.color = bg
                        g2.fillRect(x, y, runWidthPx, ch)
                    }

                    g2.color = fg
                    g2.font = metrics.font(attrs.bold, attrs.italic)
                    g2.drawString(run.text, x, y + metrics.ascent)

                    if (attrs.underline) {
                        val underlineY = y + metrics.ascent + 1
                        g2.drawLine(x, underlineY, x + runWidthPx - 1, underlineY)
                    }
                    if (attrs.strikethrough) {
                        val strikeY = y + metrics.ascent - metrics.ascent / 3
                        g2.drawLine(x, strikeY, x + runWidthPx - 1, strikeY)
                    }

                    column += run.width
                }
            }

            paintCursor(g2, metrics, defaultFg)
            paintSelection(g2, metrics)
        } finally {
            g2.dispose()
        }
    }

    private fun paintCursor(g2: Graphics2D, metrics: CellMetrics, defaultFg: Color) {
        val cursor = cursor ?: return
        if (!cursor.visible) return

        val x = cursor.column * metrics.cellWidth
        val y = cursor.row * metrics.cellHeight
        g2.color = defaultFg
        when (cursor.shape) {
            "bar" -> g2.fillRect(x, y, 2, metrics.cellHeight)
            "underline" -> g2.fillRect(x, y + metrics.cellHeight - 2, metrics.cellWidth, 2)
            else -> { // "default", "block", anything else: hollow block
                g2.drawRect(x, y, metrics.cellWidth - 1, metrics.cellHeight - 1)
            }
        }
    }

    private fun paintSelection(g2: Graphics2D, metrics: CellMetrics) {
        val selection = selection ?: return
        val x = selection.column * metrics.cellWidth
        val y = selection.row * metrics.cellHeight
        val w = selection.columns * metrics.cellWidth
        val h = selection.rows * metrics.cellHeight

        g2.color = SELECTION_FILL
        g2.fillRect(x, y, w, h)
        g2.color = SELECTION_BORDER
        g2.drawRect(x, y, w - 1, h - 1)
    }

    override fun getPreferredSize(): Dimension {
        val metrics = cellMetrics()
        if (gridColumns <= 0 || gridRows <= 0)
            return Dimension(DEFAULT_COLUMNS * metrics.cellWidth, DEFAULT_ROWS * metrics.cellHeight)
        return Dimension(gridColumns * metrics.cellWidth, gridRows * metrics.cellHeight)
    }

    // ------------------------------------------------------------------
    // Cell geometry
    // ------------------------------------------------------------------

    private var cachedMetrics: CellMetrics? = null

    private fun cellMetrics(): CellMetrics {
        val baseFont = EditorColorsManager.getInstance().globalScheme.getFont(EditorFontType.PLAIN)
        cachedMetrics?.let { if (it.plain == baseFont) return it }

        val fm: FontMetrics = getFontMetrics(baseFont)
        val metrics = CellMetrics(
            plain = baseFont,
            bold = baseFont.deriveFont(Font.BOLD),
            italic = baseFont.deriveFont(Font.ITALIC),
            boldItalic = baseFont.deriveFont(Font.BOLD or Font.ITALIC),
            // The editor font is monospace; 'W' is as wide as any half-width cell.
            cellWidth = fm.charWidth('W').coerceAtLeast(1),
            cellHeight = fm.height.coerceAtLeast(1),
            ascent = fm.ascent,
        )
        cachedMetrics = metrics
        return metrics
    }

    private fun notifyGridSizeIfChanged() {
        val (columns, rows) = gridSize()
        if (columns == lastNotifiedColumns && rows == lastNotifiedRows) return
        lastNotifiedColumns = columns
        lastNotifiedRows = rows
        resizeListener?.invoke(columns, rows)
    }

    private fun cellAt(e: MouseEvent): Pair<Int, Int>? {
        val metrics = cellMetrics()
        val column = e.x / metrics.cellWidth
        val row = e.y / metrics.cellHeight
        // Viewport slack around the grid is not a cell; don't send out-of-range positions.
        if (column < 0 || row < 0 || column >= gridColumns || row >= gridRows) return null
        return column to row
    }

    private fun buttonOf(e: MouseEvent): String = when {
        SwingUtilities.isRightMouseButton(e) -> PointerButton.RIGHT
        SwingUtilities.isMiddleMouseButton(e) -> PointerButton.MIDDLE
        else -> PointerButton.LEFT
    }

    private fun modifiersOf(e: KeyEvent): List<String> = buildList {
        if (e.isControlDown) add("ctrl")
        if (e.isAltDown) add("alt")
        if (e.isShiftDown) add("shift")
        if (e.isMetaDown) add("meta")
    }

    /** Protocol names for non-printable keys (docs/protocol.md); null = not a named key. */
    private fun namedKey(e: KeyEvent): String? {
        val right = e.keyLocation == KeyEvent.KEY_LOCATION_RIGHT
        return when (val code = e.keyCode) {
            KeyEvent.VK_ENTER -> "Enter"
            KeyEvent.VK_TAB -> "Tab"
            KeyEvent.VK_ESCAPE -> "Escape"
            KeyEvent.VK_SPACE -> "Space"
            KeyEvent.VK_UP -> "Up"
            KeyEvent.VK_DOWN -> "Down"
            KeyEvent.VK_LEFT -> "Left"
            KeyEvent.VK_RIGHT -> "Right"
            KeyEvent.VK_BACK_SPACE -> "Backspace"
            KeyEvent.VK_DELETE -> "Delete"
            KeyEvent.VK_INSERT -> "Insert"
            KeyEvent.VK_HOME -> "Home"
            KeyEvent.VK_END -> "End"
            KeyEvent.VK_PAGE_UP -> "PageUp"
            KeyEvent.VK_PAGE_DOWN -> "PageDown"
            // Modifier keys are real down/up-forwarded keys — the preview's access-key
            // display (Alt gate) depends on seeing them.
            KeyEvent.VK_ALT -> if (right) "RightAlt" else "Alt"
            KeyEvent.VK_ALT_GRAPH -> "AltGr"
            KeyEvent.VK_CONTROL -> if (right) "RightCtrl" else "Ctrl"
            KeyEvent.VK_SHIFT -> if (right) "RightShift" else "Shift"
            KeyEvent.VK_META -> if (right) "RightMeta" else "Meta"
            in KeyEvent.VK_F1..KeyEvent.VK_F12 -> "F${code - KeyEvent.VK_F1 + 1}"
            else -> null
        }
    }

    private data class CellMetrics(
        val plain: Font,
        val bold: Font,
        val italic: Font,
        val boldItalic: Font,
        val cellWidth: Int,
        val cellHeight: Int,
        val ascent: Int,
    ) {
        fun font(bold: Boolean, italic: Boolean): Font = when {
            bold && italic -> boldItalic
            bold -> this.bold
            italic -> this.italic
            else -> plain
        }
    }

    /** One run of the cached screen state, style resolved at ingest. */
    private data class ResolvedRun(
        val text: String,
        val width: Int,
        val style: ResolvedStyle?,
    )

    private data class ResolvedStyle(
        /** null = terminal default (theme foreground). */
        val fg: Color?,
        /** null = terminal default (theme background). */
        val bg: Color?,
        val attrs: StyleAttrs,
    )

    private data class StyleAttrs(
        val bold: Boolean,
        val italic: Boolean,
        val underline: Boolean,
        val dim: Boolean,
        val strikethrough: Boolean,
        val reverse: Boolean,
    ) {
        companion object {
            val NONE = StyleAttrs(
                bold = false, italic = false, underline = false,
                dim = false, strikethrough = false, reverse = false,
            )

            fun of(attrs: List<String>?): StyleAttrs {
                if (attrs.isNullOrEmpty()) return NONE
                return StyleAttrs(
                    bold = "bold" in attrs,
                    italic = "italic" in attrs,
                    underline = "underline" in attrs,
                    dim = "dim" in attrs,
                    strikethrough = "strikethrough" in attrs,
                    reverse = "reverse" in attrs,
                )
            }
        }
    }

    companion object {
        // Terminal-default colors, paired with the preview theme base (see lightBase).
        private val DARK_DEFAULT_BG = Color(0x1e1e1e)
        private val DARK_DEFAULT_FG = Color(0xe5e5e5)
        private val LIGHT_DEFAULT_BG = Color(0xffffff)
        private val LIGHT_DEFAULT_FG = Color(0x333333)

        const val DEFAULT_COLUMNS = 80
        const val DEFAULT_ROWS = 24

        private val SELECTION_FILL = Color(64, 128, 255, 48)
        private val SELECTION_BORDER = Color(64, 128, 255, 192)

        /** Parses "#RRGGBB"; returns null for "default"/null/garbage (meaning: use the theme default). */
        private fun parseColor(value: String?): Color? {
            if (value == null || value == "default" || !value.startsWith("#") || value.length != 7) return null
            return try {
                Color(value.substring(1).toInt(16))
            } catch (_: NumberFormatException) {
                null
            }
        }

        /** 50% blend used for the "dim" attribute. */
        private fun blend(a: Color, b: Color): Color = Color(
            (a.red + b.red) / 2,
            (a.green + b.green) / 2,
            (a.blue + b.blue) / 2,
        )
    }
}
