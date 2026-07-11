package dev.cursorial.designer.language

import com.intellij.codeInspection.XmlSuppressionProvider
import com.intellij.javaee.ResourceRegistrar
import com.intellij.javaee.StandardResourceProvider
import com.intellij.lang.xml.XMLParserDefinition
import com.intellij.psi.FileViewProvider
import com.intellij.psi.PsiElement
import com.intellij.psi.PsiFile
import com.intellij.psi.impl.source.xml.XmlFileImpl
import com.intellij.psi.tree.IFileElementType

/**
 * Parser definition for the CursorialXaml dialect: XML parsing verbatim, with the dialect's own
 * file element type. Without this the view provider "refuses to parse" — the daemon and
 * completion die on RuntimeExceptions and the file behaves like an opaque blob.
 */
class CursorialXamlParserDefinition : XMLParserDefinition() {

    companion object {
        val FILE = IFileElementType("CURSORIAL_XAML_FILE", CursorialXamlLanguage)
    }

    override fun getFileNodeType(): IFileElementType = FILE

    override fun createFile(viewProvider: FileViewProvider): PsiFile = XmlFileImpl(viewProvider, FILE)
}

/**
 * Registers the Cursorial xmlns URIs as ignored resources — the platform's XML validation
 * otherwise flags every root-tag namespace with "URI is not registered".
 */
class CursorialXamlResourceProvider : StandardResourceProvider {
    override fun registerResources(registrar: ResourceRegistrar) {
        registrar.addIgnoredResource("https://cursorial.dev/ui")
        registrar.addIgnoredResource("https://cursorial.dev/xaml")
        registrar.addIgnoredResource("https://cursorial.dev/xaml/design")
        registrar.addIgnoredResource("http://schemas.microsoft.com/expression/blend/2008")
        registrar.addIgnoredResource("http://schemas.openxmlformats.org/markup-compatibility/2006")
        registrar.addIgnoredResource("clr-namespace:*")
        registrar.addIgnoredResource("using:*")
    }
}

/**
 * Backstop for xmlns values the ignored-resources list can't pattern-match (project-specific
 * clr-namespace URIs): drops the "URI is not registered" highlight in Cursorial documents.
 * String-matched (English) — the ignored-resources list handles the fixed URIs regardless.
 */
class CursorialXamlHighlightFilter : com.intellij.codeInsight.daemon.impl.HighlightInfoFilter {
    override fun accept(highlightInfo: com.intellij.codeInsight.daemon.impl.HighlightInfo, file: PsiFile?): Boolean {
        if (file == null || file.language != CursorialXamlLanguage) return true
        val description = highlightInfo.description ?: return true
        return !description.startsWith("URI is not registered")
    }
}

/**
 * Suppresses the platform's XML inspections inside Cursorial documents wholesale: schemas/DTDs
 * don't model XAML (clr-namespace URIs, markup extensions, attached properties), and semantic
 * validation belongs to the language service's real parser diagnostics.
 */
class CursorialXamlSuppressionProvider : XmlSuppressionProvider() {
    override fun isProviderAvailable(file: PsiFile): Boolean = file.language == CursorialXamlLanguage

    override fun isSuppressedFor(element: PsiElement, inspectionId: String): Boolean = true

    override fun suppressForFile(element: PsiElement, inspectionId: String) {}

    override fun suppressForTag(element: PsiElement, inspectionId: String) {}
}
