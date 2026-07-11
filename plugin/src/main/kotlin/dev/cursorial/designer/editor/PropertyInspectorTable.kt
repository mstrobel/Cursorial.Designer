package dev.cursorial.designer.editor

import com.intellij.ui.ColoredTableCellRenderer
import com.intellij.ui.ColoredTreeCellRenderer
import com.intellij.ui.SimpleTextAttributes
import com.intellij.ui.components.JBScrollPane
import com.intellij.ui.treeStructure.treetable.ListTreeTableModelOnColumns
import com.intellij.ui.treeStructure.treetable.TreeColumnInfo
import com.intellij.ui.treeStructure.treetable.TreeTable
import com.intellij.util.ui.ColorIcon
import com.intellij.util.ui.ColumnInfo
import com.intellij.util.ui.JBUI
import dev.cursorial.designer.protocol.CellSamplesEvent
import dev.cursorial.designer.protocol.PropertiesEvent
import dev.cursorial.designer.protocol.PropertyItem
import java.awt.Color
import java.awt.Component
import javax.swing.JTable
import javax.swing.JTree
import javax.swing.Timer
import javax.swing.event.TreeExpansionEvent
import javax.swing.event.TreeExpansionListener
import javax.swing.tree.DefaultMutableTreeNode
import javax.swing.tree.TreePath

/**
 * The property inspector as a tree/grid hybrid: the tree column carries only the STABLE name
 * (property name, frame selector — never a value), values render in their own column with
 * swatches and a provenance badge. Updates DIFF nodes in place by stable key, so expansion
 * survives refreshes by construction and changed values flash debugger-style — the design
 * Mike specified for live updates (rebuild-and-reload is jarring; identity must never contain
 * mutable data).
 */
class PropertyInspectorTable {

    /** One immutable row description; [key] is the node's identity within its parent. */
    private data class Spec(
        val key: String,
        val name: String,
        val value: String? = null,
        val swatch: Color? = null,
        val badge: String? = null,
        val explanation: String? = null,
        val autoExpand: Boolean = false,
        val children: List<Spec> = emptyList(),
    )

    /** A mutable node: identity is [key]; everything else updates in place. */
    private class Node(var spec: Spec) : DefaultMutableTreeNode() {
        var changedAt: Long = 0
        val key: String get() = spec.key
    }

    private val root = DefaultMutableTreeNode()

    private val valueColumn = object : ColumnInfo<Any, Any>("Value") {
        override fun valueOf(item: Any): Any = item
    }

    private val model = ListTreeTableModelOnColumns(root, arrayOf(TreeColumnInfo("Property"), valueColumn))

    private val flashTimer = Timer(150) {
        if (System.currentTimeMillis() - lastFlashAt > FLASH_MILLIS) (it.source as Timer).stop()
        table.repaint()
    }

    private var lastFlashAt = 0L

    val table: TreeTable = object : TreeTable(model) {
        override fun getToolTipText(event: java.awt.event.MouseEvent): String? {
            val row = rowAtPoint(event.point)
            if (row < 0) return super.getToolTipText(event)
            val node = tree.getPathForRow(row)?.lastPathComponent as? Node ?: return super.getToolTipText(event)
            val explanation = node.spec.explanation ?: return super.getToolTipText(event)
            return "<html><pre>${com.intellij.openapi.util.text.StringUtil.escapeXmlEntities(explanation)}</pre></html>"
        }
    }.apply {
        setRootVisible(false)
        tree.showsRootHandles = true
        tree.cellRenderer = object : ColoredTreeCellRenderer() {
            override fun customizeCellRenderer(
                tree: JTree, value: Any?, selected: Boolean, expanded: Boolean,
                leaf: Boolean, row: Int, hasFocus: Boolean,
            ) {
                val node = value as? Node ?: return
                append(node.spec.name, SimpleTextAttributes.REGULAR_ATTRIBUTES)
            }
        }
        setDefaultRenderer(Any::class.java, object : ColoredTableCellRenderer() {
            override fun customizeCellRenderer(
                table: JTable, value: Any?, selected: Boolean, hasFocus: Boolean, row: Int, column: Int,
            ) {
                val node = value as? Node ?: return
                if (!selected && System.currentTimeMillis() - node.changedAt < FLASH_MILLIS)
                    background = JBUI.CurrentTheme.Banner.INFO_BACKGROUND

                node.spec.swatch?.let { icon = ColorIcon(12, it) }
                node.spec.value?.let { append(it, SimpleTextAttributes.REGULAR_ATTRIBUTES) }
                node.spec.badge?.let {
                    append("  ")
                    append(it, SimpleTextAttributes.GRAYED_SMALL_ATTRIBUTES)
                }
            }
        })
        // Expanding a node auto-expands its "interesting" interior (the Binding group, the
        // winning frame) so one click reveals the meat — same policy as the old tree.
        tree.addTreeExpansionListener(object : TreeExpansionListener {
            override fun treeExpanded(event: TreeExpansionEvent) {
                val node = event.path.lastPathComponent as? DefaultMutableTreeNode ?: return
                for (child in node.children()) {
                    if ((child as? Node)?.spec?.autoExpand == true)
                        tree.expandPath(event.path.pathByAddingChild(child))
                }
            }

            override fun treeCollapsed(event: TreeExpansionEvent) {}
        })
        columnModel.getColumn(0).preferredWidth = JBUI.scale(220)
        columnModel.getColumn(1).preferredWidth = JBUI.scale(280)
    }

