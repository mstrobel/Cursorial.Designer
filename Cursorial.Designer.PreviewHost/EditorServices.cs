using System.Text.RegularExpressions;

using Cursorial.Designer.Protocol;
using Cursorial.UI.Xaml;

namespace Cursorial.Designer.PreviewHost;

/// <summary>
/// The editor-service brain behind <c>analyze</c> and <c>complete</c>: pure text + metadata
/// provider, no preview session. Mid-edit documents are routinely malformed XML, so nothing here
/// depends on a successful parse — xmlns declarations are scanned from the root tag text (the
/// framework's root-only xmlns rule makes that reliable), and the completion context is detected
/// with a lightweight backward scan from the caret.
/// </summary>
internal static partial class EditorServices
{
    [GeneratedRegex("xmlns(?::([A-Za-z_][\\w.-]*))?\\s*=\\s*\"([^\"]*)\"")]
    private static partial Regex XmlnsDeclaration();

    /// <summary>
    /// The document's prefix → xmlns-URI map, scanned textually from the first element tag.
    /// The empty-string key is the default xmlns. Tolerant of unclosed/malformed documents.
    /// </summary>
    internal static Dictionary<string, string> ScanNamespaces(string xaml)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        // Find the first real element tag (skip the XML declaration and comments).
        var start = 0;
        while (start < xaml.Length)
        {
            start = xaml.IndexOf('<', start);
            if (start < 0)
                return map;
            if (start + 1 < xaml.Length && (char.IsLetter(xaml[start + 1]) || xaml[start + 1] == '_'))
                break;
            start++;
        }

        if (start < 0 || start >= xaml.Length)
            return map;

        var end = xaml.IndexOf('>', start);
        var rootTag = end < 0 ? xaml[start..] : xaml[start..end];

        foreach (Match match in XmlnsDeclaration().Matches(rootTag))
            map[match.Groups[1].Success ? match.Groups[1].Value : string.Empty] = match.Groups[2].Value;

