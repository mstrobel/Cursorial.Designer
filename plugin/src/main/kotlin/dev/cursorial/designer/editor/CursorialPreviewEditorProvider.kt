package dev.cursorial.designer.editor

import com.intellij.ide.util.PropertiesComponent
import com.intellij.openapi.fileEditor.FileEditor
import com.intellij.openapi.fileEditor.FileEditorPolicy
import com.intellij.openapi.fileEditor.FileEditorProvider
import com.intellij.openapi.fileEditor.TextEditor
import com.intellij.openapi.fileEditor.TextEditorWithPreview
import com.intellij.openapi.fileEditor.impl.text.TextEditorProvider
import com.intellij.openapi.project.DumbAware
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import java.io.IOException
import java.nio.charset.StandardCharsets

/**
 * Provides a split (text + live preview) editor for Cursorial XAML files.
 *
 * Detection (v1, deliberately cheap): the file has a `.xaml` extension and its leading bytes
 * contain the Cursorial xmlns marker (`cursorial.dev`).
 */
class CursorialPreviewEditorProvider : FileEditorProvider, DumbAware {

    companion object {
        const val EDITOR_TYPE_ID = "cursorial-designer-editor"

        private const val CURSORIAL_XMLNS_MARKER = "cursorial.dev"
        private const val SNIFF_LIMIT_BYTES = 8192

        private val SNIFF_KEY =
            com.intellij.openapi.util.Key.create<Pair<Long, Boolean>>("cursorial.designer.xmlns.sniff")

        /**
         * Whether the file is a Cursorial XAML document (.xaml/.cxaml carrying the cursorial.dev
         * xmlns). Cached per modification stamp: the file-type overrider calls this on hot paths.
         */
        fun isCursorialXaml(file: VirtualFile): Boolean {
            if (file.isDirectory || !file.isValid) return false
            val extension = file.extension
            if (!"xaml".equals(extension, ignoreCase = true) && !"cxaml".equals(extension, ignoreCase = true)) return false

            val stamp = file.modificationStamp
            file.getUserData(SNIFF_KEY)?.let { (cachedStamp, result) ->
                if (cachedStamp == stamp) return result
            }

            val result = try {
                file.inputStream.use { stream ->
                    val head = stream.readNBytes(SNIFF_LIMIT_BYTES)
                    String(head, StandardCharsets.UTF_8).contains(CURSORIAL_XMLNS_MARKER)
                }
            } catch (_: IOException) {
                false
            }

            file.putUserData(SNIFF_KEY, stamp to result)
            return result
        }
    }

    override fun accept(project: Project, file: VirtualFile): Boolean = isCursorialXaml(file)

    override fun createEditor(project: Project, file: VirtualFile): FileEditor {
        val textEditor = TextEditorProvider.getInstance().createEditor(project, file) as TextEditor
        val previewEditor = CursorialPreviewEditor(project, file, textEditor)
        // TextEditorWithPreview disposes both child editors on its own disposal.
        return RememberingSplitEditor(textEditor, previewEditor)
    }

    /**
     * The split editor, remembering how the user last arranged it: layout mode (text / split /
     * preview) and split orientation are read back at construction and saved on dispose. Divider
     * proportions load once per editor from per-orientation defaults and save as they move —
     * never live-synced between open documents (see [detachSharedProportionKey]).
     */
    private class RememberingSplitEditor(textEditor: TextEditor, previewEditor: CursorialPreviewEditor) :
        TextEditorWithPreview(textEditor, previewEditor, "Cursorial Designer", savedLayout()) {

        private companion object {
            const val LAYOUT_KEY = "cursorial.designer.editor.layout"
            const val VERTICAL_KEY = "cursorial.designer.editor.verticalSplit"
            const val DEFAULT_PROPORTION = 0.5f

            fun savedLayout(): Layout =
                PropertiesComponent.getInstance().getValue(LAYOUT_KEY)
                    ?.let { saved -> Layout.entries.firstOrNull { it.name == saved } }
                    ?: Layout.SHOW_EDITOR_AND_PREVIEW
        }

        init {
            if (PropertiesComponent.getInstance().getBoolean(VERTICAL_KEY, false))
                setState(MyFileEditorState(savedLayout(), null, null, isVerticalSplit = true))
            detachSharedProportionKey()
        }

        /**
         * The platform splitter persists its proportion under ONE key shared by every
         * [TextEditorWithPreview] in the IDE — and RELOADS it on `addNotify` (every tab switch),
         * so resizing the preview in one document live-resized it in every other, and a
         * HORIZONTAL proportion drove VERTICAL splits too (one key, both orientations). Detach
         * the shared key and manage per-orientation defaults ourselves: an editor loads its
         * orientation's default at open (and when the orientation flips), saves as the divider
         * moves, and is otherwise independent of its siblings.
         */
        private fun detachSharedProportionKey() {
            val splitter = findSplitter(component) ?: return
            splitter.setSplitterProportionKey(null)
            applySavedProportion(splitter)
            splitter.addPropertyChangeListener { event ->
                when (event.propertyName) {
                    com.intellij.openapi.ui.Splitter.PROP_PROPORTION -> PropertiesComponent.getInstance()
                        .setValue(proportionKey(splitter.isVertical), splitter.proportion, DEFAULT_PROPORTION)
                    com.intellij.openapi.ui.Splitter.PROP_ORIENTATION -> applySavedProportion(splitter)
                }
            }
        }

        private fun applySavedProportion(splitter: com.intellij.ui.JBSplitter) {
            splitter.proportion = PropertiesComponent.getInstance()
                .getFloat(proportionKey(splitter.isVertical), DEFAULT_PROPORTION)
        }

        private fun proportionKey(vertical: Boolean): String =
            if (vertical) "cursorial.designer.editor.splitProportion.vertical"
            else "cursorial.designer.editor.splitProportion.horizontal"

        private fun findSplitter(root: java.awt.Component): com.intellij.ui.JBSplitter? {
            if (root is com.intellij.ui.JBSplitter) return root
            if (root !is java.awt.Container) return null
            for (child in root.components)
                findSplitter(child)?.let { return it }
            return null
        }

        override fun dispose() {
            // layout/isVerticalSplit are private here; the public FileEditor state carries both.
            (getState(com.intellij.openapi.fileEditor.FileEditorStateLevel.FULL) as? MyFileEditorState)?.let { state ->
                PropertiesComponent.getInstance().setValue(LAYOUT_KEY, state.splitLayout?.name)
                PropertiesComponent.getInstance().setValue(VERTICAL_KEY, state.isVerticalSplit, false)
            }
            super.dispose()
        }
    }

    override fun getEditorTypeId(): String = EDITOR_TYPE_ID

    // HIDE_OTHER_EDITORS (not just HIDE_DEFAULT_EDITOR): Rider's built-in XamlSplitEditor also
    // claims .xaml files and otherwise wins the default tab with a dead "Preview is unsupported
    // for this project" pane. Detection is scoped to the cursorial.dev xmlns, and this editor
    // embeds the full text editor (text/split/preview layouts), so suppressing the rest is safe.
    override fun getPolicy(): FileEditorPolicy = FileEditorPolicy.HIDE_OTHER_EDITORS
}
