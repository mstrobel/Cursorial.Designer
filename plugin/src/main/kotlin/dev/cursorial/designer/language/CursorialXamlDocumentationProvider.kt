package dev.cursorial.designer.language

import com.intellij.lang.documentation.AbstractDocumentationProvider
import com.intellij.lang.documentation.DocumentationMarkup
import com.intellij.openapi.editor.Editor
import com.intellij.openapi.util.text.StringUtil
import com.intellij.psi.PsiElement
import com.intellij.psi.PsiFile
import com.intellij.psi.impl.FakePsiElement
import dev.cursorial.designer.editor.CursorialPreviewEditorProvider

/**
 * Quick documentation (Ctrl+Q / hover) for Cursorial XAML: the language service resolves the
 * symbol at the caret and returns a signature, the XML-doc summary from the assembly's doc
 * file, and — for x:Static paths — the resolved value. The custom documentation element wraps
 * the raw offset because these files expose no structured PSI on the frontend; the actual host
 * round trip happens in [generateDoc], which the platform calls off the EDT.
 */
class CursorialXamlDocumentationProvider : AbstractDocumentationProvider() {

    /** A positional stand-in for the symbol under the caret (no structured PSI exists here). */
    class DocTarget(private val psiFile: PsiFile, val offset: Int) : FakePsiElement() {
        override fun getParent(): PsiElement = psiFile
        override fun getContainingFile(): PsiFile = psiFile
    }

    override fun getCustomDocumentationElement(
        editor: Editor,
        file: PsiFile,
        contextElement: PsiElement?,
        targetOffset: Int,
    ): PsiElement? {
        val virtualFile = file.virtualFile ?: return null
        if (!CursorialPreviewEditorProvider.isCursorialXaml(virtualFile)) return null
        return DocTarget(file, targetOffset)
    }

    override fun generateDoc(element: PsiElement?, originalElement: PsiElement?): String? {
        val target = element as? DocTarget ?: return null
        val file = target.containingFile
        val virtualFile = file.virtualFile ?: return null

        val text = file.text
        val (line, column) = positionOf(text, target.offset)
        val hover = CursorialLanguageService.getInstance(file.project)
            .hover(text, line, column, virtualFile) ?: return null
        if (hover.signature == null && hover.summary == null) return null

        return buildString {
            hover.signature?.let {
                append(DocumentationMarkup.DEFINITION_START)
                append(StringUtil.escapeXmlEntities(it))
                append(DocumentationMarkup.DEFINITION_END)
            }
            if (hover.summary != null || hover.detail != null) {
                append(DocumentationMarkup.CONTENT_START)
                hover.summary?.let { append("<p>").append(StringUtil.escapeXmlEntities(it)).append("</p>") }
                hover.detail?.let { append("<p><b>Value:</b> ").append(StringUtil.escapeXmlEntities(it)).append("</p>") }
                append(DocumentationMarkup.CONTENT_END)
            }
        }
    }

    /** Offset → 1-based (line, column). */
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
