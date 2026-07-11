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

    private val logger = com.intellij.openapi.diagnostic.logger<CursorialXamlExternalAnnotator>()

    /** The document snapshot captured on the EDT before the background run. */
    data class Source(val file: PsiFile, val text: String)

    /** Diagnostics paired with the snapshot they were computed against. */
    data class Result(val source: Source, val diagnostics: DiagnosticsEvent)

    override fun collectInformation(file: PsiFile): Source? {
        val virtualFile = file.virtualFile ?: return null
        val ours = CursorialPreviewEditorProvider.isCursorialXaml(virtualFile)
        logger.info("annotator collect: ${file.name} language=${file.language.id} ours=$ours")
        if (!ours) return null
        return Source(file, file.text)
    }

    override fun doAnnotate(collected: Source?): Result? {
        val source = collected ?: return null
        val diagnostics = CursorialLanguageService.getInstance(source.file.project)
            .analyze(source.text, source.file.virtualFile?.url, source.file.virtualFile, classify = true)
        logger.info("annotator doAnnotate: ${source.file.name} -> items=${diagnostics?.items?.size ?: -1} tokens=${diagnostics?.tokens?.size ?: -1}")
        if (diagnostics == null) return null
        return Result(source, diagnostics)
    }

    override fun apply(file: PsiFile, annotationResult: Result?, holder: AnnotationHolder) {
        logger.info("annotator apply: ${file.name} stale=${annotationResult != null && annotationResult.source.text != file.text}")
        val result = annotationResult?.diagnostics ?: return

        // An edit may have slipped in during doAnnotate; positions computed against the old
        // snapshot would land on shifted text. Bail — the daemon reruns on the fresh document.
        val text = annotationResult.source.text
        if (text != file.text) return

        for (item in result.items) {
            val start = offsetOf(text, item.line, item.column) ?: continue
            val range = highlightRange(text, start) ?: continue
            val severity = when (item.severity) {
                "error" -> HighlightSeverity.ERROR
                "warning" -> HighlightSeverity.WARNING
                else -> HighlightSeverity.WEAK_WARNING
            }

            holder.newAnnotation(severity, "${item.code ?: "CUR"}: ${item.message}")
                .range(range)
                .create()
        }

        // Semantic highlighting: the host's classified token ranges, rendered as silent
        // informational annotations. The frontend has no XML PSI for these files, so this is
        // the only layer that knows an element from an attached property from an extension.
        // Base kinds (comments, plain attributes, strings) apply only where no native lexer
        // colors them — plain text; Rider's Xaml language already paints those natively.
        val hasNativeLexer = file.language.id != "TEXT"
        for (token in result.tokens.orEmpty()) {
            if (hasNativeLexer && token.k in baseKinds) continue
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
            "comment" to TextAttributesKey.createTextAttributesKey(
                "CURSORIAL_XAML_COMMENT", DefaultLanguageHighlighterColors.BLOCK_COMMENT),
            // XAML convention: attributes color as PROPERTIES. Rider's scheme has a dedicated
            // R# property key; fall back to the platform's instance-field color when it's not
            // present (find() creates an empty key rather than failing).
            "attribute" to TextAttributesKey.createTextAttributesKey(
                "CURSORIAL_XAML_ATTRIBUTE",
                TextAttributesKey.find("ReSharper.PROPERTY_IDENTIFIER")
                    .takeIf { it.defaultAttributes != null }
                    ?: DefaultLanguageHighlighterColors.INSTANCE_FIELD),
            "string" to TextAttributesKey.createTextAttributesKey(
                "CURSORIAL_XAML_STRING", DefaultLanguageHighlighterColors.STRING),
        )

        /** Kinds a native lexer already paints; applied only on plain-text files. */
        val baseKinds = setOf("comment", "attribute", "string")
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

    /**
     * The identifier-ish token at [start]. Parser diagnostics anchor element errors on the tag
     * START — the `<` (or `</`) — so leading brackets are skipped to squiggle the NAME rather
     * than one bracket character. Falls back to a single character at the anchor; null only
     * when the anchor is outside the text.
     */
    private fun highlightRange(text: String, start: Int): TextRange? {
        if (start >= text.length) return if (text.isEmpty()) null else TextRange(text.length - 1, text.length)

        var begin = start
        if (text[begin] == '<') begin++
        if (begin < text.length && text[begin] == '/') begin++

        var end = begin
        while (end < text.length && (text[end].isLetterOrDigit() || text[end] == '.' || text[end] == ':' || text[end] == '_'))
            end++

        return if (end > begin) TextRange(begin, end) else TextRange(start, start + 1)
    }
}
