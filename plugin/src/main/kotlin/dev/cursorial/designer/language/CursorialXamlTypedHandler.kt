package dev.cursorial.designer.language

import com.intellij.codeInsight.editorActions.TypedHandlerDelegate
import com.intellij.openapi.editor.Editor
import com.intellij.openapi.editor.EditorModificationUtil
import com.intellij.openapi.project.Project
import com.intellij.psi.PsiFile
import dev.cursorial.designer.editor.CursorialPreviewEditorProvider

/**
 * Tag auto-closing for Cursorial XAML: typing `>` after an opening tag inserts the matching
 * `</Name>` (caret stays put), and typing `</` completes the innermost unclosed tag.
 *
 * The platform's XML typed handlers never fire here because Rider's XAML file type does not
 * expose XML-language PSI on the frontend (its XML smarts live in the R# backend, which does
 * not engage with Cursorial XAML) — the same reason the annotator and completion contributor
 * register with language="any". Everything below works on raw document text.
 */
class CursorialXamlTypedHandler : TypedHandlerDelegate() {

    override fun checkAutoPopup(charTyped: Char, project: Project, editor: Editor, file: PsiFile): Result {
        // ':', '#', '.', and '/' start pseudo-class / named-element / style-class /
        // template-combinator tokens in selectors ('.' also starts attached properties in
        // attribute names); '=' starts extension parameter values ({Binding Path=…}).
        if (charTyped != ':' && charTyped != '#' && charTyped != '.' && charTyped != '/' && charTyped != '=') return Result.CONTINUE
        val virtualFile = file.virtualFile ?: return Result.CONTINUE
        if (!CursorialPreviewEditorProvider.isCursorialXaml(virtualFile)) return Result.CONTINUE
        com.intellij.codeInsight.AutoPopupController.getInstance(project).scheduleAutoPopup(editor)
        return Result.STOP
    }

    override fun charTyped(c: Char, project: Project, editor: Editor, file: PsiFile): Result {
        if (c != '>' && c != '/') return Result.CONTINUE
        val virtualFile = file.virtualFile ?: return Result.CONTINUE
        if (!CursorialPreviewEditorProvider.isCursorialXaml(virtualFile)) return Result.CONTINUE
        // XML-derived PSI (the owned file type) already gets the platform's XML auto-closing;
        // inserting a second closing tag would double it. This handler serves only files with
        // no XML PSI (plain text).
        if (file.language is com.intellij.lang.xml.XMLLanguage) return Result.CONTINUE

        return when (c) {
            '>' -> autoCloseTag(editor)
            else -> completeEndTag(editor)
        }
    }

    /** After `<Foo Bar="…">` is completed with `>`, insert `</Foo>` without moving the caret. */
    private fun autoCloseTag(editor: Editor): Result {
        val text = editor.document.charsSequence
        val offset = editor.caretModel.offset
        if (offset < 2 || text[offset - 1] != '>') return Result.CONTINUE

        val start = text.lastIndexOf('<', offset - 2)
        if (start < 0 || inCommentOrCData(text, start)) return Result.CONTINUE

        // A full-segment match rejects closing tags, comments/PIs, stray quotes, and a `>` typed
        // inside an attribute value; a trailing `/` means the tag self-closed.
        val tag = OPEN_TAG.matchEntire(text.subSequence(start, offset)) ?: return Result.CONTINUE
        if (tag.groupValues[2] == "/") return Result.CONTINUE

        val name = tag.groupValues[1]
        if (text.startsWith("</$name", offset)) return Result.CONTINUE

        EditorModificationUtil.insertStringAtCaret(editor, "</$name>", false, false)
        return Result.STOP
    }

    /** Typing `</` completes to `</Name>` for the innermost unclosed tag, caret after. */
    private fun completeEndTag(editor: Editor): Result {
        val text = editor.document.charsSequence
        val offset = editor.caretModel.offset
        if (offset < 2 || text[offset - 1] != '/' || text[offset - 2] != '<') return Result.CONTINUE
        if (inCommentOrCData(text, offset - 2)) return Result.CONTINUE

        val name = innermostUnclosedTag(text.subSequence(0, offset - 2)) ?: return Result.CONTINUE
        val alreadyClosed = text.startsWith("$name>", offset)
        EditorModificationUtil.insertStringAtCaret(editor, if (alreadyClosed) "" else "$name>", false, true)
        if (alreadyClosed) editor.caretModel.moveToOffset(offset + name.length + 1)
        return Result.STOP
    }

    private fun innermostUnclosedTag(prefix: CharSequence): String? {
        val scannable = COMMENT_OR_CDATA.replace(prefix.toString()) { " ".repeat(it.value.length) }
        val stack = ArrayDeque<String>()
        for (tag in TAG.findAll(scannable)) {
            val (closing, name, _, selfClosing) = tag.destructured
            when {
                selfClosing == "/" -> {}
                closing == "/" -> while (stack.isNotEmpty() && stack.removeLast() != name) {}
                else -> stack.addLast(name)
            }
        }
        return stack.lastOrNull()
    }

    /** Whether [position] falls inside an unterminated `<!-- -->` or `<![CDATA[ ]]>` section. */
    private fun inCommentOrCData(text: CharSequence, position: Int): Boolean {
        val prefix = text.subSequence(0, position).toString()
        if (prefix.lastIndexOf("<!--") > prefix.lastIndexOf("-->")) return true
        return prefix.lastIndexOf("<![CDATA[") > prefix.lastIndexOf("]]>")
    }

    private companion object {
        val OPEN_TAG = Regex("""<([A-Za-z_][\w.:-]*)(?:[^<>"']|"[^"]*"|'[^']*')*?(/?)>""")
        val TAG = Regex("""<(/?)([A-Za-z_][\w.:-]*)((?:[^<>"']|"[^"]*"|'[^']*')*?)(/?)>""")
        val COMMENT_OR_CDATA = Regex("""<!--.*?-->|<!\[CDATA\[.*?]]>""", RegexOption.DOT_MATCHES_ALL)
    }
}
