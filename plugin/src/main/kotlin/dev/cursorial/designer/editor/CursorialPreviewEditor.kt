package dev.cursorial.designer.editor

import com.intellij.icons.AllIcons
import com.intellij.openapi.actionSystem.ActionManager
import com.intellij.openapi.actionSystem.ActionUpdateThread
import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.actionSystem.DefaultActionGroup
import com.intellij.openapi.actionSystem.ToggleAction
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.application.ReadAction
import com.intellij.openapi.diagnostic.logger
import com.intellij.openapi.editor.Document
import com.intellij.openapi.editor.event.DocumentEvent
import com.intellij.openapi.editor.event.DocumentListener
import com.intellij.openapi.fileEditor.FileDocumentManager
import com.intellij.openapi.fileEditor.FileEditor
import com.intellij.openapi.fileEditor.FileEditorState
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.Disposer
import com.intellij.openapi.util.UserDataHolderBase
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.ui.JBColor
import com.intellij.ui.components.JBLabel
import com.intellij.util.Alarm
import dev.cursorial.designer.protocol.AdvanceTimeCommand
import dev.cursorial.designer.protocol.CellSamplesEvent
import dev.cursorial.designer.protocol.ChildrenEvent
import dev.cursorial.designer.protocol.GetPropertiesCommand
import dev.cursorial.designer.protocol.SampleCellCommand
import dev.cursorial.designer.protocol.SetThemeCommand
import dev.cursorial.designer.protocol.DiagnosticsEvent
import dev.cursorial.designer.protocol.ErrorEvent
import dev.cursorial.designer.protocol.FrameEvent
import dev.cursorial.designer.protocol.HitTestCommand
import dev.cursorial.designer.protocol.HitTestResultEvent
import dev.cursorial.designer.protocol.InitializeCommand
import dev.cursorial.designer.protocol.KeyCommand
import dev.cursorial.designer.protocol.LoadXamlCommand
import dev.cursorial.designer.protocol.LogEvent
import dev.cursorial.designer.protocol.PointerCommand
import dev.cursorial.designer.protocol.PreviewerEvent
import dev.cursorial.designer.protocol.PropertiesEvent
import dev.cursorial.designer.protocol.ReadyEvent
import dev.cursorial.designer.protocol.ResizeCommand
import dev.cursorial.designer.protocol.ThemeBase
import dev.cursorial.designer.protocol.UnknownEvent
import dev.cursorial.designer.previewer.PreviewHostProcess
import dev.cursorial.designer.previewer.UserAssemblyLocator
import dev.cursorial.designer.settings.CursorialDesignerSettings
import java.awt.BorderLayout
import java.beans.PropertyChangeListener
import java.util.concurrent.atomic.AtomicInteger
import javax.swing.JComponent
import javax.swing.JPanel
import javax.swing.SwingConstants

/**
 * The preview half of the Cursorial split editor: hosts the [CellGridPanel] and drives an
 * out-of-process [PreviewHostProcess] that renders the XAML headlessly.
 */
