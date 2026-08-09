package dev.cursorial.designer

import com.intellij.icons.AllIcons
import com.intellij.openapi.util.IconLoader
import com.intellij.util.IconUtil
import javax.swing.Icon

/**
 * Icons for the previewer toolbar and the property inspector's value-source annotations.
 *
 * The SVGs under `/icons` are vendored copies of JetBrains platform/product icons (the Rider
 * XAML PSI set, the terminal tool window, Gateway's link, MPS's default, ML's context picker)
 * so they resolve regardless of which optional plugins the running IDE actually has installed.
 */
object CursorialDesignerIcons {

    private fun load(path: String): Icon = IconLoader.getIcon(path, CursorialDesignerIcons::class.java)

    val SelectOff: Icon = load("/icons/pickContextOff.svg")
    val SelectOn: Icon = load("/icons/pickContextOn.svg")
    val TerminalProfile: Icon = load("/icons/terminalProfile.svg")
    val IncludeDefaults: Icon = load("/icons/valueSourceDefault.svg")

    /**
     * The theme-base toggle. Originally `AllIcons.MeetNewUi.Dark/LightThemeSelected`, which the
     * JetBrains verifier reports as unresolved against RD-262 — the `MeetNewUi` namespace is the
     * new-UI experiment's and its fields come and go. These are ours, so they cannot vanish.
     */
    val ThemeDark: Icon = load("/icons/themeDark.svg")
    val ThemeLight: Icon = load("/icons/themeLight.svg")

    /** Inline provenance icons render at 12×12 DIU so they read as annotations, not tree glyphs. */
    private fun sourceIcon(icon: Icon): Icon = IconUtil.scale(icon, null, 12f / 16f)

    private val valueSourceIcons: Map<String, Icon> = mapOf(
        "Default" to sourceIcon(load("/icons/valueSourceDefault.svg")),
        "Inherited" to sourceIcon(AllIcons.General.InheritedMethod),
        "Local" to sourceIcon(load("/icons/valueSourceLocal.svg")),
        "TemplateLiteral" to sourceIcon(load("/icons/valueSourceTemplateLiteral.svg")),
        "TemplateBinding" to sourceIcon(load("/icons/valueSourceTemplateBinding.svg")),
        "TemplateResource" to sourceIcon(load("/icons/valueSourceTemplateResource.svg")),
        "StyleSetter" to sourceIcon(load("/icons/valueSourceStyleSetter.svg")),
        "StyleWhen" to sourceIcon(load("/icons/valueSourceStyleWhen.svg")),
        "Animation" to sourceIcon(load("/icons/valueSourceAnimation.svg")),
    )

    /** The inline icon for a value-source kind (the host's `ValueSourceKind` names), or null when unknown. */
    fun valueSource(kind: String?): Icon? = kind?.let(valueSourceIcons::get)
}