    val component = JBScrollPane(table)

    fun clear() {
        root.removeAllChildren()
        model.reload()
    }

    /** Rebuilds the content by DIFFING against the existing nodes — expansion survives. */
    fun show(samples: CellSamplesEvent?, properties: PropertiesEvent?) {
        val specs = buildList {
            samples?.let { add(layersSpec(it)) }
            properties?.items?.forEach { add(propertySpec(it)) }
        }
        reconcile(root, specs)
    }

    // ── Diffing ─────────────────────────────────────────────────────────────

    private fun reconcile(parent: DefaultMutableTreeNode, specs: List<Spec>) {
        val existing = HashMap<String, Node>()
        for (child in parent.children())
            (child as? Node)?.let { existing[it.key] = it }

        // Remove nodes whose keys vanished (collapses only those subtrees, nothing else).
        val wanted = specs.mapTo(HashSet()) { it.key }
        val doomed = parent.children().asSequence().filterIsInstance<Node>().filter { it.key !in wanted }.toList()
        for (node in doomed) {
            existing.remove(node.key)
            model.removeNodeFromParent(node)
        }

        specs.forEachIndexed { index, spec ->
            val node = existing[spec.key]
            if (node == null) {
                model.insertNodeInto(build(spec), parent, index.coerceAtMost(parent.childCount))
                return@forEachIndexed
            }

            // Same identity: move only if order genuinely changed (rare; moving drops the
            // subtree's expansion, updating in place never does).
            if (parent.getIndex(node) != index && index < parent.childCount) {
                model.removeNodeFromParent(node)
                model.insertNodeInto(node, parent, index)
            }

            val changed = node.spec.value != spec.value || node.spec.badge != spec.badge
            val nameChanged = node.spec.name != spec.name || node.spec.swatch != spec.swatch
            node.spec = spec
            if (changed) {
                node.changedAt = System.currentTimeMillis()
                lastFlashAt = node.changedAt
                if (!flashTimer.isRunning) flashTimer.start()
            }

            if (changed || nameChanged)
                model.nodeChanged(node)
            reconcile(node, spec.children)
        }
    }

    private fun build(spec: Spec): Node {
        val node = Node(spec)
        for (child in spec.children)
            node.add(build(child))
        return node
    }

    // ── Spec builders (ported from the flat-tree inspector, name/value split) ───────────────

