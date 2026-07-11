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

    // Resolved lazily: probing the R# key's defaults goes through the TextAttributesKey
    // defaults-provider SERVICE, and class initialization must not depend on services
    // (the platform logs an error). First read happens in a normal call frame instead.
    private val propertyLike: TextAttributesKey by lazy {
        TextAttributesKey.find("ReSharper.PROPERTY_IDENTIFIER").takeIf { it.defaultAttributes != null }
            ?: DefaultLanguageHighlighterColors.INSTANCE_FIELD
    }

    val ELEMENT: TextAttributesKey =
        TextAttributesKey.createTextAttributesKey("CURSORIAL_XAML_ELEMENT", DefaultLanguageHighlighterColors.CLASS_REFERENCE)
    val ATTRIBUTE: TextAttributesKey by lazy {
        TextAttributesKey.createTextAttributesKey("CURSORIAL_XAML_ATTRIBUTE", propertyLike)
    }
    val ATTACHED: TextAttributesKey by lazy {
        TextAttributesKey.createTextAttributesKey("CURSORIAL_XAML_ATTACHED", propertyLike)
    }
    val DIRECTIVE: TextAttributesKey =
        TextAttributesKey.createTextAttributesKey("CURSORIAL_XAML_DIRECTIVE", DefaultLanguageHighlighterColors.METADATA)
    val EXTENSION: TextAttributesKey =
        TextAttributesKey.createTextAttributesKey("CURSORIAL_XAML_EXTENSION", DefaultLanguageHighlighterColors.KEYWORD)
    val COMMENT: TextAttributesKey =
        TextAttributesKey.createTextAttributesKey("CURSORIAL_XAML_COMMENT", DefaultLanguageHighlighterColors.BLOCK_COMMENT)
    val STRING: TextAttributesKey =
        TextAttributesKey.createTextAttributesKey("CURSORIAL_XAML_STRING", DefaultLanguageHighlighterColors.STRING)
    val BRACE: TextAttributesKey =
        TextAttributesKey.createTextAttributesKey("CURSORIAL_XAML_BRACE", DefaultLanguageHighlighterColors.BRACES)
    val DOT: TextAttributesKey =
        TextAttributesKey.createTextAttributesKey("CURSORIAL_XAML_DOT", DefaultLanguageHighlighterColors.DOT)
    val PARAMETER: TextAttributesKey by lazy {
        TextAttributesKey.createTextAttributesKey("CURSORIAL_XAML_PARAMETER", propertyLike)
    }
    val RESOURCE_KEY: TextAttributesKey =
        TextAttributesKey.createTextAttributesKey("CURSORIAL_XAML_RESOURCE_KEY", DefaultLanguageHighlighterColors.CONSTANT)
    val BINDING_PATH: TextAttributesKey by lazy {
        TextAttributesKey.createTextAttributesKey("CURSORIAL_XAML_BINDING_PATH", propertyLike)
    }
    val ELEMENT_REF: TextAttributesKey =
        TextAttributesKey.createTextAttributesKey("CURSORIAL_XAML_ELEMENT_REF", DefaultLanguageHighlighterColors.LOCAL_VARIABLE)
    val STATIC_MEMBER: TextAttributesKey =
        TextAttributesKey.createTextAttributesKey("CURSORIAL_XAML_STATIC_MEMBER", DefaultLanguageHighlighterColors.CONSTANT)
    val NUMBER: TextAttributesKey =
        TextAttributesKey.createTextAttributesKey("CURSORIAL_XAML_NUMBER", DefaultLanguageHighlighterColors.NUMBER)
    val ENUM_VALUE: TextAttributesKey =
        TextAttributesKey.createTextAttributesKey("CURSORIAL_XAML_ENUM_VALUE", DefaultLanguageHighlighterColors.CONSTANT)
    val BOOL: TextAttributesKey =
        TextAttributesKey.createTextAttributesKey("CURSORIAL_XAML_BOOL", DefaultLanguageHighlighterColors.KEYWORD)
    val STYLE_CLASS: TextAttributesKey =
        TextAttributesKey.createTextAttributesKey("CURSORIAL_XAML_STYLE_CLASS", DefaultLanguageHighlighterColors.INSTANCE_METHOD)
    val PSEUDO_CLASS: TextAttributesKey =
        TextAttributesKey.createTextAttributesKey("CURSORIAL_XAML_PSEUDO_CLASS", DefaultLanguageHighlighterColors.METADATA)

    /** Host token kinds → keys. Lazy: the property-like keys resolve against R#'s scheme on first use. */
    val byKind: Map<String, TextAttributesKey> by lazy { mapOf(
        "element" to ELEMENT,
        "attribute" to ATTRIBUTE,
        "attached" to ATTACHED,
        "directive" to DIRECTIVE,
        "extension" to EXTENSION,
        "comment" to COMMENT,
        "string" to STRING,
        "brace" to BRACE,
        "dot" to DOT,
        "parameter" to PARAMETER,
        "resourceKey" to RESOURCE_KEY,
        "bindingPath" to BINDING_PATH,
        "elementRef" to ELEMENT_REF,
        "staticMember" to STATIC_MEMBER,
        "number" to NUMBER,
        "enumValue" to ENUM_VALUE,
        "bool" to BOOL,
        "styleClass" to STYLE_CLASS,
        "pseudoClass" to PSEUDO_CLASS,
    ) }

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
        AttributesDescriptor("Markup extension braces", CursorialXamlColors.BRACE),
        AttributesDescriptor("Member access dot", CursorialXamlColors.DOT),
        AttributesDescriptor("Extension parameter", CursorialXamlColors.PARAMETER),
        AttributesDescriptor("Resource key", CursorialXamlColors.RESOURCE_KEY),
        AttributesDescriptor("Binding path", CursorialXamlColors.BINDING_PATH),
        AttributesDescriptor("Element reference", CursorialXamlColors.ELEMENT_REF),
        AttributesDescriptor("Static member (x:Static)", CursorialXamlColors.STATIC_MEMBER),
        AttributesDescriptor("Number literal", CursorialXamlColors.NUMBER),
        AttributesDescriptor("Enum value", CursorialXamlColors.ENUM_VALUE),
        AttributesDescriptor("Boolean literal", CursorialXamlColors.BOOL),
        AttributesDescriptor("Style class (selector)", CursorialXamlColors.STYLE_CLASS),
        AttributesDescriptor("Pseudo-class (selector)", CursorialXamlColors.PSEUDO_CLASS),
    )

    override fun getColorDescriptors(): Array<ColorDescriptor> = ColorDescriptor.EMPTY_ARRAY

    override fun getDemoText(): String = """
        <comment><!-- A Cursorial view --></comment>
        <<element>DockPanel</element> xmlns="https://cursorial.dev/ui"
                   xmlns:x="https://cursorial.dev/xaml"
                   <directive>x:Name</directive>="Root"
                   <attribute>Padding</attribute>="<number>1</number>"
                   <element>Grid</element><dot>.</dot><attached>Row</attached>="<number>0</number>"
                   <attribute>Visibility</attribute>="<enumValue>Visible</enumValue>"
                   <attribute>IsEnabled</attribute>="<bool>True</bool>"
                   <attribute>Background</attribute>="<brace>{</brace><extension>DynamicResource</extension> <brace>{</brace><extension>x:Static</extension> <element>ThemeKeys</element><dot>.</dot><staticMember>PanelBrush</staticMember><brace>}</brace><brace>}</brace>"
                   <attribute>Text</attribute>="<brace>{</brace><extension>Binding</extension> <parameter>Path</parameter>=<bindingPath>Title</bindingPath>, <parameter>ElementName</parameter>=<elementRef>Root</elementRef><brace>}</brace>"
                   <attribute>Tag</attribute>="<brace>{</brace><extension>StaticResource</extension> <resourceKey>PanelAccent</resourceKey><brace>}</brace>">
            <<element>Style</element> <attribute>Selector</attribute>="<element>Button</element><styleClass>.accent</styleClass><pseudoClass>:pointerover</pseudoClass> <dot>></dot> <element>TextBlock</element>"/>
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
        "brace" to CursorialXamlColors.BRACE,
        "dot" to CursorialXamlColors.DOT,
        "parameter" to CursorialXamlColors.PARAMETER,
        "resourceKey" to CursorialXamlColors.RESOURCE_KEY,
        "bindingPath" to CursorialXamlColors.BINDING_PATH,
        "elementRef" to CursorialXamlColors.ELEMENT_REF,
        "staticMember" to CursorialXamlColors.STATIC_MEMBER,
        "number" to CursorialXamlColors.NUMBER,
        "enumValue" to CursorialXamlColors.ENUM_VALUE,
        "bool" to CursorialXamlColors.BOOL,
        "styleClass" to CursorialXamlColors.STYLE_CLASS,
        "pseudoClass" to CursorialXamlColors.PSEUDO_CLASS,
    )
}
