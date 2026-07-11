package dev.cursorial.designer.language

import com.intellij.lang.annotation.AnnotationHolder
import com.intellij.lang.annotation.ExternalAnnotator
import com.intellij.lang.annotation.HighlightSeverity
import com.intellij.openapi.editor.DefaultLanguageHighlighterColors
import com.intellij.openapi.editor.colors.TextAttributesKey
import com.intellij.openapi.util.TextRange
import com.intellij.psi.PsiFile
import dev.cursorial.designer.editor.CursorialPreviewEditorProvider
import dev.cursorial.designer.protocol.DiagnosticsEvent

/**
 * Live CURxxxx diagnostics: ships the document snapshot to the language service on a background
 * pass (the ExternalAnnotator contract — runs after typing settles, off the EDT) and annotates
 * the editor with the parser's positioned diagnostics, did-you-mean suggestions included.
 */
class CursorialXamlExternalAnnotator : ExternalAnnotator<CursorialXamlExternalAnnotator.Source, CursorialXamlExternalAnnotator.Result?>() {

    /** The document snapshot captured on the EDT before the background run. */
    data class Source(val file: PsiFile, val text: String)

    /** Diagnostics paired with the snapshot they were computed against. */
    data class Result(val source: Source, val diagnostics: DiagnosticsEvent)

    override fun collectInformation(file: PsiFile): Source? {
        val virtualFile = file.virtualFile ?: return null
        if (!CursorialPreviewEditorProvider.isCursorialXaml(virtualFile)) return null
        return Source(file, file.text)
    }

    override fun doAnnotate(collected: Source?): Result? {
        val source = collected ?: return null
        val diagnostics = CursorialLanguageService.getInstance(source.file.project)
            .analyze(source.text, source.file.virtualFile?.url, source.file.virtualFile, classify = true) ?: return null
        return Result(source, diagnostics)
    }

    override fun apply(file: PsiFile, annotationResult: Result?, holder: AnnotationHolder) {
        val result = annotationResult?.diagnostics ?: return

        // An edit may have slipped in during doAnnotate; positions computed against the old
        // snapshot would land on shifted text. Bail — the daemon reruns on the fresh document.
        val text = annotationResult.source.text
        if (text != file.text) return

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

        // Semantic highlighting: the host's classified token ranges, rendered as silent
        // informational annotations. The frontend has no XML PSI for these files, so this is
        // the only layer that knows an element from an attached property from an extension.
        for (token in result.tokens.orEmpty()) {
            val key = tokenAttributes[token.k] ?: continue
            val start = offsetOf(text, token.l, token.c) ?: continue
            val end = (start + token.n).coerceAtMost(text.length)
            if (end <= start) continue

            holder.newSilentAnnotation(HighlightSeverity.INFORMATION)
                .range(TextRange(start, end))
                .textAttributes(key)
                .create()
        }
    }

    private companion object {
        /** Host token kinds → theme-aware attribute keys (fallbacks pick up the active scheme). */
        val tokenAttributes: Map<String, TextAttributesKey> = mapOf(
            "element" to TextAttributesKey.createTextAttributesKey(
                "CURSORIAL_XAML_ELEMENT", DefaultLanguageHighlighterColors.CLASS_REFERENCE),
            "attached" to TextAttributesKey.createTextAttributesKey(
                "CURSORIAL_XAML_ATTACHED", DefaultLanguageHighlighterColors.STATIC_FIELD),
            "directive" to TextAttributesKey.createTextAttributesKey(
                "CURSORIAL_XAML_DIRECTIVE", DefaultLanguageHighlighterColors.METADATA),
            "extension" to TextAttributesKey.createTextAttributesKey(
                "CURSORIAL_XAML_EXTENSION", DefaultLanguageHighlighterColors.KEYWORD),
        )
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
