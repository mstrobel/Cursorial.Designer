package dev.cursorial.designer.language

import com.intellij.codeInsight.completion.CompletionContributor
import com.intellij.codeInsight.completion.CompletionParameters
import com.intellij.codeInsight.completion.CompletionProvider
import com.intellij.codeInsight.completion.CompletionResultSet
import com.intellij.codeInsight.completion.CompletionType
import com.intellij.codeInsight.completion.InsertHandler
import com.intellij.codeInsight.lookup.LookupElement
import com.intellij.codeInsight.lookup.LookupElementBuilder
import com.intellij.icons.AllIcons
import com.intellij.patterns.PlatformPatterns
import com.intellij.openapi.diagnostic.debug
import com.intellij.openapi.diagnostic.logger
import com.intellij.util.ProcessingContext
import dev.cursorial.designer.editor.CursorialPreviewEditorProvider

/**
 * Code completion for Cursorial XAML: element names, attribute names (members + x: directives),
 * and enum/bool attribute values, answered by the language service (the same parser + metadata
 * providers production uses, with the project's own assemblies registered).
 */
class CursorialXamlCompletionContributor : CompletionContributor() {

    private companion object {
        val logger = logger<CursorialXamlCompletionContributor>()
    }

    init {
        extend(CompletionType.BASIC, PlatformPatterns.psiElement(), object : CompletionProvider<CompletionParameters>() {
            override fun addCompletions(
                parameters: CompletionParameters,
                context: ProcessingContext,
                result: CompletionResultSet,
            ) {
                val file = parameters.originalFile.virtualFile ?: return
                if (!CursorialPreviewEditorProvider.isCursorialXaml(file)) return
                logger.debug { "completing in ${'$'}{file.name} (language=${'$'}{parameters.originalFile.language.id})" }

                val document = parameters.editor.document
                val offset = parameters.offset
                val line = document.getLineNumber(offset)
                val column = offset - document.getLineStartOffset(line) + 1

                val completions = CursorialLanguageService.getInstance(parameters.originalFile.project)
                    .complete(document.text, line + 1, column, file) ?: return

                // Own the prefix: the platform's default guess swallows too much at a caret
                // hard against '=' ({Binding Path=| matched nothing until a space was typed).
                // The prefix is the identifier-ish run before the caret — every delimiter
                // ('=', '{', ',', '.', quotes, whitespace) is a hard break.
                val text = document.charsSequence
                var prefixStart = offset
                while (prefixStart > 0 && (text[prefixStart - 1].isLetterOrDigit() || text[prefixStart - 1] == '_' || text[prefixStart - 1] == '-'))
                    prefixStart--
                val matched = result.withPrefixMatcher(text.subSequence(prefixStart, offset).toString())

                for (item in completions.items)
                    matched.addElement(lookup(item.text, item.kind, item.detail, item.insert, item.caret))
            }
        })
    }

    private fun lookup(text: String, kind: String?, detail: String?, insert: String?, caret: Int? = null): LookupElement {
        // When insert differs from the display text (e.g. {x:Static …} references), the inserted
        // string is the element's payload while the display text drives matching/presentation.
        var builder = if (insert != null)
            LookupElementBuilder.create(insert).withPresentableText(text).withLookupString(text)
        else
            LookupElementBuilder.create(text)
        detail?.let { builder = builder.withTypeText(it, true) }
        if (insert != null && caret != null) {
            builder = builder.withInsertHandler { context, _ ->
                context.editor.caretModel.moveToOffset(context.startOffset + caret)
            }
        }
        builder = when (kind) {
            "element" -> builder.withIcon(AllIcons.Nodes.Class)
            "attribute" -> builder.withIcon(AllIcons.Nodes.Property).withInsertHandler(AttributeInsertHandler)
            "value" -> builder.withIcon(AllIcons.Nodes.Enum)
            else -> builder
        }
        return builder
    }

    /** Completing an attribute inserts `="…"` and parks the caret between the quotes. */
    private object AttributeInsertHandler : InsertHandler<LookupElement> {
        override fun handleInsert(context: com.intellij.codeInsight.completion.InsertionContext, item: LookupElement) {
            val editor = context.editor
            val offset = context.tailOffset
            val alreadyHasValue = offset < editor.document.textLength && editor.document.charsSequence[offset] == '='
            if (!alreadyHasValue) {
                editor.document.insertString(offset, "=\"\"")
                editor.caretModel.moveToOffset(offset + 2)
            }
        }
    }
}