class CursorialPreviewEditor(
    private val project: Project,
    private val file: VirtualFile,
    /** The paired text editor of the split view; selection changes sync its caret to the element's markup. */
    private val textEditor: com.intellij.openapi.fileEditor.TextEditor? = null,
) : UserDataHolderBase(), FileEditor {

    companion object {
        private val logger = logger<CursorialPreviewEditor>()
        private const val RELOAD_DEBOUNCE_MS = 300

        /** Parses "#RRGGBB" or "#RRGGBBAA" into an AWT color; null for anything else. */
        fun parseSwatch(hex: String?): java.awt.Color? {
            if (hex == null || !hex.startsWith("#")) return null
            return try {
                when (hex.length) {
                    7 -> java.awt.Color(hex.substring(1).toInt(16))
                    9 -> {
                        val rgba = hex.substring(1).toLong(16)
                        java.awt.Color(
                            (rgba shr 24 and 0xFF).toInt(),
                            (rgba shr 16 and 0xFF).toInt(),
                            (rgba shr 8 and 0xFF).toInt(),
                            (rgba and 0xFF).toInt(),
                        )
                    }
                    else -> null
                }
            } catch (_: NumberFormatException) {
                null
            }
        }
    }

    private val gridPanel = CellGridPanel()

    // One line, fixed height: the strip's content must never resize the preview above it.
    private val statusLabel = JBLabel(" ", SwingConstants.LEADING).apply {
        preferredSize = java.awt.Dimension(0, preferredSize.height)
    }

    // ── Properties panel (toggled from the toolbar) ─────────────────────
    // Presentation follows the framework's own InspectorDemo: one tree, "Name: value" per
    // property, expanding into the full provenance — kind/priority/resource key and every
    // style frame contending for the value (winning and losing alike).
    private val propertiesHeader = JBLabel("No selection", SwingConstants.LEADING)
    private val propertiesRoot = javax.swing.tree.DefaultMutableTreeNode()
    private val propertiesTreeModel = javax.swing.tree.DefaultTreeModel(propertiesRoot)
    private val propertiesTree = object : com.intellij.ui.treeStructure.Tree(propertiesTreeModel) {
        override fun getToolTipText(event: java.awt.event.MouseEvent): String? {
            val path = getPathForLocation(event.x, event.y) ?: return super.getToolTipText(event)
            val node = path.lastPathComponent as? javax.swing.tree.DefaultMutableTreeNode
            val data = node?.userObject as? PropertyNode ?: return super.getToolTipText(event)
            val explanation = data.explanation ?: return super.getToolTipText(event)
            return "<html><pre>${com.intellij.openapi.util.text.StringUtil.escapeXmlEntities(explanation)}</pre></html>"
        }
    }.apply {
        isRootVisible = false
        showsRootHandles = true
        // ColoredTreeCellRenderer over DefaultTreeCellRenderer: non-opaque and theme-correct (no
        // stray label-background blocks on dark themes), with two-tone rows — muted name, regular
        // value — in the InspectorDemo idiom. Color-like values get an inline swatch chip.
        cellRenderer = object : com.intellij.ui.ColoredTreeCellRenderer() {
            override fun customizeCellRenderer(
                tree: javax.swing.JTree, value: Any?, selected: Boolean, expanded: Boolean,
                leaf: Boolean, row: Int, hasFocus: Boolean,
            ) {
                val data = (value as? javax.swing.tree.DefaultMutableTreeNode)?.userObject as? PropertyNode ?: return
                icon = data.swatch?.let { com.intellij.util.ui.ColorIcon(12, it) }

                val text = data.toString()
                val separator = text.indexOf(": ")
                if (separator > 0) {
                    append(text.substring(0, separator + 2), com.intellij.ui.SimpleTextAttributes.GRAYED_ATTRIBUTES)
                    append(text.substring(separator + 2), com.intellij.ui.SimpleTextAttributes.REGULAR_ATTRIBUTES)
                } else {
                    append(text, com.intellij.ui.SimpleTextAttributes.REGULAR_ATTRIBUTES)
                }
            }
        }
        // Top-level property nodes stay collapsed by default; expanding one auto-expands its
        // "interesting" interior (Binding, the winning frame) so one click reveals the meat.
        addTreeExpansionListener(object : javax.swing.event.TreeExpansionListener {
            override fun treeExpanded(event: javax.swing.event.TreeExpansionEvent) {
                val node = event.path.lastPathComponent as? javax.swing.tree.DefaultMutableTreeNode ?: return
                for (child in node.children()) {
                    val childNode = child as javax.swing.tree.DefaultMutableTreeNode
                    if ((childNode.userObject as? PropertyNode)?.autoExpand == true)
                        expandPath(event.path.pathByAddingChild(childNode))
                }
            }

            override fun treeCollapsed(event: javax.swing.event.TreeExpansionEvent) {}
        })
    }
    private val propertiesPanel = JPanel(BorderLayout()).apply {
        add(propertiesHeader, BorderLayout.NORTH)
        add(com.intellij.ui.components.JBScrollPane(propertiesTree), BorderLayout.CENTER)
    }

    /** A property node's display text, tooltip derivation, expansion hint, and optional color swatch. */
    private class PropertyNode(
        private val text: String,
        val explanation: String? = null,
        val autoExpand: Boolean = false,
        val swatch: java.awt.Color? = null,
    ) {
        override fun toString(): String = text
    }

    private val gridScrollPane = com.intellij.ui.components.JBScrollPane(gridPanel).apply {
        border = null
        // Session resize tracks the VIEWPORT: while the frame fits, the grid fills it (auto-fit
        // unchanged); when the frame is larger (e.g. a pinned design size), scrollbars take over.
        viewport.addComponentListener(object : java.awt.event.ComponentAdapter() {
            override fun componentResized(e: java.awt.event.ComponentEvent) = gridPanel.refreshGridSize()
        })
    }

    private val splitter = com.intellij.ui.JBSplitter(false, 0.72f).apply {
        // Remember where the user parks the properties divider (global, PropertiesComponent-backed).
        setAndLoadSplitterProportionKey("cursorial.designer.preview.propertiesSplitter")
        firstComponent = gridScrollPane
        secondComponent = null // hidden until the toolbar toggle shows it
    }

    private val rootPanel: JPanel = JPanel(BorderLayout()).apply {
        add(buildToolbar(), BorderLayout.NORTH)
        add(splitter, BorderLayout.CENTER)
        add(statusLabel, BorderLayout.SOUTH)
    }

    @Volatile
    private var pendingPropertiesId: Int = -1

    @Volatile
    private var pendingSamplesId: Int = -1

    private var lastHitCell: Pair<Int, Int>? = null
    private var lastPropertiesEvent: PropertiesEvent? = null
    private var lastSamplesEvent: CellSamplesEvent? = null

    // Preview session state, re-applied on every host (re)start via onHostReady.
    private var themeBase: String = if (JBColor.isBright()) ThemeBase.LIGHT else ThemeBase.DARK
    private var colorTier: String? = null
    private var capabilitiesProfile: String = "kitty-truecolor"

    /** Streams virtual time into the preview (~30 fps) so animations run while "playing". */
    private val playTimer = javax.swing.Timer(33) {
        hostProcess?.sendCommand(AdvanceTimeCommand(33))
    }

    // Restart-on-rebuild: the located user assemblies and their last-observed stamps. Watching
    // the output file (rather than IDE build events) catches IDE builds, CLI builds, and
    // anything else that produces fresh bits. A change must hold stable for one tick before the
    // restart fires, so a mid-write file is never loaded.
    private val watchedAssemblies = HashMap<String, Long>()
    private var pendingAssemblyChange: Map<String, Long>? = null
    private val rebuildWatchTimer = javax.swing.Timer(2000) { checkForRebuild() }.apply { start() }

    private fun checkForRebuild() {
        if (watchedAssemblies.isEmpty()) return
        val current = watchedAssemblies.keys.associateWith { java.io.File(it).lastModified() }
        if (current == watchedAssemblies) {
            pendingAssemblyChange = null
            return
        }

        // Changed since load: wait until two consecutive ticks agree (the build finished writing).
        if (current == pendingAssemblyChange) {
            pendingAssemblyChange = null
            watchedAssemblies.putAll(current)
            statusLabel.text = "Project output changed — restarting preview…"
            hostProcess?.restart()
        } else {
            pendingAssemblyChange = current
        }
    }

    private val reloadAlarm = Alarm(Alarm.ThreadToUse.SWING_THREAD, this)
    private val requestIds = AtomicInteger()

    @Volatile
    private var pendingHitTestId: Int = -1

    // The most recent hit-test chain (innermost first) and the currently selected depth within
    // it — '[' walks outward toward the root, ']' back inward. Pointer selection alone cannot
    // reach every layer of a deep templated tree.
    private var selectionChain: List<dev.cursorial.designer.protocol.HitTestElement> = emptyList()
    private var selectionIndex: Int = 0

    private val document: Document? =
        ReadAction.compute<Document?, RuntimeException> {
            FileDocumentManager.getInstance().getDocument(file)
        }

    private var hostProcess: PreviewHostProcess? = null

    private val hostListener = object : PreviewHostProcess.Listener {
        override fun onEvent(event: PreviewerEvent) {
            when (event) {
                is ReadyEvent -> onHostReady(event)
                is FrameEvent -> onEdt { gridPanel.render(event) }
                is DiagnosticsEvent -> onEdt { showDiagnostics(event) }
                is HitTestResultEvent -> onEdt { showHitTestResult(event) }
                is PropertiesEvent -> onEdt { showProperties(event) }
                is CellSamplesEvent -> onEdt { showCellSamples(event) }
                is ChildrenEvent -> logger.info("children(replyTo=${event.replyTo}): ${event.elements.size} of #${event.parentId}")
                is dev.cursorial.designer.protocol.CompletionsEvent,
                is dev.cursorial.designer.protocol.HoverInfoEvent,
                is dev.cursorial.designer.protocol.DefinitionEvent -> {} // language-service replies; not for preview editors
                is ErrorEvent -> onEdt {
                    logger.warn("Previewer error: ${event.message}${event.detail?.let { "\n$it" } ?: ""}")
                    statusLabel.text = "Previewer error: ${event.message}"
                    event.detail?.let { statusLabel.toolTipText = "<html><pre>${it.take(2000)}</pre></html>" }
                }
                is LogEvent -> logger.info("PreviewHost [${event.level}]: ${event.message}")
                is UnknownEvent -> logger.warn("Unknown event type \"${event.type}\" from preview host")
            }
        }

        override fun onTerminated(exitCode: Int, willRestart: Boolean) {
            onEdt {
                statusLabel.text =
                    if (willRestart) "Previewer crashed (exit code $exitCode); restarting…"
                    else "Previewer terminated (exit code $exitCode)"
            }
        }

        override fun onStderrLine(line: String) {
            if (logger.isDebugEnabled) logger.debug("PreviewHost stderr: $line")
        }
    }

    init {
        val hostDll = CursorialDesignerSettings.getInstance(project).previewHostDllPath(file)
        if (hostDll == null) {
            statusLabel.text =
                "Cursorial PreviewHost not found; set ${CursorialDesignerSettings.ENV_PREVIEW_HOST_DLL} " +
                    "or build it to ${CursorialDesignerSettings.DEFAULT_HOST_RELATIVE_PATH}"
        } else {
            val process = PreviewHostProcess(hostDll)
            Disposer.register(this, process)
            process.addListener(hostListener)
            hostProcess = process
            process.start()
        }

        gridPanel.resizeListener = { columns, rows ->
            hostProcess?.sendCommand(ResizeCommand(columns, rows))
        }
        gridPanel.pointerListener = { kind, column, row, button ->
            hostProcess?.sendCommand(PointerCommand(kind, column, row, button))
        }
        gridPanel.hitTestListener = { column, row ->
            lastHitCell = column to row
            val id = requestIds.incrementAndGet()
            pendingHitTestId = id
            hostProcess?.sendCommand(HitTestCommand(id, column, row))
        }
        gridPanel.keyListener = { key, modifiers, kind ->
            hostProcess?.sendCommand(KeyCommand(key, modifiers, kind))
        }
        gridPanel.treeWalkListener = { outward -> walkSelection(outward) }

        document?.addDocumentListener(
            object : DocumentListener {
                override fun documentChanged(event: DocumentEvent) {
                    reloadAlarm.cancelAllRequests()
                    reloadAlarm.addRequest({ sendLoadXaml() }, RELOAD_DEBOUNCE_MS)
                }
            },
            this,
        )
    }

    private fun onHostReady(event: ReadyEvent) {
        logger.info("Preview host ready: protocol=${event.protocolVersion}, pid=${event.pid}")
        val process = hostProcess ?: return

        val (columns, rows) = gridPanel.gridSize()
        process.sendCommand(InitializeCommand(
            columns = columns,
            rows = rows,
            capabilities = capabilitiesProfile,
            themeBase = themeBase,
            colorTier = colorTier,
        ))
        sendLoadXaml()
        // Nudge the host to produce a first frame even for purely animation-driven UIs.
        process.sendCommand(AdvanceTimeCommand(0))
    }

    private fun sendLoadXaml() {
        val process = hostProcess ?: return
        val document = document ?: return
        val xaml = ReadAction.compute<String, RuntimeException> { document.text }

        val located = UserAssemblyLocator.locate(file)
        located.problem?.let { statusLabel.text = it }

        // sendLoadXaml can run on the host pump thread; the watch map lives on the EDT (timer).
        val stamps = located.assemblies.associateWith { java.io.File(it).lastModified() }
        onEdt {
            watchedAssemblies.clear()
            pendingAssemblyChange = null
            watchedAssemblies.putAll(stamps)
        }

        process.sendCommand(
            LoadXamlCommand(
                xaml = xaml,
                sourceUri = file.url,
                assemblies = located.assemblies,
            ),
        )
    }

    private var lastErrorCount = -1

    /**
     * The editor's annotator owns diagnostics now (squiggles + the Problems view). The strip
     * keeps only a quiet stale-preview cue, updated when the error COUNT changes — never per
     * keystroke, so typing cannot jitter the pane.
     */
    private fun showDiagnostics(event: DiagnosticsEvent) {
        val errors = event.items.filter { it.severity == "error" }
        for (item in errors)
            logger.warn("Preview load problem: ${item.code} @${item.line}:${item.column} ${item.message}")

        if (errors.size == lastErrorCount) return
        lastErrorCount = errors.size
        statusLabel.text = if (errors.isEmpty()) " "
            else "⚠ ${errors.size} problem${if (errors.size == 1) "" else "s"} — preview shows the last good state"
        // Runtime problems (instantiation, layout) exist only on the PREVIEW path — the
        // annotator never instantiates, so they can't reach the Problems view. The strip's
        // tooltip is where their details live.
        statusLabel.toolTipText = if (errors.isEmpty()) null
            else "<html>" + errors.joinToString("<br>") { "${it.code} @${it.line}:${it.column} ${it.message}" } + "</html>"
    }

    private fun showHitTestResult(event: HitTestResultEvent) {
        if (event.replyTo != pendingHitTestId) return
        selectionChain = event.elements
        selectionIndex = 0
        showSelectionAt(selectionIndex)
    }

    /** '[' walks outward (toward the root), ']' back inward. No-op without a selection. */
    private fun walkSelection(outward: Boolean) {
        if (selectionChain.isEmpty()) return
        selectionIndex = (selectionIndex + if (outward) 1 else -1).coerceIn(0, selectionChain.lastIndex)
        showSelectionAt(selectionIndex)
    }

    private fun showSelectionAt(index: Int) {
        val element = selectionChain.getOrNull(index)
        gridPanel.showSelection(element?.bounds)
        requestProperties(element)
        val fromTemplate = syncCaret(index)
        statusLabel.text = element?.let {
            val depth = if (selectionChain.size > 1) "  (${index + 1}/${selectionChain.size}, [ / ] to walk)" else ""
            val provenance = if (fromTemplate) "  · from template" else ""
            "${it.elementType ?: "element"} ${it.name ?: "#${it.elementId}"}$depth$provenance"
        } ?: ""
    }

    /** Fetches the selected element's properties when the panel is visible. */
    private fun requestProperties(element: dev.cursorial.designer.protocol.HitTestElement?) {
        if (splitter.secondComponent == null) return
        if (element == null) {
            propertiesHeader.text = "No selection"
            lastPropertiesEvent = null
            lastSamplesEvent = null
            propertiesRoot.removeAllChildren()
            propertiesTreeModel.reload()
            return
        }

        val id = requestIds.incrementAndGet()
        pendingPropertiesId = id
        hostProcess?.sendCommand(GetPropertiesCommand(id, element.elementId))

        lastHitCell?.let { (column, row) ->
            val sampleId = requestIds.incrementAndGet()
            pendingSamplesId = sampleId
            hostProcess?.sendCommand(SampleCellCommand(sampleId, column, row))
        }
    }

    private fun showProperties(event: PropertiesEvent) {
        if (event.replyTo != pendingPropertiesId) return
        val element = selectionChain.getOrNull(selectionIndex) ?: return

        propertiesHeader.text = buildString {
            append("<html><b>")
            append(element.elementType ?: "element")
            element.name?.let { append("  '").append(it).append('\'') }
            append("</b>  —  ").append(event.items.size).append(" set propert").append(if (event.items.size == 1) "y" else "ies")
            event.classes?.let { append("<br>Classes: ").append(it) }
            append("</html>")
        }

        lastPropertiesEvent = event
        rebuildInspectorTree()
    }

    private fun showCellSamples(event: CellSamplesEvent) {
        if (event.replyTo != pendingSamplesId) return
        lastSamplesEvent = event
        rebuildInspectorTree()
    }

    /** The inspector tree: a Layers drilldown for the clicked cell, then the property nodes. */
    private fun rebuildInspectorTree() {
        propertiesRoot.removeAllChildren()
        lastSamplesEvent?.let { propertiesRoot.add(buildLayersNode(it)) }
        lastPropertiesEvent?.let { for (item in it.items) propertiesRoot.add(buildPropertyNode(item)) }
        propertiesTreeModel.reload()
    }

    private fun buildLayersNode(event: CellSamplesEvent): javax.swing.tree.DefaultMutableTreeNode {
        fun node(text: String, swatch: java.awt.Color? = null) =
            javax.swing.tree.DefaultMutableTreeNode(PropertyNode(text, swatch = swatch))

        val layers = node("Layers: ${event.layers.size} at (${event.column}, ${event.row})")
        for (i in event.layers.indices.reversed()) { // topmost first, like the InspectorDemo
            val layer = event.layers[i]
            val glyph = layer.grapheme?.let { "\"$it\"" } ?: "(null)"
            val layerNode = node("[$i]: $glyph [${layer.kind ?: "—"}] ${layer.element ?: "?"}")

            layer.element?.let { layerNode.add(node("Element: $it")) }
            layerNode.add(node("SurfaceZ: ${layer.surfaceZ}"))

            val parameters = node("Parameters")
            layer.parameters.clip?.let { parameters.add(node("Clip: $it")) }
            parameters.add(node("Mode: ${layer.parameters.mode ?: "(null)"}"))
            parameters.add(node("OffsetColumn: ${layer.parameters.offsetColumn}"))
            parameters.add(node("OffsetRow: ${layer.parameters.offsetRow}"))
            parameters.add(node("Opacity: ${layer.parameters.opacity}"))
            layerNode.add(parameters)

            layer.style?.let { style ->
                val styleNode = node("Style")
                styleNode.add(node("Foreground: ${style.fg ?: "default"}", parseSwatch(style.fg)))
                styleNode.add(node("Background: ${style.bg ?: "default"}", parseSwatch(style.bg)))
                styleNode.add(node("Attributes: ${style.attrs?.joinToString(", ") ?: "None"}"))
                style.underline?.let { styleNode.add(node("UnderlineStyle: $it")) }
                style.underlineColor?.let { styleNode.add(node("UnderlineColor: $it", parseSwatch(it))) }
                style.link?.let { styleNode.add(node("Hyperlink: $it")) }
                layerNode.add(styleNode)
            }

            layers.add(layerNode)
        }
        return layers
    }

    private fun buildPropertyNode(item: dev.cursorial.designer.protocol.PropertyItem): javax.swing.tree.DefaultMutableTreeNode {
        fun node(text: String, explanation: String? = null, autoExpand: Boolean = false) =
            javax.swing.tree.DefaultMutableTreeNode(PropertyNode(text, explanation, autoExpand))

        val name = item.declaringType?.let { "$it.${item.name}" } ?: item.name
        val property = javax.swing.tree.DefaultMutableTreeNode(
            PropertyNode("$name: ${item.value ?: ""}", item.explanation, swatch = parseSwatch(item.swatch)))

        item.valueSource?.let { property.add(node("Kind: $it")) }
        item.priority?.let { property.add(node("Priority: $it")) }
        item.basePriority?.let { property.add(node("BasePriority: $it")) }
        if (item.isAnimated == true) property.add(node("IsAnimated: true"))
        item.resourceKey?.let { property.add(node("Resource Key: $it")) }

        if (item.bindings != null) {
            val bindingNode = node("Binding", autoExpand = true)
            item.bindingTarget?.let { bindingNode.add(node("Target: $it")) }
            item.bindings.forEachIndexed { i, binding ->
                val expressionNode = node("Bindings[$i]: ${binding.path ?: "?"} — ${binding.status ?: "?"}", autoExpand = i == 0)
                binding.lane?.let { expressionNode.add(node("Lane: $it")) }
                binding.path?.let { expressionNode.add(node("Path: $it")) }
                binding.status?.let { expressionNode.add(node("Status: $it")) }
                binding.effectiveMode?.let { expressionNode.add(node("EffectiveMode: $it")) }
                binding.resolvedSourceChain?.let { expressionNode.add(node("ResolvedSourceChain: $it")) }
                binding.value?.let { expressionNode.add(node("LastProducedValue: $it")) }
                expressionNode.add(node("LastFailure: ${binding.lastFailure ?: "None"}"))
                bindingNode.add(expressionNode)
            }
            property.add(bindingNode)
        }

        item.frames?.forEachIndexed { i, frame ->
            val winning = frame.status == "Winning"
            val frameNode = javax.swing.tree.DefaultMutableTreeNode(PropertyNode(
                "Frames[$i]: ${frame.selector ?: "?"} — ${frame.status ?: "?"}",
                autoExpand = winning, swatch = parseSwatch(frame.swatch)))
            frame.layer?.let { frameNode.add(node("Layer: $it")) }
            frame.selector?.let { frameNode.add(node("Selector: $it")) }
            frameNode.add(node("IsActive: ${frame.isActive}"))
            frameNode.add(node("HasValue: ${frame.hasValue}"))
            frame.value?.let { frameNode.add(node("LastProducedValue: $it")) }
            frame.resourceKey?.let { frameNode.add(node("ResourceKey: $it")) }
            frame.status?.let { frameNode.add(node("Status: $it")) }
            frame.sortKey?.let { frameNode.add(node("SortKey: $it")) }
            property.add(frameNode)
        }

        return property
    }

    /**
     * Moves the text editor's caret to the selected element's markup. A document-owned element
     * syncs directly; a template-expanded element (foreign or untracked span) falls back to the
     * nearest document-owned ancestor in the chain — the logical-tree mapping. Returns whether
     * the fallback was used.
     */
    private fun syncCaret(index: Int): Boolean {
        val editor = textEditor?.editor ?: return false
        val selected = selectionChain.getOrNull(index) ?: return false

        var target = selected
        var fromTemplate = false
        if (target.inDocument != true) {
            target = (index + 1..selectionChain.lastIndex)
                .map { selectionChain[it] }
                .firstOrNull { it.inDocument == true } ?: return false
            fromTemplate = true
        }

        val line = target.line ?: return fromTemplate
        val position = com.intellij.openapi.editor.LogicalPosition(
            (line - 1).coerceAtLeast(0),
            ((target.column ?: 1) - 1).coerceAtLeast(0),
        )
        editor.caretModel.moveToLogicalPosition(position)
        editor.scrollingModel.scrollToCaret(com.intellij.openapi.editor.ScrollType.MAKE_VISIBLE)
        return fromTemplate
    }

    private fun onEdt(action: () -> Unit) {
        val application = ApplicationManager.getApplication()
        if (application.isDispatchThread) action() else application.invokeLater(action)
    }

    // ------------------------------------------------------------------
    // Preview toolbar
    // ------------------------------------------------------------------

    private fun buildToolbar(): JComponent {
        val group = DefaultActionGroup().apply {
            add(object : ToggleAction("Dark", "Toggle the preview between the dark and light theme base", null) {
                override fun getActionUpdateThread() = ActionUpdateThread.EDT
                override fun isSelected(e: AnActionEvent) = themeBase == ThemeBase.DARK
                override fun setSelected(e: AnActionEvent, state: Boolean) {
                    themeBase = if (state) ThemeBase.DARK else ThemeBase.LIGHT
                    hostProcess?.sendCommand(SetThemeCommand(themeBase = themeBase))
                }
            })
            add(DefaultActionGroup("Tier", true).apply {
                templatePresentation.description = "Preview under a specific color tier"
                for (tier in listOf("truecolor", "ansi256", "ansi16", "nocolor"))
                    add(tierAction(tier))
            })
            add(DefaultActionGroup("Terminal Profiles", true).apply {
                templatePresentation.description = "Preview against a synthetic terminal capability profile (restarts the preview)"
                for (profile in listOf("kitty-truecolor", "ansi16", "no-motion", "kitty-graphics", "sixel", "iterm2"))
                    add(capabilityAction(profile))
            })
            addSeparator()
            add(object : ToggleAction("Play", "Stream time into the preview so animations and timers run", AllIcons.Actions.Execute) {
                override fun getActionUpdateThread() = ActionUpdateThread.EDT
                override fun isSelected(e: AnActionEvent) = playTimer.isRunning
                override fun setSelected(e: AnActionEvent, state: Boolean) {
                    if (state) playTimer.start() else playTimer.stop()
                }
                override fun update(e: AnActionEvent) {
                    super.update(e)
                    e.presentation.icon = if (playTimer.isRunning) AllIcons.Actions.Suspend else AllIcons.Actions.Execute
                    e.presentation.text = if (playTimer.isRunning) "Pause" else "Play"
                }
            })
            add(object : ToggleAction("Select", "Clicks select elements for inspection instead of driving the app (Alt+Click always selects)", null) {
                override fun getActionUpdateThread() = ActionUpdateThread.EDT
                override fun isSelected(e: AnActionEvent) = gridPanel.selectMode
                override fun setSelected(e: AnActionEvent, state: Boolean) {
                    gridPanel.selectMode = state
                    if (!state) {
                        selectionChain = emptyList()
                        gridPanel.showSelection(null)
                        statusLabel.text = ""
                    }
                }
            })
            add(object : ToggleAction("Properties", "Show the selected element's set properties with provenance", null) {
                override fun getActionUpdateThread() = ActionUpdateThread.EDT
                override fun isSelected(e: AnActionEvent) = splitter.secondComponent != null
                override fun setSelected(e: AnActionEvent, state: Boolean) {
                    splitter.secondComponent = if (state) propertiesPanel else null
                    if (state) requestProperties(selectionChain.getOrNull(selectionIndex))
                }
            })
            addSeparator()
            add(object : AnAction("Restart", "Restart the preview host process and reload the document", AllIcons.Actions.Restart) {
                override fun getActionUpdateThread() = ActionUpdateThread.EDT
                override fun actionPerformed(e: AnActionEvent) {
                    hostProcess?.restart()
                }
            })
        }

        val toolbar = ActionManager.getInstance().createActionToolbar("CursorialDesignerPreview", group, true)
        toolbar.targetComponent = gridPanel
        return toolbar.component
    }

    private fun tierAction(tier: String): AnAction = object : ToggleAction(tier) {
        override fun getActionUpdateThread() = ActionUpdateThread.EDT
        override fun isSelected(e: AnActionEvent) = colorTier == tier
        override fun setSelected(e: AnActionEvent, state: Boolean) {
            if (!state) return
            colorTier = tier
            hostProcess?.sendCommand(SetThemeCommand(colorTier = tier))
        }
    }

    private fun capabilityAction(profile: String): AnAction = object : ToggleAction(profile) {
        override fun getActionUpdateThread() = ActionUpdateThread.EDT
        override fun isSelected(e: AnActionEvent) = capabilitiesProfile == profile
        override fun setSelected(e: AnActionEvent, state: Boolean) {
            if (!state || capabilitiesProfile == profile) return
            capabilitiesProfile = profile
            // The capability snapshot is fixed at initialize; a fresh host picks it up via
            // onHostReady, which re-sends initialize + loadXaml with the current selections.
            hostProcess?.restart()
        }
    }

    // ------------------------------------------------------------------
    // FileEditor implementation
    // ------------------------------------------------------------------

    override fun getComponent(): JComponent = rootPanel

    override fun getPreferredFocusedComponent(): JComponent = gridPanel

    override fun getName(): String = "Cursorial Preview"

    override fun getFile(): VirtualFile = file

    override fun setState(state: FileEditorState) {}

    override fun isModified(): Boolean = false

    override fun isValid(): Boolean = file.isValid

    override fun addPropertyChangeListener(listener: PropertyChangeListener) {}

    override fun removePropertyChangeListener(listener: PropertyChangeListener) {}

    override fun dispose() {
        playTimer.stop()
        rebuildWatchTimer.stop()
        // hostProcess is disposed through Disposer (registered in init).
    }
}
