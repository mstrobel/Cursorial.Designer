package dev.cursorial.designer.language

import com.intellij.codeInsight.completion.CompletionContributor
import com.intellij.codeInsight.completion.CompletionParameters
import com.intellij.codeInsight.completion.CompletionProvider
import com.intellij.codeInsight.completion.CompletionResultSet
import com.intellij.codeInsight.completion.CompletionSorter
import com.intellij.codeInsight.completion.CompletionType
import com.intellij.codeInsight.completion.InsertHandler
import com.intellij.codeInsight.lookup.LookupElementWeigher
import com.intellij.codeInsight.lookup.LookupElement
import com.intellij.codeInsight.lookup.LookupElementBuilder
import com.intellij.icons.AllIcons
import com.intellij.patterns.PlatformPatterns
import com.intellij.openapi.diagnostic.debug
import com.intellij.openapi.diagnostic.logger
import com.intellij.openapi.util.Key
import com.intellij.util.ProcessingContext
import dev.cursorial.designer.editor.CursorialPreviewEditorProvider
import icons.ReSharperIcons

/**
 * Code completion for Cursorial XAML: element names, attribute names (members + x: directives),
 * and enum/bool attribute values, answered by the language service (the same parser + metadata
 * providers production uses, with the project's own assemblies registered).
 */
class CursorialXamlCompletionContributor : CompletionContributor() {

    private companion object {
        val logger = logger<CursorialXamlCompletionContributor>()

        /** The completion sort band (0 = parent attached, 1 = own members [reserved], 2 = body). */
        val BAND_KEY: Key<Int> = Key.create("cursorial.completionBand")
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

                // Force a deterministic order: the platform's default sorter (Rider's ML relevance
                // weigher) floats predicted-common members to the top and leaves the rest in the
                // lexicographic tiebreaker, so a list we hand over pre-sorted still lands part-ordered.
                // An empty relevance sorter overrides that with THREE BANDS, then A→Z within each band.
                // [ByBand] is the primary weigher (added first ⇒ outermost classifier), [ByBareName]
                // the tiebreaker: the platform's ComparingClassifier sorts each weigher ascending, so
                // band 0 lands at the top and equal-band items fall back to the bare-name order.
                val sorted = result.withRelevanceSorter(CompletionSorter.emptySorter().weigh(ByBand).weigh(ByBareName))
                val matched = sorted.withPrefixMatcher(text.subSequence(prefixStart, offset).toString())

                for (item in completions.items)
                    matched.addElement(lookup(item.text, item.kind, item.detail, item.insert, item.caret, item.parentContext, item.ownMember))

                // We are the authority for Cursorial XAML: the language service answers from the
                // real parser + metadata providers. Stop the pipeline so the platform's default
                // XML contributors don't append schema/document-INFERRED names on top of ours —
                // those pollute the list and, sorted above ours, defeat the alphabetical order.
                // Registered order="first" (plugin.xml) so ours runs before them and this bites.
                // Guarded to Cursorial files by the early returns above; foreign XML is untouched.
                result.stopHere()
            }
        })
    }

    private fun lookup(text: String, kind: String?, detail: String?, insert: String?, caret: Int? = null, parentContext: Boolean = false, ownMember: Boolean = false): LookupElement {
        // When insert differs from the display text (e.g. {x:Static …} references), the inserted
        // string is the element's payload while the display text drives matching/presentation.
        var builder = if (insert != null)
            LookupElementBuilder.create(insert).withPresentableText(text).withLookupString(text)
        else
            LookupElementBuilder.create(text)
        detail?.let { builder = builder.withTypeText(it, true) }
        if (insert != null && caret != null) {
            // A parked caret means the insert stubbed a spot to fill ({StaticResource |},
            // AncestorType=|) — chain straight into the next completion list.
            builder = builder.withInsertHandler { context, _ ->
                context.editor.caretModel.moveToOffset(context.startOffset + caret)
                com.intellij.codeInsight.AutoPopupController.getInstance(context.project).scheduleAutoPopup(context.editor)
            }
        }
        builder = when (kind) {
            "element" -> builder.withIcon(AllIcons.Nodes.Class)
            "attribute" -> builder.withIcon(AllIcons.Nodes.Property).withInsertHandler(AttributeInsertHandler)
            // An event authors as an event-handler attribute (Click="…"), so it keeps the attribute
            // insert behavior — only the icon differs, to set it apart from a real property. The
            // platform AllIcons.Nodes has no event glyph (it is a Rider/.NET-only concept), so use
            // ReSharper's own event symbol icon — the same one Rider shows for a C# event.
            "event" -> builder.withIcon(ReSharperIcons.PsiSymbols.Event).withInsertHandler(AttributeInsertHandler)
            // An attached property (Grid.Column) authors as an ="…" attribute too, so it keeps the
            // attribute insert behavior; a distinct read/write-property glyph sets it apart from a
            // plain own property.
            "attached" -> builder.withIcon(AllIcons.Nodes.PropertyReadWrite).withInsertHandler(AttributeInsertHandler)
            "value" -> builder.withIcon(AllIcons.Nodes.Enum)
            else -> builder
        }
        // The sort band travels as user data so [ByBand] (which only sees the LookupElement) can read
        // it. Band 0 = the parent's attached properties (parentContext) → lifted to the top; band 1 =
        // the element's OWN declared/AddOwner'd members (ownMember, from the framework provenance query)
        // → the middle; band 2 = everything else (inherited base members, non-parent attachables), the
        // alphabetical body. parentContext wins over ownMember when a candidate is somehow both.
        val element: LookupElement = builder
        element.putUserData(BAND_KEY, if (parentContext) 0 else if (ownMember) 1 else 2)
        return element
    }

    /**
     * The PRIMARY (three-band) weigher. Bands, top-first: 0 = the element's PARENT's attached
     * properties (Grid.Column inside a Grid — the host's `parentContext` flag); 1 = RESERVED for the
     * element's OWN declared/AddOwner'd members (a framework per-member provenance query is the
     * pending task #28 follow-up — until it lands the host tags nothing here); 2 = everything else
     * (inherited base members, non-parent attachables). The band rides on the lookup element as user
     * data (set in [lookup]); the platform's ComparingClassifier sorts ascending, so band 0 tops the
     * list. Within a band, [ByBareName] restores the A→Z order.
     */
    private object ByBand : LookupElementWeigher("cursorial.band") {
        override fun weigh(element: LookupElement): Comparable<*> = element.getUserData(BAND_KEY) ?: 2
    }

    /**
     * The tiebreaker within a band: sorts by the bare member name — the `prefix:` and `Owner.`
     * qualification stripped — so each band reads A→Z (mirrors the host's NameOf). Kind-agnostic by
     * design: it works from the lookup string, and value completions (enum members) carry neither ':'
     * nor '.', so they sort by their own text unchanged.
     */
    private object ByBareName : LookupElementWeigher("cursorial.bareName") {
        override fun weigh(element: LookupElement): Comparable<*> = bareName(element.lookupString)

        private fun bareName(text: String): String {
            var s = text
            val colon = s.indexOf(':')
            if (colon in 0..s.length - 2) s = s.substring(colon + 1)
            val dot = s.indexOf('.')
            if (dot in 0..s.length - 2) s = s.substring(dot + 1)
            return s
        }
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
