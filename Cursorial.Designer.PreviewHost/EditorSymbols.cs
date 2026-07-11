using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using System.Xml.Linq;

using Cursorial.Designer.Protocol;
using Cursorial.UI.Xaml;

namespace Cursorial.Designer.PreviewHost;

/// <summary>
/// The symbol-oriented half of the editor services: semantic token classification (<c>analyze</c>
/// with <c>classify</c>), symbol resolution at a position (<c>hover</c>), XML-doc summaries from
/// the assemblies' doc files, and source locations from portable PDB sequence points
/// (<c>definition</c>). Same textual tolerance rules as completion: mid-edit documents are
/// routinely malformed, so everything anchors on the blanked-comment tag scan, never a parse.
/// </summary>
internal static partial class EditorServices
{
    [GeneratedRegex("([A-Za-z_][\\w.:-]*)\\s*=\\s*\"([^\"]*)\"")]
    private static partial Regex AttributeToken();

    [GeneratedRegex("\\{\\s*([A-Za-z_][\\w:]*)")]
    private static partial Regex ExtensionNameToken();

    // ── Semantic classification ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Classified token ranges for semantic highlighting. Semantic kinds: <c>element</c>
    /// (resolvable element names), <c>attached</c> (dotted names — attached properties and
    /// property elements), <c>directive</c> (intrinsics-prefixed attributes), <c>extension</c>
    /// (markup extension names inside braces). Base kinds — <c>comment</c>, <c>attribute</c>
    /// (plain attribute names), <c>string</c> (quoted values without extensions) — make the set
    /// a COMPLETE highlighter for hosts with no native lexer (plain-text .cxaml); IDEs with
    /// native XML coloring apply only the semantic kinds. Unresolvable names are left
    /// unclassified — the diagnostics squiggles own that story.
    /// </summary>
    internal static List<TokenInfo> ClassifyTokens(string xaml)
    {
        var blanked = BlankNonMarkup(xaml);
        var namespaces = ScanNamespaces(blanked);
        var provider = XamlLoaderOptions.DefaultMetadataProvider;
        var intrinsicsPrefix = namespaces.FirstOrDefault(n => n.Value == IntrinsicsUri).Key;
        var lineStarts = LineStarts(xaml);
        var tokens = new List<TokenInfo>();
        var resolvable = new Dictionary<string, bool>(StringComparer.Ordinal);

        void Add(int offset, int length, string kind)
        {
            var (line, column) = PositionAt(lineStarts, offset);
            tokens.Add(new TokenInfo { Line = line, Column = column, Length = length, Kind = kind });
        }

        bool Resolves(string name)
        {
            if (!resolvable.TryGetValue(name, out var known))
                resolvable[name] = known = ResolveElement(name, namespaces, provider) is not null;
            return known;
        }

        // Comments/CDATA/PIs from the ORIGINAL text — they're blanked out of everything below.
        foreach (Match region in NonMarkupRegion().Matches(xaml))
            Add(region.Index, region.Length, "comment");

        foreach (Match tag in TagToken().Matches(blanked))
        {
            var nameGroup = tag.Groups[2];
            if (nameGroup.Value.Contains('.'))
                Add(nameGroup.Index, nameGroup.Length, "attached");
            else if (Resolves(nameGroup.Value))
                Add(nameGroup.Index, nameGroup.Length, "element");

            var attributes = tag.Groups[3];
            foreach (Match attribute in AttributeToken().Matches(attributes.Value))
            {
                var attrName = attribute.Groups[1];
                var colon = attrName.Value.IndexOf(':');
                if (colon > 0 && intrinsicsPrefix is { Length: > 0 } && attrName.Value[..colon] == intrinsicsPrefix)
                    Add(attributes.Index + attrName.Index, attrName.Length, "directive");
                else if (attrName.Value.Contains('.'))
                    Add(attributes.Index + attrName.Index, attrName.Length, "attached");
                else
                    Add(attributes.Index + attrName.Index, attrName.Length, "attribute");

                var value = attribute.Groups[2];
                if (!value.Value.Contains('{'))
                {
                    // The quoted value, quotes included. Extension-bearing values stay uncolored
                    // apart from their extension-name tokens — overlapping attribute layers muddy.
                    Add(attributes.Index + value.Index - 1, value.Length + 2, "string");
                    continue;
                }

                foreach (Match extension in ExtensionNameToken().Matches(value.Value))
                {
                    var name = extension.Groups[1];
                    Add(attributes.Index + value.Index + name.Index, name.Length, "extension");
                }
            }
        }

        return tokens;
    }

    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
                starts.Add(i + 1);
        }

        return starts;
    }

    /// <summary>Offset → 1-based (line, column).</summary>
    private static (int Line, int Column) PositionAt(List<int> lineStarts, int offset)
    {
        var line = lineStarts.BinarySearch(offset);
        if (line < 0)
            line = ~line - 1;
        return (line + 1, offset - lineStarts[line] + 1);
    }

    // ── Symbol resolution at a position ─────────────────────────────────────────────────────────

    /// <summary>Everything hover/definition needs about the symbol under the caret.</summary>
    internal sealed record SymbolInfo(
        string Display,
        string Signature,
        Type? Owner,
        MemberInfo? Member,
        IReadOnlyList<string> DocIds,
        string? Detail);

    /// <summary>The symbol at a 1-based (line, column), or null when the caret is not on one.</summary>
    internal static SymbolInfo? SymbolAt(string xaml, int line, int column)
    {
        var offset = OffsetOf(xaml, line, column);
        var blanked = BlankNonMarkup(xaml);
        var namespaces = ScanNamespaces(blanked);
        var provider = XamlLoaderOptions.DefaultMetadataProvider;

        foreach (Match tag in TagToken().Matches(blanked))
        {
            if (offset < tag.Index || offset > tag.Index + tag.Length)
                continue;

            var nameGroup = tag.Groups[2];
            if (offset >= nameGroup.Index && offset <= nameGroup.Index + nameGroup.Length)
                return SymbolFromName(nameGroup.Value, namespaces, provider);

            var attributes = tag.Groups[3];
            foreach (Match attribute in AttributeToken().Matches(attributes.Value))
            {
                var attrName = attribute.Groups[1];
                var nameStart = attributes.Index + attrName.Index;
                if (offset >= nameStart && offset <= nameStart + attrName.Length)
                    return SymbolFromAttribute(nameGroup.Value, attrName.Value, namespaces, provider);

                var value = attribute.Groups[2];
                var valueStart = attributes.Index + value.Index;
                if (offset >= valueStart && offset <= valueStart + value.Length)
                    return SymbolFromValue(value.Value, offset - valueStart, namespaces, provider);
            }

            return null; // inside the tag, but between symbols
        }

        return null;
    }

    /// <summary>An element or property-element/attached name (<c>Button</c>, <c>Grid.Row</c>).</summary>
    private static SymbolInfo? SymbolFromName(string name, Dictionary<string, string> namespaces, IXamlTypeMetadataProvider provider)
    {
        var dot = name.IndexOf('.');
        if (dot > 0)
            return MemberSymbol(name[..dot], name[(dot + 1)..], namespaces, provider);

        var clr = ResolveElement(name, namespaces, provider)?.ClrType.UnderlyingSystemType;
        if (clr is null)
            return null;

        var keyword = clr switch
        {
            { IsEnum: true } => "enum",
            { IsValueType: true } => "struct",
            { IsInterface: true } => "interface",
            { IsAbstract: true, IsSealed: true } => "static class",
            _ => "class",
        };
        var baseSuffix = clr is { IsClass: true, BaseType: { } baseType } && baseType != typeof(object)
            ? $" : {baseType.Name}"
            : "";
        return new SymbolInfo(
            clr.Name,
            $"{keyword} {clr.FullName}{baseSuffix}",
            clr,
            null,
            [$"T:{DocTypeName(clr)}"],
            clr.Assembly.GetName().Name);
    }

    /// <summary>An attribute name: directive, attached (dotted), or a member of the element's type.</summary>
    private static SymbolInfo? SymbolFromAttribute(string elementName, string attributeName, Dictionary<string, string> namespaces, IXamlTypeMetadataProvider provider)
    {
        var colon = attributeName.IndexOf(':');
        if (colon > 0)
        {
            var prefix = attributeName[..colon];
            if (namespaces.TryGetValue(prefix, out var uri) && uri == IntrinsicsUri)
                return new SymbolInfo(attributeName, $"directive {attributeName}", null, null, [], "XAML intrinsics");
            attributeName = attributeName[(colon + 1)..]; // prefixed attached owner: fall through with the local name
        }

        var dot = attributeName.IndexOf('.');
        return dot > 0
            ? MemberSymbol(attributeName[..dot], attributeName[(dot + 1)..], namespaces, provider)
            : MemberSymbol(elementName, attributeName, namespaces, provider);
    }

    /// <summary>A member on an owner: instance property, or attached via the field convention.</summary>
    private static SymbolInfo? MemberSymbol(string ownerName, string memberName, Dictionary<string, string> namespaces, IXamlTypeMetadataProvider provider)
    {
        var owner = ResolveElement(ownerName, namespaces, provider);
        var clr = owner?.ClrType.UnderlyingSystemType;
        if (owner is null || clr is null)
            return null;

        var docIds = new[] { $"P:{DocTypeName(clr)}.{memberName}", $"F:{DocTypeName(clr)}.{memberName}Property" };

        if (clr.GetProperty(memberName, BindingFlags.Public | BindingFlags.Instance) is { } property)
        {
            return new SymbolInfo(
                $"{clr.Name}.{memberName}",
                $"{property.PropertyType.Name} {clr.Name}.{memberName}",
                clr,
                property,
                docIds,
                null);
        }

        var field = clr.GetField(memberName + "Property", BindingFlags.Public | BindingFlags.Static);
        if (field?.FieldType is { IsGenericType: true } fieldType && fieldType.GetGenericTypeDefinition() == typeof(Cursorial.UI.AttachedProperty<>))
        {
            return new SymbolInfo(
                $"{clr.Name}.{memberName}",
                $"attached {fieldType.GetGenericArguments()[0].Name} {clr.Name}.{memberName}",
                clr,
                field,
                docIds,
                null);
        }

        // Registered but not CLR-visible (unconventional naming): still a real member per the metadata.
        return owner.TryGetMember(memberName) is { } member
            ? new SymbolInfo(
                $"{clr.Name}.{memberName}",
                $"{member.ValueType.UnderlyingSystemType?.Name ?? "object"} {clr.Name}.{memberName}",
                clr,
                null,
                docIds,
                null)
            : null;
    }

    /// <summary>Inside an attribute value: a markup-extension name, or an x:Static member path.</summary>
    private static SymbolInfo? SymbolFromValue(string value, int offsetInValue, Dictionary<string, string> namespaces, IXamlTypeMetadataProvider provider)
    {
        // On an extension name itself ({Binding, {x:Static, {theme:Elevate)?
        foreach (Match extension in ExtensionNameToken().Matches(value))
        {
            var name = extension.Groups[1];
            if (offsetInValue >= name.Index && offsetInValue <= name.Index + name.Length)
            {
                var canonical = Canonical(name.Value, namespaces);
                if (canonical.StartsWith("x:", StringComparison.Ordinal) || canonical is "Binding" or "StaticResource" or "DynamicResource" or "TemplateBinding")
                    return new SymbolInfo(canonical, $"markup extension {{{canonical}}}", null, null, [], "XAML intrinsics");

                var clr = (ResolveElement(name.Value, namespaces, provider) ?? ResolveElement(name.Value + "Extension", namespaces, provider))
                    ?.ClrType.UnderlyingSystemType;
                return clr is null ? null : new SymbolInfo(
                    clr.Name, $"markup extension {clr.FullName}", clr, null, [$"T:{DocTypeName(clr)}"], clr.Assembly.GetName().Name);
            }
        }

        // Inside an x:Static path? Anchor on the innermost extension containing the caret.
        var open = -1;
        var depth = 0;
        for (var i = offsetInValue - 1; i >= 0; i--)
        {
            if (value[i] == '}')
                depth++;
            else if (value[i] == '{')
            {
                if (depth == 0)
                {
                    open = i;
                    break;
                }

                depth--;
            }
        }

        if (open < 0)
            return null;

        var body = value[(open + 1)..].TrimStart();
        var extensionName = new string(body.TakeWhile(c => !char.IsWhiteSpace(c) && c != '}').ToArray());
        if (Canonical(extensionName, namespaces) != "x:Static")
            return null;

        // The dotted path token around the caret.
        static bool PathChar(char c) => char.IsLetterOrDigit(c) || c is '.' or ':' or '_';
        var start = offsetInValue;
        while (start > 0 && PathChar(value[start - 1]))
            start--;
        var end = offsetInValue;
        while (end < value.Length && PathChar(value[end]))
            end++;
        var path = value[start..end];
        var lastDot = path.LastIndexOf('.');
        if (lastDot <= 0)
            return null;

        var ownerType = ResolveElement(path[..lastDot], namespaces, provider)?.ClrType.UnderlyingSystemType;
        var memberName = path[(lastDot + 1)..];
        if (ownerType is null || memberName.Length == 0)
            return null;

        if (ownerType.GetField(memberName, BindingFlags.Public | BindingFlags.Static) is { } field)
        {
            var keyword = field.IsLiteral ? "const" : field.IsInitOnly ? "static readonly" : "static";
            string? detail = null;
            try
            {
                detail = ValueFormatter.Format(field.IsLiteral ? field.GetRawConstantValue() : field.GetValue(null));
            }
            catch
            {
            }

            return new SymbolInfo(
                $"{ownerType.Name}.{memberName}",
                $"{keyword} {field.FieldType.Name} {ownerType.Name}.{memberName}",
                ownerType,
                field,
                [$"F:{DocTypeName(ownerType)}.{memberName}"],
                detail);
        }

        if (ownerType.GetProperty(memberName, BindingFlags.Public | BindingFlags.Static) is { } property)
        {
            return new SymbolInfo(
                $"{ownerType.Name}.{memberName}",
                $"static {property.PropertyType.Name} {ownerType.Name}.{memberName}",
                ownerType,
                property,
                [$"P:{DocTypeName(ownerType)}.{memberName}"],
                null);
        }

        return null;
    }

    /// <summary>XML-doc id spelling of a type name (nested types use '.').</summary>
    private static string DocTypeName(Type type) => (type.FullName ?? type.Name).Replace('+', '.');

    // ── XML documentation summaries ─────────────────────────────────────────────────────────────

    private static readonly Dictionary<string, Dictionary<string, string>?> XmlDocCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The first matching <c>&lt;summary&gt;</c> from the owner assembly's XML doc file.</summary>
    internal static string? XmlSummary(Type owner, IReadOnlyList<string> docIds)
    {
        var location = owner.Assembly.Location;
        if (string.IsNullOrEmpty(location))
            return null;

        if (!XmlDocCache.TryGetValue(location, out var map))
            XmlDocCache[location] = map = LoadXmlDocs(Path.ChangeExtension(location, ".xml"));
        if (map is null)
            return null;

        foreach (var id in docIds)
        {
            if (map.TryGetValue(id, out var summary))
                return summary;
        }

        return null;
    }

    private static Dictionary<string, string>? LoadXmlDocs(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var member in XDocument.Load(path).Descendants("member"))
            {
                var name = member.Attribute("name")?.Value;
                var summary = member.Element("summary") is { } element ? FlattenDocText(element) : null;
                if (name is not null && !string.IsNullOrWhiteSpace(summary))
                    map[name] = summary!;
            }

            return map;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Doc XML to plain text: cref/name references become their short names; whitespace collapses.</summary>
    private static string FlattenDocText(XElement element)
    {
        var parts = new List<string>();
        foreach (var node in element.DescendantNodes())
        {
            switch (node)
            {
                case XText text:
                    parts.Add(text.Value);
                    break;

                case XElement { Name.LocalName: "see" or "seealso" } reference when reference.IsEmpty:
                    var target = reference.Attribute("cref")?.Value ?? reference.Attribute("langword")?.Value ?? "";
                    var shortName = target[(target.LastIndexOf('.') + 1)..].TrimEnd('(', ')');
                    parts.Add(shortName);
                    break;

                case XElement { Name.LocalName: "paramref" or "typeparamref" } reference:
                    parts.Add(reference.Attribute("name")?.Value ?? "");
                    break;
            }
        }

        return string.Join(' ', string.Concat(parts).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    // ── Source locations from portable PDBs ─────────────────────────────────────────────────────

    /// <summary>
    /// The source location of a type or member, read from the assembly's portable PDB sequence
    /// points. Members map through their accessor/method bodies; fields and whole types fall
    /// back to the type's best document (the file holding most of its members, earliest line).
    /// Null when the assembly has no reachable portable PDB.
    /// </summary>
    internal static (string File, int Line, int Column)? SourceLocationOf(Type type, MemberInfo? member)
    {
        var location = type.Assembly.Location;
        if (string.IsNullOrEmpty(location) || !File.Exists(location))
            return null;

        try
        {
            using var peStream = File.OpenRead(location);
            using var peReader = new PEReader(peStream);
            if (!peReader.TryOpenAssociatedPortablePdb(location, path => File.Exists(path) ? File.OpenRead(path) : null, out var provider, out _) || provider is null)
                return null;

            using (provider)
            {
                var pdb = provider.GetMetadataReader();

                var methods = member switch
                {
                    PropertyInfo property => new[] { property.GetMethod, property.SetMethod }.Where(m => m is not null).Cast<MethodBase>().ToArray(),
                    MethodBase method => [method],
                    _ => [.. type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly),
                          .. type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)],
                };

                var hits = new List<(string File, int Line, int Column)>();
                foreach (var method in methods)
                {
                    var first = FirstSequencePoint(pdb, method);
                    if (first is not null)
                    {
                        if (member is not null and not FieldInfo)
                            return first; // a member's own body is authoritative
                        hits.Add(first.Value);
                    }
                }

                if (hits.Count == 0)
                    return null;

                var bestFile = hits.GroupBy(h => h.File, StringComparer.Ordinal).MaxBy(g => g.Count())!.Key;
                return hits.Where(h => h.File == bestFile).MinBy(h => h.Line);
            }
        }
        catch
        {
            return null;
        }
    }

    private static (string File, int Line, int Column)? FirstSequencePoint(MetadataReader pdb, MethodBase method)
    {
        if (MetadataTokens.EntityHandle(method.MetadataToken) is not { Kind: HandleKind.MethodDefinition } handle)
            return null;

        var debugInfo = pdb.GetMethodDebugInformation(((MethodDefinitionHandle)handle).ToDebugInformationHandle());
        if (debugInfo.SequencePointsBlob.IsNil)
            return null;

        foreach (var point in debugInfo.GetSequencePoints())
        {
            if (point.IsHidden || point.Document.IsNil)
                continue;
            return (pdb.GetString(pdb.GetDocument(point.Document).Name), point.StartLine, point.StartColumn);
        }

        return null;
    }
}
