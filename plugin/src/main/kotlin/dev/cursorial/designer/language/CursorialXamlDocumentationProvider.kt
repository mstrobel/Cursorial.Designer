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

    private val logger = com.intellij.openapi.diagnostic.logger<CursorialXamlDocumentationProvider>()

    /** A positional stand-in for the symbol under the caret (no structured PSI exists here). */
    class DocTarget(private val psiFile: PsiFile, val offset: Int) : FakePsiElement() {
        override fun getParent(): PsiElement = psiFile
        override fun getContainingFile(): PsiFile = psiFile

        // The documentation pipeline computes a target presentation BEFORE fetching content and
        // throws "cannot be presented" (killing the popup) when the element has no name — the
        // actual symbol name isn't known until the host answers, so present the document.
        override fun getName(): String = psiFile.name
        override fun getPresentation(): com.intellij.navigation.ItemPresentation = object : com.intellij.navigation.ItemPresentation {
            override fun getPresentableText(): String = psiFile.name
            override fun getLocationString(): String? = null
            override fun getIcon(unused: Boolean): javax.swing.Icon? = null
        }
    }

    override fun getCustomDocumentationElement(
        editor: Editor,
        file: PsiFile,
        contextElement: PsiElement?,
        targetOffset: Int,
    ): PsiElement? {
        val virtualFile = file.virtualFile ?: return null
        logger.info("docs getCustomDocumentationElement: ${file.name} lang=${file.language.id}")
        if (!CursorialPreviewEditorProvider.isCursorialXaml(virtualFile)) return null
        return DocTarget(file, targetOffset)
    }

    override fun generateDoc(element: PsiElement?, originalElement: PsiElement?): String? {
        logger.info("docs generateDoc: element=${element?.javaClass?.simpleName}")
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
