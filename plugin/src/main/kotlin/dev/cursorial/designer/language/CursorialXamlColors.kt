package dev.cursorial.designer.language

import com.intellij.icons.AllIcons
import com.intellij.openapi.editor.DefaultLanguageHighlighterColors
import com.intellij.openapi.editor.colors.TextAttributesKey
import com.intellij.openapi.fileTypes.PlainSyntaxHighlighter
import com.intellij.openapi.fileTypes.SyntaxHighlighter
import com.intellij.openapi.fileTypes.SyntaxHighlighterFactory
import com.intellij.openapi.options.colors.AttributesDescriptor
import com.intellij.openapi.options.colors.ColorDescriptor
import com.intellij.openapi.options.colors.ColorSettingsPage
import javax.swing.Icon

/**
 * The semantic-highlighting attribute keys, XAML-conventioned: element names color as types,
 * attribute names AND attached properties as properties (Rider's dedicated R# property key when
 * the scheme has one — attached properties are properties, not C# static fields, so no
 * inherited italics), directives as metadata, markup extensions as keywords.
 */
object CursorialXamlColors {

    private val propertyLike: TextAttributesKey =
        TextAttributesKey.find("ReSharper.PROPERTY_IDENTIFIER").takeIf { it.defaultAttributes != null }
            ?: DefaultLanguageHighlighterColors.INSTANCE_FIELD

    val ELEMENT: TextAttributesKey =
        TextAttributesKey.createTextAttributesKey("CURSORIAL_XAML_ELEMENT", DefaultLanguageHighlighterColors.CLASS_REFERENCE)
    val ATTRIBUTE: TextAttributesKey =
        TextAttributesKey.createTextAttributesKey("CURSORIAL_XAML_ATTRIBUTE", propertyLike)
    val ATTACHED: TextAttributesKey =
        TextAttributesKey.createTextAttributesKey("CURSORIAL_XAML_ATTACHED", propertyLike)
    val DIRECTIVE: TextAttributesKey =
        TextAttributesKey.createTextAttributesKey("CURSORIAL_XAML_DIRECTIVE", DefaultLanguageHighlighterColors.METADATA)
    val EXTENSION: TextAttributesKey =
        TextAttributesKey.createTextAttributesKey("CURSORIAL_XAML_EXTENSION", DefaultLanguageHighlighterColors.KEYWORD)
    val COMMENT: TextAttributesKey =
        TextAttributesKey.createTextAttributesKey("CURSORIAL_XAML_COMMENT", DefaultLanguageHighlighterColors.BLOCK_COMMENT)
    val STRING: TextAttributesKey =
        TextAttributesKey.createTextAttributesKey("CURSORIAL_XAML_STRING", DefaultLanguageHighlighterColors.STRING)

    /** Host token kinds → keys. */
    val byKind: Map<String, TextAttributesKey> = mapOf(
        "element" to ELEMENT,
        "attribute" to ATTRIBUTE,
        "attached" to ATTACHED,
        "directive" to DIRECTIVE,
        "extension" to EXTENSION,
        "comment" to COMMENT,
        "string" to STRING,
    )

    /** Kinds a native lexer already paints correctly; applied only on plain-text files. */
    val nativeLexerKinds: Set<String> = setOf("comment", "string")
}

/** Settings → Editor → Color Scheme → Cursorial XAML: makes the keys visible and restylable. */
class CursorialXamlColorSettingsPage : ColorSettingsPage {

    override fun getDisplayName(): String = "Cursorial XAML"

    override fun getIcon(): Icon = AllIcons.FileTypes.Xml

    override fun getHighlighter(): SyntaxHighlighter =
        SyntaxHighlighterFactory.getSyntaxHighlighter(CursorialXamlLanguage, null, null) ?: PlainSyntaxHighlighter()

    override fun getAttributeDescriptors(): Array<AttributesDescriptor> = arrayOf(
        AttributesDescriptor("Element name", CursorialXamlColors.ELEMENT),
        AttributesDescriptor("Property (attribute)", CursorialXamlColors.ATTRIBUTE),
        AttributesDescriptor("Attached property", CursorialXamlColors.ATTACHED),
        AttributesDescriptor("Directive (x:)", CursorialXamlColors.DIRECTIVE),
        AttributesDescriptor("Markup extension", CursorialXamlColors.EXTENSION),
        AttributesDescriptor("Comment (plain text fallback)", CursorialXamlColors.COMMENT),
        AttributesDescriptor("String (plain text fallback)", CursorialXamlColors.STRING),
    )

    override fun getColorDescriptors(): Array<ColorDescriptor> = ColorDescriptor.EMPTY_ARRAY

    override fun getDemoText(): String = """
        <comment><!-- A Cursorial view --></comment>
        <<element>DockPanel</element> xmlns="https://cursorial.dev/ui"
                   xmlns:x="https://cursorial.dev/xaml"
                   <directive>x:Name</directive>="Root"
                   <attribute>Padding</attribute>="1"
                   <attached>Grid.Row</attached>="0"
                   <attribute>Background</attribute>="{<extension>DynamicResource</extension> {<extension>x:Static</extension> ThemeKeys.PanelBrush}}">
            <<element>TextBlock</element> <attribute>Text</attribute>=<string>"Hello"</string>/>
        </<element>DockPanel</element>>
    """.trimIndent()

    override fun getAdditionalHighlightingTagToDescriptorMap(): Map<String, TextAttributesKey> = mapOf(
        "element" to CursorialXamlColors.ELEMENT,
        "attribute" to CursorialXamlColors.ATTRIBUTE,
        "attached" to CursorialXamlColors.ATTACHED,
        "directive" to CursorialXamlColors.DIRECTIVE,
        "extension" to CursorialXamlColors.EXTENSION,
        "comment" to CursorialXamlColors.COMMENT,
        "string" to CursorialXamlColors.STRING,
    )
}
