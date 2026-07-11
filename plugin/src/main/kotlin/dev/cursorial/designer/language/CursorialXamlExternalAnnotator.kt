package dev.cursorial.designer.language

import com.intellij.lang.annotation.AnnotationHolder
import com.intellij.lang.annotation.ExternalAnnotator
import com.intellij.lang.annotation.HighlightSeverity
import com.intellij.openapi.util.TextRange
import com.intellij.psi.PsiFile
import dev.cursorial.designer.editor.CursorialPreviewEditorProvider
import dev.cursorial.designer.protocol.DiagnosticsEvent

/**
 * Live CURxxxx diagnostics: ships the document snapshot to the language service on a background
 * pass (the ExternalAnnotator contract — runs after typing settles, off the EDT) and annotates
 * the editor with the parser's positioned diagnostics, did-you-mean suggestions included.
 */
class CursorialXamlExternalAnnotator : ExternalAnnotator<CursorialXamlExternalAnnotator.Source, DiagnosticsEvent?>() {

    /** The document snapshot captured on the EDT before the background run. */
    data class Source(val file: PsiFile, val text: String)

    override fun collectInformation(file: PsiFile): Source? {
        val virtualFile = file.virtualFile ?: return null
        if (!CursorialPreviewEditorProvider.isCursorialXaml(virtualFile)) return null
        return Source(file, file.text)
    }

    override fun doAnnotate(collected: Source?): DiagnosticsEvent? {
        val source = collected ?: return null
        return CursorialLanguageService.getInstance(source.file.project)
            .analyze(source.text, source.file.virtualFile?.url, source.file.virtualFile)
    }

    override fun apply(file: PsiFile, annotationResult: DiagnosticsEvent?, holder: AnnotationHolder) {
        val result = annotationResult ?: return
        val text = file.text

        for (item in result.items) {
            val start = offsetOf(text, item.line, item.column) ?: continue
            val end = tokenEnd(text, start)
            val severity = when (item.severity) {
                "error" -> HighlightSeverity.ERROR
                "warning" -> HighlightSeverity.WARNING
                else -> HighlightSeverity.WEAK_WARNING
            }

            holder.newAnnotation(severity, "${item.code ?: "CUR"}: ${item.message}")
                .range(TextRange(start, end))
                .create()
        }
    }

    /** 1-based (line, column) to offset; null when the position falls outside the snapshot. */
    private fun offsetOf(text: String, line: Int, column: Int): Int? {
        var offset = 0
        var current = 1
        while (current < line) {
            val newline = text.indexOf('\n', offset)
            if (newline < 0) return null
            offset = newline + 1
            current++
        }
        val target = offset + column - 1
        return target.takeIf { it in 0..text.length }
    }

    /** Extends the highlight over the identifier-ish token at [start]; minimum one character. */
    private fun tokenEnd(text: String, start: Int): Int {
        var end = start
        while (end < text.length && (text[end].isLetterOrDigit() || text[end] == '.' || text[end] == ':' || text[end] == '_'))
            end++
        return maxOf(end, minOf(start + 1, text.length))
    }
}