        return map;
    }

    internal enum ContextKind
    {
        None,
        ElementName,
        AttributeName,
        AttributeValue,
    }

    internal readonly record struct CompletionContext(
        ContextKind Kind,
        string ElementName,
        string AttributeName,
        string Prefix);

    /// <summary>
    /// Detects what the caret is completing: an element name (right after <c>&lt;</c>), an
    /// attribute name (inside a tag, outside quotes), or an attribute value (inside quotes).
    /// </summary>
    internal static CompletionContext Detect(string xaml, int offset)
    {
        offset = Math.Clamp(offset, 0, xaml.Length);
        var open = xaml.LastIndexOf('<', Math.Max(0, offset - 1));
        if (open < 0)
            return new CompletionContext(ContextKind.None, "", "", "");

        var close = xaml.LastIndexOf('>', Math.Max(0, offset - 1));
        if (close > open)
            return new CompletionContext(ContextKind.None, "", "", ""); // between tags

        var segment = xaml[(open + 1)..offset];
        if (segment.StartsWith('/'))
            return new CompletionContext(ContextKind.None, "", "", ""); // closing tag

        // No whitespace yet → still typing the element name itself.
        if (!segment.Any(char.IsWhiteSpace))
            return new CompletionContext(ContextKind.ElementName, "", "", segment);

        var elementName = segment.TakeWhile(c => !char.IsWhiteSpace(c)).ToArray() is { Length: > 0 } name
            ? new string(name)
            : "";

        // An odd number of quotes means the caret sits inside an attribute value.
        if (segment.Count(c => c == '"') % 2 == 1)
        {
            var quote = segment.LastIndexOf('"');
            var beforeQuote = segment[..quote].TrimEnd();
            if (!beforeQuote.EndsWith('='))
                return new CompletionContext(ContextKind.None, "", "", "");

            var attrStart = beforeQuote.Length - 1;
            while (attrStart > 0 && !char.IsWhiteSpace(beforeQuote[attrStart - 1]))
                attrStart--;
            var attributeName = beforeQuote[attrStart..^1].TrimEnd();
            return new CompletionContext(ContextKind.AttributeValue, elementName, attributeName, segment[(quote + 1)..]);
        }

        // Attribute-name position: the prefix is the word being typed after the last whitespace.
        var prefixStart = segment.Length;
        while (prefixStart > 0 && !char.IsWhiteSpace(segment[prefixStart - 1]))
            prefixStart--;
        var prefix = segment[prefixStart..];
        if (prefix.Contains('=') || prefix.Contains('"'))
            return new CompletionContext(ContextKind.None, "", "", "");

        return new CompletionContext(ContextKind.AttributeName, elementName, "", prefix);
    }

    /// <summary>Computes completion items for a 1-based (line, column) position.</summary>
    internal static List<CompletionItemInfo> Complete(string xaml, int line, int column)
    {
        var offset = OffsetOf(xaml, line, column);
        var context = Detect(xaml, offset);
        var namespaces = ScanNamespaces(xaml);
        var provider = XamlLoaderOptions.DefaultMetadataProvider;
        var items = new List<CompletionItemInfo>();

        switch (context.Kind)
        {
            case ContextKind.ElementName:
            {
                foreach (var (prefix, uri) in namespaces.OrderBy(n => n.Key, StringComparer.Ordinal))
                {
                    foreach (var name in provider.GetKnownTypeNames(uri))
                    {
                        items.Add(new CompletionItemInfo
                        {
                            Text = prefix.Length == 0 ? name : $"{prefix}:{name}",
                            Kind = "element",
                            Detail = provider.GetClrNamespaces(uri).FirstOrDefault(),
                        });
                    }
                }

                break;
            }

            case ContextKind.AttributeName:
            {
                var type = ResolveElement(context.ElementName, namespaces, provider);
                if (type is not null)
                {
                    foreach (var member in provider.GetKnownMemberNames(type.ClrType))
                        items.Add(new CompletionItemInfo { Text = member, Kind = "attribute" });
                }

                // The intrinsics directives, under whatever prefix maps to them (conventionally x).
                var intrinsicsPrefix = namespaces.FirstOrDefault(n => n.Value == "https://cursorial.dev/xaml").Key;
                if (intrinsicsPrefix is { Length: > 0 })
                {
                    foreach (var directive in new[] { "Name", "Key", "Class", "DataType" })
                        items.Add(new CompletionItemInfo { Text = $"{intrinsicsPrefix}:{directive}", Kind = "attribute", Detail = "directive" });
                }

                break;
            }

            case ContextKind.AttributeValue:
            {
                var valueType = ResolveAttributeValueType(context, namespaces, provider);
                var underlying = valueType is null ? null : Nullable.GetUnderlyingType(valueType) ?? valueType;
                if (underlying is null)
                    break;

                if (underlying.IsEnum)
                {
                    foreach (var name in Enum.GetNames(underlying))
                        items.Add(new CompletionItemInfo { Text = name, Kind = "value", Detail = underlying.Name });
                }
                else if (underlying == typeof(bool))
                {
                    items.Add(new CompletionItemInfo { Text = "True", Kind = "value" });
                    items.Add(new CompletionItemInfo { Text = "False", Kind = "value" });
                }

                break;
            }
        }

        return items;
    }

    private static XamlType? ResolveElement(string elementName, Dictionary<string, string> namespaces, IXamlTypeMetadataProvider provider)
    {
        var colon = elementName.IndexOf(':');
        var prefix = colon > 0 ? elementName[..colon] : string.Empty;
        var localName = colon > 0 ? elementName[(colon + 1)..] : elementName;
        if (localName.Contains('.'))
            return null; // property elements aren't completable targets here

        return namespaces.TryGetValue(prefix, out var uri) ? provider.TryGetType(uri, localName).Type : null;
    }

    private static Type? ResolveAttributeValueType(in CompletionContext context, Dictionary<string, string> namespaces, IXamlTypeMetadataProvider provider)
    {
        var attribute = context.AttributeName;
        if (attribute.Contains(':'))
            return null; // directives (x:) and prefixed attached owners: no value completion yet

        string owner;
        string member;
        var dot = attribute.IndexOf('.');
        if (dot > 0)
        {
            owner = attribute[..dot];
            member = attribute[(dot + 1)..];
        }
        else
        {
            owner = context.ElementName;
            member = attribute;
        }

        var type = ResolveElement(owner, namespaces, provider);
        return type?.TryGetMember(member)?.ValueType.UnderlyingSystemType;
    }

    private static int OffsetOf(string text, int line, int column)
    {
        var current = 1;
        var offset = 0;
        while (current < line && offset < text.Length)
        {
            var newline = text.IndexOf('\n', offset);
            if (newline < 0)
                break;
            offset = newline + 1;
            current++;
        }

        return Math.Clamp(offset + column - 1, 0, text.Length);
    }
}
