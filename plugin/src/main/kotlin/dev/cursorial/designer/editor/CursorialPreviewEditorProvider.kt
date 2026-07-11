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

        // TODO: cache the sniff result per file modification stamp; accept() runs often.
        fun isCursorialXaml(file: VirtualFile): Boolean {
            if (file.isDirectory || !file.isValid) return false
            // .cxaml: an extension Rider's own XAML file type does NOT claim, so the platform's
            // frontend pipelines (daemon passes, navigation, docs) all run normally for it.
            val extension = file.extension
            if (!"xaml".equals(extension, ignoreCase = true) && !"cxaml".equals(extension, ignoreCase = true)) return false
            return try {
                file.inputStream.use { stream ->
                    val head = stream.readNBytes(SNIFF_LIMIT_BYTES)
                    String(head, StandardCharsets.UTF_8).contains(CURSORIAL_XMLNS_MARKER)
                }
            } catch (_: IOException) {
                false
            }
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
     * The split editor, remembering how the user last arranged it (globally, not per file):
     * layout mode (text / split / preview) and split orientation are read back at construction
     * and saved on dispose. The inner preview/properties divider persists separately via its
     * splitter proportion key.
     */
    private class RememberingSplitEditor(textEditor: TextEditor, previewEditor: CursorialPreviewEditor) :
        TextEditorWithPreview(textEditor, previewEditor, "Cursorial Designer", savedLayout()) {

        private companion object {
            const val LAYOUT_KEY = "cursorial.designer.editor.layout"
            const val VERTICAL_KEY = "cursorial.designer.editor.verticalSplit"

            fun savedLayout(): Layout =
                PropertiesComponent.getInstance().getValue(LAYOUT_KEY)
                    ?.let { saved -> Layout.entries.firstOrNull { it.name == saved } }
                    ?: Layout.SHOW_EDITOR_AND_PREVIEW
        }

        init {
            if (PropertiesComponent.getInstance().getBoolean(VERTICAL_KEY, false))
                setState(MyFileEditorState(savedLayout(), null, null, isVerticalSplit = true))
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
