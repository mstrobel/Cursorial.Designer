package dev.cursorial.designer.language

import com.intellij.codeInsight.navigation.actions.GotoDeclarationHandler
import com.intellij.openapi.editor.Editor
import com.intellij.openapi.fileEditor.OpenFileDescriptor
import com.intellij.openapi.util.io.FileUtil
import com.intellij.openapi.vfs.LocalFileSystem
import com.intellij.pom.Navigatable
import com.intellij.psi.PsiElement
import com.intellij.psi.PsiFile
import com.intellij.psi.impl.FakePsiElement
import dev.cursorial.designer.editor.CursorialPreviewEditorProvider

/**
 * Go to declaration (Ctrl+B / Ctrl+Click) for Cursorial XAML. The host resolves the symbol at
 * the caret and reads its source location from the assembly's portable PDB sequence points —
 * no ReSharper backend involved, which is why this works for a frontend-only plugin. Locations
 * are only offered when the PDB-recorded path exists locally (true for anything built from
 * source on this machine; NuGet-restored binaries simply don't navigate).
 */
class CursorialXamlGotoDeclarationHandler : GotoDeclarationHandler {

    private val logger = com.intellij.openapi.diagnostic.logger<CursorialXamlGotoDeclarationHandler>()

    /** Ctrl-hover re-queries the same offset every few ms; one host round trip per (doc-stamp, offset) is plenty. */
    private data class CacheKey(val document: com.intellij.openapi.editor.Document, val stamp: Long, val offset: Int)
    @Volatile
    private var lastQuery: Pair<CacheKey, dev.cursorial.designer.protocol.DefinitionEvent?>? = null

    override fun getGotoDeclarationTargets(sourceElement: PsiElement?, offset: Int, editor: Editor): Array<PsiElement>? {
        val file = sourceElement?.containingFile ?: return null
        val virtualFile = file.virtualFile ?: return null
        if (!CursorialPreviewEditorProvider.isCursorialXaml(virtualFile)) return null

        val key = CacheKey(editor.document, editor.document.modificationStamp, offset)
        val cached = lastQuery
        val result = if (cached?.first == key) {
            cached.second
        } else {
            val text = editor.document.text
            val (line, column) = positionOf(text, offset)
            val fresh = CursorialLanguageService.getInstance(file.project)
                .definition(text, line, column, virtualFile)
            logger.info("goto @$line:$column -> ${fresh?.symbol} at ${fresh?.file}:${fresh?.line}")
            lastQuery = key to fresh
            fresh
        }

        if (result == null) return null

        val path = result.file ?: return null
        // PDB document names use the build machine's separators ('\' for Windows-built
        // assemblies); the VFS wants system-independent '/'.
        val targetFile = LocalFileSystem.getInstance().findFileByPath(FileUtil.toSystemIndependentName(path)) ?: return null
        val descriptor = OpenFileDescriptor(
            file.project,
            targetFile,
            ((result.line ?: 1) - 1).coerceAtLeast(0),
            ((result.column ?: 1) - 1).coerceAtLeast(0),
        )

        return arrayOf(NavigationTarget(file, result.symbol ?: targetFile.name, descriptor))
    }

    /** A navigatable stand-in: the real target is a (file, line, column), not frontend PSI. */
    private class NavigationTarget(
        private val sourceFile: PsiFile,
        private val symbolName: String,
        private val descriptor: OpenFileDescriptor,
    ) : FakePsiElement(), Navigatable {
        override fun getParent(): PsiElement = sourceFile
        override fun getName(): String = symbolName
        override fun navigate(requestFocus: Boolean) = descriptor.navigate(requestFocus)
        override fun canNavigate(): Boolean = descriptor.canNavigate()
        override fun canNavigateToSource(): Boolean = canNavigate()

        // The ctrl-hover tooltip and target popups present this element; without an explicit
        // presentation they show the SOURCE file (our parent) as the location, which reads
        // like the link goes nowhere. Show the actual destination.
        override fun getPresentation(): com.intellij.navigation.ItemPresentation = object : com.intellij.navigation.ItemPresentation {
            override fun getPresentableText(): String = symbolName
            override fun getLocationString(): String = "${descriptor.file.name}:${descriptor.line + 1}"
            override fun getIcon(unused: Boolean): javax.swing.Icon? = null
        }
    }

    private fun positionOf(text: String, offset: Int): Pair<Int, Int> {
        var line = 1
        var lineStart = 0
        for (i in 0 until offset.coerceIn(0, text.length)) {
            if (text[i] == '\n') {
                line++
                lineStart = i + 1
            }
        }
        return line to (offset - lineStart + 1)
    }
}