    private fun propertySpec(item: PropertyItem): Spec {
        val name = item.declaringType?.let { "$it.${item.name}" } ?: item.name
        val details = buildList {
            item.valueSource?.let { add(Spec("kind", "Kind", it)) }
            item.priority?.let { add(Spec("priority", "Priority", it)) }
            item.basePriority?.let { add(Spec("basePriority", "BasePriority", it)) }
            if (item.isAnimated == true) add(Spec("animated", "IsAnimated", "true"))
            item.resourceKey?.let { add(Spec("resourceKey", "Resource Key", it)) }

            if (item.bindings != null) {
                val expressions = item.bindings.mapIndexed { i, binding ->
                    Spec(
                        key = "expr:${binding.lane}:${binding.path}:$i",
                        name = binding.path ?: "?",
                        value = binding.status,
                        autoExpand = i == 0,
                        children = buildList {
                            binding.lane?.let { add(Spec("lane", "Lane", it)) }
                            binding.status?.let { add(Spec("status", "Status", it)) }
                            binding.effectiveMode?.let { add(Spec("mode", "EffectiveMode", it)) }
                            binding.resolvedSourceChain?.let { add(Spec("chain", "ResolvedSourceChain", it)) }
                            binding.value?.let { add(Spec("produced", "LastProducedValue", it)) }
                            add(Spec("failure", "LastFailure", binding.lastFailure ?: "None"))
                        },
                    )
                }
                add(Spec("binding", "Binding", item.bindingTarget, autoExpand = true, children = expressions))
            }

            // Frame identity is the SELECTOR (not the index): rules keep their nodes across
            // updates even as match order shifts.
            item.frames?.forEachIndexed { i, frame ->
                val winning = frame.status == "Winning"
                add(Spec(
                    key = "frame:${frame.layer}:${frame.selector ?: i.toString()}",
                    name = frame.selector ?: "?",
                    value = frame.status,
                    swatch = CursorialPreviewEditor.parseSwatch(frame.swatch),
                    autoExpand = winning,
                    children = buildList {
                        frame.layer?.let { add(Spec("layer", "Layer", it)) }
                        add(Spec("active", "IsActive", frame.isActive.toString()))
                        add(Spec("hasValue", "HasValue", frame.hasValue.toString()))
                        frame.value?.let { add(Spec("produced", "LastProducedValue", it)) }
                        frame.resourceKey?.let { add(Spec("resourceKey", "ResourceKey", it)) }
                        frame.sortKey?.let { add(Spec("sortKey", "SortKey", it)) }
                    },
                ))
            }
        }

        return Spec(
            key = "prop:$name",
            name = name,
            value = item.value ?: "",
            swatch = CursorialPreviewEditor.parseSwatch(item.swatch),
            badge = item.valueSource,
            explanation = item.explanation,
            children = details,
        )
    }

    private fun layersSpec(event: CellSamplesEvent): Spec {
        val layers = event.layers.indices.reversed().map { i -> // topmost first, like the InspectorDemo
            val layer = event.layers[i]
            val glyph = layer.grapheme?.let { "\"$it\"" } ?: "(null)"
            Spec(
                key = "layer:${layer.surfaceZ}:${layer.element}",
                name = "[$i] ${layer.element ?: "?"}",
                value = "$glyph [${layer.kind ?: "—"}]",
                children = buildList {
                    add(Spec("z", "SurfaceZ", layer.surfaceZ.toString()))
                    add(Spec("params", "Parameters", null, children = buildList {
                        layer.parameters.clip?.let { add(Spec("clip", "Clip", it)) }
                        add(Spec("mode", "Mode", layer.parameters.mode ?: "(null)"))
                        add(Spec("offc", "OffsetColumn", layer.parameters.offsetColumn.toString()))
                        add(Spec("offr", "OffsetRow", layer.parameters.offsetRow.toString()))
                        add(Spec("opacity", "Opacity", layer.parameters.opacity.toString()))
                    }))
                    layer.style?.let { style ->
                        add(Spec("style", "Style", null, children = buildList {
                            add(Spec("fg", "Foreground", style.fg ?: "default", CursorialPreviewEditor.parseSwatch(style.fg)))
                            add(Spec("bg", "Background", style.bg ?: "default", CursorialPreviewEditor.parseSwatch(style.bg)))
                            add(Spec("attrs", "Attributes", style.attrs?.joinToString(", ") ?: "None"))
                            style.underline?.let { add(Spec("ul", "UnderlineStyle", it)) }
                            style.underlineColor?.let { add(Spec("ulc", "UnderlineColor", it, CursorialPreviewEditor.parseSwatch(it))) }
                            style.link?.let { add(Spec("link", "Hyperlink", it)) }
                        }))
                    }
                },
            )
        }

        return Spec(
            key = "layers",
            name = "Layers",
            value = "${event.layers.size} at (${event.column}, ${event.row})",
            children = layers,
        )
    }

    private companion object {
        const val FLASH_MILLIS = 1200L
    }
}
