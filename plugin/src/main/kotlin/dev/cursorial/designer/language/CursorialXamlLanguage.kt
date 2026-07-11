package dev.cursorial.designer.language

import com.intellij.icons.AllIcons
import com.intellij.lang.xml.XMLLanguage
import com.intellij.openapi.fileTypes.LanguageFileType
import com.intellij.openapi.fileTypes.FileType
import com.intellij.openapi.fileTypes.impl.FileTypeOverrider
import com.intellij.openapi.vfs.VirtualFile
import dev.cursorial.designer.editor.CursorialPreviewEditorProvider
import javax.swing.Icon

/**
 * Cursorial XAML as its own frontend language, derived from XML so the platform's XML services
 * (lexer/PSI, syntax coloring, tag matching, folding, structure view) apply natively, while our
 * language-keyed extensions (annotator, docs, completion) register against a language Rider
 * cannot intercept: Rider routes editor actions for its OWN Xaml language through the ReSharper
 * backend before platform EPs run — docs and go-to-declaration never fired for .xaml until
 * these files stopped being Rider's.
 */
object CursorialXamlLanguage : XMLLanguage(INSTANCE, "CursorialXaml", "text/xml")

object CursorialXamlFileType : LanguageFileType(CursorialXamlLanguage) {
    override fun getName(): String = "Cursorial XAML"
    override fun getDescription(): String = "Cursorial XAML markup"
    override fun getDefaultExtension(): String = "cxaml"
    override fun getIcon(): Icon = AllIcons.FileTypes.Xml
}

/**
 * Claims `.xaml` files carrying the cursorial.dev xmlns for [CursorialXamlFileType], overriding
 * Rider's registered XAML file type per file. Every other .xaml (WPF, Avalonia, …) is untouched.
 * The content sniff is cached per modification stamp — overriders run on hot paths.
 */
class CursorialXamlFileTypeOverrider : FileTypeOverrider {
    override fun getOverriddenFileType(file: VirtualFile): FileType? =
        if ("xaml".equals(file.extension, ignoreCase = true) && CursorialPreviewEditorProvider.isCursorialXaml(file))
            CursorialXamlFileType
        else
            null
}
