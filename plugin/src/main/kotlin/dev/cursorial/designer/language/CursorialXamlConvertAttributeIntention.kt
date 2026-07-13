package dev.cursorial.designer.language

import com.intellij.codeInsight.intention.IntentionAction
import com.intellij.codeInsight.daemon.impl.IntentionActionFilter
import com.intellij.codeInsight.intention.PsiElementBaseIntentionAction
import com.intellij.openapi.editor.Editor
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.text.StringUtil
import com.intellij.psi.PsiElement
import com.intellij.psi.PsiFile
import com.intellij.psi.XmlElementFactory
import com.intellij.psi.util.PsiTreeUtil
import com.intellij.psi.xml.XmlAttribute
import com.intellij.psi.xml.XmlTag

/**
 * Converts an attribute to XAML property-element form:
 *
 *  - `Width="12"` on `<Border>` → `<Border.Width>12</Border.Width>`
 *  - `Grid.Row="0"` → `<Grid.Row>0</Grid.Row>` (attached: the dotted name IS the element form)
 *  - `bars:Ribbon.ButtonSize="Small"` → `<bars:Ribbon.ButtonSize>…` (prefix carried verbatim)
 *
 * NOT offered for xmlns declarations, prefixed names WITHOUT a dotted owner (`x:Name`,
 * `d:DesignWidth` — directives are attribute-only in XAML; there is no element form to convert
 * to), or markup-extension values (`{Binding …}` would need the extension's element syntax).
 */
class CursorialXamlConvertAttributeIntention : PsiElementBaseIntentionAction() {

    override fun getFamilyName(): String = "Convert attribute to property element"

    override fun getText(): String = familyName

    override fun isAvailable(project: Project, editor: Editor?, element: PsiElement): Boolean {
        if (element.containingFile?.language != CursorialXamlLanguage) return false
        val attribute = PsiTreeUtil.getParentOfType(element, XmlAttribute::class.java, false) ?: return false
        val value = attribute.value ?: return false
        return elementFormName(attribute) != null && !value.startsWith("{")
    }

    override fun invoke(project: Project, editor: Editor?, element: PsiElement) {
        val attribute = PsiTreeUtil.getParentOfType(element, XmlAttribute::class.java, false) ?: return
        val tag = attribute.parent as? XmlTag ?: return
        val name = elementFormName(attribute) ?: return
        val value = attribute.value ?: return

        val child = XmlElementFactory.getInstance(project)
            .createTagFromText("<$name>${StringUtil.escapeXmlEntities(value)}</$name>", CursorialXamlLanguage)
        tag.addSubTag(child, true)
        attribute.delete()
    }

    /** The property-element tag name, or null when the attribute has no element form. */
    private fun elementFormName(attribute: XmlAttribute): String? {
        val name = attribute.name
        if (name.startsWith("xmlns")) return null
        val owner = (attribute.parent as? XmlTag)?.name?.takeIf { it.isNotEmpty() } ?: return null
        return when {
            name.contains('.') -> name          // attached / pre-qualified — already the element form
            name.contains(':') -> null          // directive (x:Name, d:DesignWidth) — attribute-only
            else -> "$owner.$name"              // plain property — qualify with the OWNING tag's name
        }
    }
}

/**
 * Hides the foreign "convert … to nested element" intention on Cursorial documents: it mangles
 * prefixed attributes under our dialect (`d:DesignWidth="12"` → `<d:d:DesignWidth>`), and
 * [CursorialXamlConvertAttributeIntention] provides the XAML-correct conversion in its place.
 * Text-matched — the contributor is not identifiable by class from here; on files where it never
 * appears this filter is inert.
 */
class CursorialXamlIntentionFilter : IntentionActionFilter {
    override fun accept(intentionAction: IntentionAction, file: PsiFile?): Boolean {
        if (file?.language != CursorialXamlLanguage) return true
        return !intentionAction.text.contains("nested element", ignoreCase = true)
    }
}
