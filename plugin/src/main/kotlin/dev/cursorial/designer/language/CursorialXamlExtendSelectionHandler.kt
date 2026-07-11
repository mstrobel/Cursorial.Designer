package dev.cursorial.designer.language

import com.intellij.codeInsight.editorActions.ExtendWordSelectionHandlerBase
import com.intellij.openapi.editor.Editor
import com.intellij.openapi.util.TextRange
import com.intellij.psi.PsiElement
import dev.cursorial.designer.editor.CursorialPreviewEditorProvider

/**
 * Extend Selection (Ctrl+W) steps for markup-extension structure inside attribute values.
 * Without XML PSI on the frontend, the platform jumps straight from a word to the whole
 * value; this handler adds the intermediate ranges so
 * `Foreground="{DynamicResource {x:Static ThemeKeys.MutedBrush|}}"` expands through
 * `MutedBrush` → `ThemeKeys.MutedBrush` → `{x:Static ThemeKeys.MutedBrush}` →
 * `{DynamicResource {x:Static ThemeKeys.MutedBrush}}` → the value. Everything is textual
 * and local — selection expansion must be instant, so no language-service round trip.
 */
class CursorialXamlExtendSelectionHandler : ExtendWordSelectionHandlerBase() {

    override fun canSelect(e: PsiElement): Boolean {
        val virtualFile = e.containingFile?.virtualFile ?: return false
        return CursorialPreviewEditorProvider.isCursorialXaml(virtualFile)
    }

    override fun select(e: PsiElement, editorText: CharSequence, cursorOffset: Int, editor: Editor): List<TextRange>? {
        val value = attributeValueAround(editorText, cursorOffset) ?: return null
        val ranges = mutableListOf<TextRange>()

        // Innermost first: the identifier segment, then the dotted/prefixed path around the caret.
        wordRange(editorText, cursorOffset, value) { it.isLetterOrDigit() || it == '_' }?.let(ranges::add)
        wordRange(editorText, cursorOffset, value) { it.isLetterOrDigit() || it == '_' || it == '.' || it == ':' }?.let(ranges::add)

        // Every balanced {…} group in the value containing the caret, innermost to outermost.
        val open = ArrayDeque<Int>()
        for (i in value.startOffset until value.endOffset) {
            when (editorText[i]) {
                '{' -> open.addLast(i)
                '}' -> {
                    val start = open.removeLastOrNull() ?: continue
                    if (cursorOffset in (start + 1)..i) ranges.add(TextRange(start, i + 1))
                }
            }
        }

        ranges.add(value)
        return ranges.distinct()
    }

    /** The inside of the quoted attribute value containing [offset], or null when not in one. */
    private fun attributeValueAround(text: CharSequence, offset: Int): TextRange? {
        // A raw '<' cannot appear inside an attribute value, so the nearest one opens our tag;
        // from there a forward walk with quote state finds the value span (a raw '>' inside
        // quotes must not read as the tag's close — same rule the host's scanners follow).
        var openTag = offset - 1
        while (openTag >= 0 && text[openTag] != '<') openTag--
        if (openTag < 0) return null

        var valueStart = -1
        var i = openTag + 1
        while (i < text.length) {
            when (text[i]) {
                '"' ->
                    if (valueStart < 0) {
                        valueStart = i + 1
                    } else {
                        if (offset in valueStart..i) return TextRange(valueStart, i)
                        valueStart = -1
                    }

                '>' -> if (valueStart < 0) return null // the tag ended before a value containing the caret
            }
            i++
        }

        // Unterminated value — routine mid-edit; treat end-of-text as its end.
        return if (valueStart in 0..offset) TextRange(valueStart, i) else null
    }

    private inline fun wordRange(text: CharSequence, offset: Int, bounds: TextRange, predicate: (Char) -> Boolean): TextRange? {
        var start = offset
        while (start > bounds.startOffset && predicate(text[start - 1])) start--
        var end = offset
        while (end < bounds.endOffset && predicate(text[end])) end++
        return if (end > start) TextRange(start, end) else null
    }
}
