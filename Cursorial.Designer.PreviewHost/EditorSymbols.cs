using System.Globalization;
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

        var dottedKinds = new Dictionary<string, string>(StringComparer.Ordinal);
        var memberTypes = new Dictionary<string, Type?>(StringComparer.Ordinal);

        // Attached property vs property element: both are Owner.Member, but attached members
        // exist as AttachedProperty<T> fields, property elements as instance members.
        string DottedMemberKind(string ownerName, string memberName)
        {
            var cacheKey = $"{ownerName}.{memberName}";
            if (dottedKinds.TryGetValue(cacheKey, out var known))
                return known;

            var owner = ResolveElement(ownerName, namespaces, provider)?.ClrType.UnderlyingSystemType;
            var field = owner?.GetField(memberName + "Property", BindingFlags.Public | BindingFlags.Static);
            var kind = field?.FieldType is { IsGenericType: true } fieldType
                       && fieldType.GetGenericTypeDefinition() == typeof(Cursorial.UI.AttachedProperty<>)
                ? "attached"
                : "attribute";
            return dottedKinds[cacheKey] = kind;
        }

        // Owner.Member splits into type + delimiter + member; undotted names are plain members.
        void AddName(int start, string name)
        {
            var dot = name.IndexOf('.');
            if (dot <= 0)
            {
                Add(start, name.Length, "attribute");
                return;
            }

            var ownerName = name[..dot];
            var memberName = name[(dot + 1)..];
            if (Resolves(ownerName))
                AddTypeName(start, ownerName);
            Add(start + dot, 1, "dot");
            if (memberName.Length > 0)
                Add(start + dot + 1, memberName.Length, DottedMemberKind(ownerName, memberName));
        }

        // The x:Class value: a full CLR name painted namespace-dot-type when it resolves to a
        // loaded type (the code-behind must be built), string otherwise.
        void ClassifyClassDirectiveValue(int contentStart, string content)
        {
            var name = content.Trim();
            if (name.Length == 0 || EditorServices.ResolveClrTypeName(name) is null)
            {
                Add(contentStart - 1, content.Length + 2, "string");
                return;
            }

            var offset = contentStart + (content.Length - content.TrimStart().Length);
            var lastDot = name.LastIndexOf('.');
            if (lastDot > 0)
            {
                Add(offset, lastDot, "namespace");
                Add(offset + lastDot, 1, "dot");
                Add(offset + lastDot + 1, name.Length - lastDot - 1, "element");
            }
            else
            {
                Add(offset, name.Length, "element");
            }
        }

        // A possibly-prefixed type reference: the xmlns prefix gets its own kind (consistent
        // with the declaration side), the colon paints as a delimiter, the local name as the type.
        void AddTypeName(int start, string name)
        {
            var prefixColon = name.IndexOf(':');
            if (prefixColon <= 0)
            {
                Add(start, name.Length, "element");
                return;
            }

            Add(start, prefixColon, "namespace");
            Add(start + prefixColon, 1, "dot");
            if (prefixColon + 1 < name.Length)
                Add(start + prefixColon + 1, name.Length - prefixColon - 1, "element");
        }

        Type? ValueType(string elementName, string attributeName)
        {
            var cacheKey = $"{elementName} {attributeName}";
            if (!memberTypes.TryGetValue(cacheKey, out var type))
                memberTypes[cacheKey] = type = AttributeValueType(elementName, attributeName, namespaces, provider);
            return type is null ? null : Nullable.GetUnderlyingType(type) ?? type;
        }

        // Plain values classify by the property's CLR type; unrecognized ones stay strings.
        void ClassifyPlainValue(int contentStart, string content, string elementName, string attributeName)
        {
            var type = content.Length > 0 ? ValueType(elementName, attributeName) : null;
            if (type is not null)
            {
                if (type.IsEnum && Enum.GetNames(type).Any(n => string.Equals(n, content, StringComparison.OrdinalIgnoreCase)))
                {
                    Add(contentStart, content.Length, "enumValue");
                    return;
                }

                if (type == typeof(bool) && bool.TryParse(content, out _))
                {
                    Add(contentStart, content.Length, "bool");
                    return;
                }

                if (IsNumeric(type) && double.TryParse(content, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                {
                    Add(contentStart, content.Length, "number");
                    return;
                }

                // Type-typed values (DataType, x:TypeArguments-style) are type references.
                if (type == typeof(Type) && Resolves(content.Trim()))
                {
                    AddTypeName(contentStart, content);
                    return;
                }

                // Converter oddballs with a known literal grammar: GridLength is Auto (a
                // keyword), fixed cells, or star with an optional weight.
                if (type.Name == "GridLength")
                {
                    if (string.Equals(content, "Auto", StringComparison.OrdinalIgnoreCase))
                    {
                        Add(contentStart, content.Length, "enumValue");
                        return;
                    }

                    if (Regex.IsMatch(content, @"^(?:\d+(?:\.\d+)?)?\*$|^\d+$"))
                    {
                        Add(contentStart, content.Length, "number");
                        return;
                    }
                }
            }

            Add(contentStart - 1, content.Length + 2, "string"); // quotes included
        }

        // {Extension arg, Param=Value, {nested}}: braces and every argument classified by role.
        void ClassifyExtensionValue(int contentStart, string content)
        {
            var i = 0;
            while (i < content.Length)
            {
                if (content[i] == '{' && (i + 1 >= content.Length || content[i + 1] != '}'))
                    i = ParseExtension(i);
                else
                    i++;
            }

            int ParseExtension(int at)
            {
                Add(contentStart + at, 1, "brace");
                var i = at + 1;
                while (i < content.Length && char.IsWhiteSpace(content[i]))
                    i++;
                var nameStart = i;
                while (i < content.Length && (char.IsLetterOrDigit(content[i]) || content[i] is ':' or '_'))
                    i++;
                if (i > nameStart)
                    Add(contentStart + nameStart, i - nameStart, "extension");
                var extensionName = Canonical(content[nameStart..i], namespaces);
                var positional = 0;

                while (i < content.Length)
                {
                    var c = content[i];
                    if (c == '}')
                    {
                        Add(contentStart + i, 1, "brace");
                        return i + 1;
                    }

                    if (c == '{')
                    {
                        i = ParseExtension(i);
                        continue;
                    }

                    if (char.IsWhiteSpace(c) || c == ',')
                    {
                        i++;
                        continue;
                    }

                    var tokenStart = i;
                    while (i < content.Length && content[i] is not (',' or '}' or '=' or '{'))
                        i++;
                    var token = content[tokenStart..i].TrimEnd();
                    if (i < content.Length && content[i] == '=')
                    {
                        if (token.Length > 0)
                            Add(contentStart + tokenStart, token.Length, "parameter");
                        i++;
                        while (i < content.Length && char.IsWhiteSpace(content[i]))
                            i++;
                        if (i < content.Length && content[i] == '{')
                        {
                            i = ParseExtension(i);
                            continue;
                        }

                        var valueStart = i;
                        while (i < content.Length && content[i] is not (',' or '}'))
                            i++;
                        var value = content[valueStart..i].TrimEnd();
                        if (value.Length > 0 && ParameterValueKind(extensionName, token, value) is { } kind)
                        {
                            if (kind == "element")
                                AddTypeName(contentStart + valueStart, value);
                            else
                                Add(contentStart + valueStart, value.Length, kind);
                        }
                        continue;
                    }

                    if (token.Length > 0)
                        ClassifyArgument(extensionName, positional++, contentStart + tokenStart, token);
                }

                return i;
            }
        }

        void ClassifyArgument(string extensionName, int index, int absStart, string token)
        {
            switch (extensionName)
            {
                case "StaticResource" or "DynamicResource":
                    Add(absStart, token.Length, "resourceKey");
                    break;

                case "x:Static":
                    var lastDot = token.LastIndexOf('.');
                    if (lastDot > 0)
                    {
                        AddTypeName(absStart, token[..lastDot]);
                        Add(absStart + lastDot, 1, "dot");
                        if (lastDot + 1 < token.Length)
                            Add(absStart + lastDot + 1, token.Length - lastDot - 1, "staticMember");
                    }
                    else
                    {
                        AddTypeName(absStart, token);
                    }

                    break;

                case "x:Type":
                    AddTypeName(absStart, token);
                    break;

                case "x:Reference":
                    Add(absStart, token.Length, "elementRef");
                    break;

                case "Binding" or "TemplateBinding" when index == 0:
                    Add(absStart, token.Length, "bindingPath");
                    break;

                case "RelativeSource" when index == 0:
                    if (ParameterValueKind("RelativeSource", "Mode", token) is { } modeKind)
                        Add(absStart, token.Length, modeKind); // shorthand {RelativeSource Self}
                    break;
            }
        }

        // Style selector text (design doc §3.1): types, .classes, #names, :pseudo-classes,
        // the >/,/^ combinators, and /template/. Resolvable type tokens color as elements and
        // navigate; #name shares the element-reference role with x:Reference.
        void ClassifySelectorValue(int contentStart, string content)
        {
            static bool IdentChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-';
            var i = 0;
            while (i < content.Length)
            {
                var c = content[i];
                if (c is ',' or '>' or '^')
                {
                    Add(contentStart + i, 1, "dot");
                    i++;
                    continue;
                }

                if (c == '/')
                {
                    var close = content.IndexOf('/', i + 1);
                    var length = close > i ? close - i + 1 : 1;
                    Add(contentStart + i, length, "extension"); // /template/
                    i += length;
                    continue;
                }

                if (c is '.' or '#' or ':')
                {
                    var nameStart = i + 1;
                    var j = nameStart;
                    while (j < content.Length && IdentChar(content[j]))
                        j++;
                    if (j > nameStart)
                    {
                        var kind = c switch { '.' => "styleClass", '#' => "elementRef", _ => "pseudoClass" };
                        Add(contentStart + i, j - i, kind); // marker char included
                    }

                    i = Math.Max(j, i + 1);
                    continue;
                }

                if (IdentChar(c))
                {
                    var start = i;
                    while (i < content.Length && IdentChar(content[i]))
                        i++;
                    if (Resolves(content[start..i]))
                        Add(contentStart + start, i - start, "element");
                    continue;
                }

                i++;
            }
        }

        string? ParameterValueKind(string extensionName, string parameter, string value)
        {
            if (parameter == "ElementName")
                return "elementRef";
            if (extensionName == "Binding" && parameter == "Path")
                return "bindingPath";
            if (parameter == "AncestorType")
                return Resolves(value) ? "element" : null;

            var extensionType = ResolveElement(extensionName, namespaces, provider)
                ?? ResolveElement(extensionName + "Extension", namespaces, provider);
            var memberType = extensionType?.TryGetMember(parameter)?.ValueType.UnderlyingSystemType;
            var underlying = memberType is null ? null : Nullable.GetUnderlyingType(memberType) ?? memberType;
            if (underlying is null)
                return null;
            if (underlying.IsEnum && Enum.GetNames(underlying).Any(n => string.Equals(n, value, StringComparison.OrdinalIgnoreCase)))
                return "enumValue";
            if (underlying == typeof(bool) && bool.TryParse(value, out _))
                return "bool";
            return null;
        }

        // Comments/CDATA/PIs from the ORIGINAL text — they're blanked out of everything below.
        foreach (Match region in NonMarkupRegion().Matches(xaml))
            Add(region.Index, region.Length, "comment");

        foreach (Match tag in TagToken().Matches(blanked))
        {
            var nameGroup = tag.Groups[2];
            var elementName = nameGroup.Value;
            if (elementName.Contains('.'))
                AddName(nameGroup.Index, elementName);
            else if (Resolves(elementName))
                AddTypeName(nameGroup.Index, elementName);

            var attributes = tag.Groups[3];
            foreach (Match attribute in AttributeToken().Matches(attributes.Value))
            {
                var attrName = attribute.Groups[1];
                var colon = attrName.Value.IndexOf(':');
                if (colon > 0 && intrinsicsPrefix is { Length: > 0 } && attrName.Value[..colon] == intrinsicsPrefix)
                {
                    Add(attributes.Index + attrName.Index, attrName.Length, "directive");
                }
                else if (colon > 0 && attrName.Value[..colon] == "xmlns")
                {
                    var declStart = attributes.Index + attrName.Index;
                    Add(declStart, colon, "attribute");
                    Add(declStart + colon, 1, "dot");
                    Add(declStart + colon + 1, attrName.Length - colon - 1, "namespace");
                }
                else
                {
                    AddName(attributes.Index + attrName.Index, attrName.Value);
                }

                var value = attribute.Groups[2];
                var attrColon = attrName.Value.IndexOf(':');
                var isClassDirective = attrColon > 0
                    && attrName.Value[(attrColon + 1)..] == "Class"
                    && intrinsicsPrefix is { Length: > 0 } && attrName.Value[..attrColon] == intrinsicsPrefix;
                if (isClassDirective)
                    ClassifyClassDirectiveValue(attributes.Index + value.Index, value.Value);
                else if (attrName.Value == "Selector")
                    ClassifySelectorValue(attributes.Index + value.Index, value.Value);
                else if (value.Value.Contains('{'))
                    ClassifyExtensionValue(attributes.Index + value.Index, value.Value);
                else
                    ClassifyPlainValue(attributes.Index + value.Index, value.Value, elementName, attrName.Value);
            }
        }

        return tokens;
    }

    private static bool IsNumeric(Type type) => Type.GetTypeCode(type) is >= TypeCode.SByte and <= TypeCode.Decimal;

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
        string? Detail,
        (string File, int Line, int Column)? Location = null);

    /// <summary>The symbol at a 1-based (line, column), or null when the caret is not on one.</summary>
    internal static SymbolInfo? SymbolAt(string xaml, int line, int column, string? documentPath = null)
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
            {
                var offsetInName = offset - nameGroup.Index;
                var nameColon = nameGroup.Value.IndexOf(':');
                if (nameColon > 0 && offsetInName <= nameColon)
                    return XmlnsPrefixSymbol(blanked, documentPath, nameGroup.Value[..nameColon], namespaces);
                return SymbolFromName(nameGroup.Value, offsetInName, namespaces, provider);
            }

            var attributes = tag.Groups[3];
            foreach (Match attribute in AttributeToken().Matches(attributes.Value))
            {
                var attrName = attribute.Groups[1];
                var nameStart = attributes.Index + attrName.Index;
                if (offset >= nameStart && offset <= nameStart + attrName.Length)
                {
                    var offsetInAttr = offset - nameStart;
                    var attrColon = attrName.Value.IndexOf(':');
                    if (attrColon > 0 && offsetInAttr <= attrColon)
                        return XmlnsPrefixSymbol(blanked, documentPath, attrName.Value[..attrColon], namespaces);
                    return SymbolFromAttribute(nameGroup.Value, attrName.Value, offsetInAttr, namespaces, provider);
                }

                var value = attribute.Groups[2];
                var valueStart = attributes.Index + value.Index;
                if (offset >= valueStart && offset <= valueStart + value.Length)
                    return SymbolFromValue(blanked, documentPath, nameGroup.Value, attrName.Value, value.Value, offset - valueStart, valueStart, tag.Index, namespaces, provider);
            }

            return null; // inside the tag, but between symbols
        }

        return null;
    }

    /// <summary>
    /// An element or property-element/attached name (<c>Button</c>, <c>Grid.Row</c>). For dotted
    /// names the caret side of the '.' picks the symbol: on <c>Grid</c> → the type, on
    /// <c>Row</c> → the member.
    /// </summary>
    private static SymbolInfo? SymbolFromName(string name, int offsetInName, Dictionary<string, string> namespaces, IXamlTypeMetadataProvider provider)
    {
        var colon = name.IndexOf(':');
        if (colon > 0 && offsetInName <= colon)
            return null; // the xmlns PREFIX itself — resolved by the caller, which has the document

        var dot = name.IndexOf('.');
        if (dot > 0)
        {
            return offsetInName <= dot
                ? SymbolFromName(name[..dot], offsetInName, namespaces, provider)
                : MemberSymbol(name[..dot], name[(dot + 1)..], namespaces, provider);
        }

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

    /// <summary>An attribute name: directive, attached (dotted, caret side picks type vs member), or a member of the element's type.</summary>
    private static SymbolInfo? SymbolFromAttribute(string elementName, string attributeName, int offsetInName, Dictionary<string, string> namespaces, IXamlTypeMetadataProvider provider)
    {
        var colon = attributeName.IndexOf(':');
        if (colon > 0)
        {
            var prefix = attributeName[..colon];
            if (namespaces.TryGetValue(prefix, out var uri) && uri == IntrinsicsUri)
                return new SymbolInfo(attributeName, $"directive {attributeName}", null, null, [], "XAML intrinsics");
            attributeName = attributeName[(colon + 1)..]; // prefixed attached owner: fall through with the local name
            offsetInName -= colon + 1;
        }

        var dot = attributeName.IndexOf('.');
        if (dot > 0)
        {
            return offsetInName <= dot && offsetInName >= 0
                ? SymbolFromName(attributeName[..dot], offsetInName, namespaces, provider)
                : MemberSymbol(attributeName[..dot], attributeName[(dot + 1)..], namespaces, provider);
        }

        return MemberSymbol(elementName, attributeName, namespaces, provider);
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
    private static SymbolInfo? SymbolFromValue(
        string xaml,
        string? documentPath,
        string elementName,
        string attributeName,
        string value,
        int offsetInValue,
        int valueDocumentOffset,
        int enclosingTagOffset,
        Dictionary<string, string> namespaces,
        IXamlTypeMetadataProvider provider)
    {
        static bool PathChar(char c) => char.IsLetterOrDigit(c) || c is '.' or ':' or '_';

        if (attributeName == "Selector")
            return SelectorSymbol(xaml, documentPath, value, offsetInValue, enclosingTagOffset, namespaces, provider);

        // x:Class names the code-behind type: hover shows its signature, Ctrl+B jumps to the
        // class declaration (PDB type-level navigation — lands in the .xaml.cs half).
        var xClassColon = attributeName.IndexOf(':');
        if (xClassColon > 0 && attributeName[(xClassColon + 1)..] == "Class"
            && namespaces.GetValueOrDefault(attributeName[..xClassColon]) == IntrinsicsUri)
        {
            return ClassDirectiveSymbol(value.Trim());
        }

        // On an extension name itself ({Binding, {x:Static, {theme:Elevate)? Prefer the REALIZED
        // type when one exists — {Binding} forwards to the Binding class and its docs; only
        // loader-level intrinsics with no CLR realization get the generic blurb.
        foreach (Match extension in ExtensionNameToken().Matches(value))
        {
            var name = extension.Groups[1];
            if (offsetInValue >= name.Index && offsetInValue <= name.Index + name.Length)
            {
                var clr = (ResolveElement(name.Value, namespaces, provider) ?? ResolveElement(name.Value + "Extension", namespaces, provider))
                    ?.ClrType.UnderlyingSystemType;
                if (clr is not null)
                    return new SymbolInfo(
                        clr.Name, $"markup extension {clr.FullName}", clr, null, [$"T:{DocTypeName(clr)}"], clr.Assembly.GetName().Name);

                var canonicalName = Canonical(name.Value, namespaces);
                return canonicalName.StartsWith("x:", StringComparison.Ordinal) || canonicalName is "Binding" or "StaticResource" or "DynamicResource" or "TemplateBinding"
                    ? new SymbolInfo(canonicalName, $"markup extension {{{canonicalName}}}", null, null, [], "XAML intrinsics")
                    : null;
            }
        }

        // The token around the caret, wherever it lands.
        var start = offsetInValue;
        while (start > 0 && PathChar(value[start - 1]))
            start--;
        var end = offsetInValue;
        while (end < value.Length && PathChar(value[end]))
            end++;
        var token = value[start..end];

        // The innermost extension containing the caret.
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
        {
            // A plain value: enum members resolve to their fields (docs + definition); Type-typed
            // values (DataType=) resolve like element names, xmlns prefix included.
            var plainType = AttributeValueType(elementName, attributeName, namespaces, provider);
            var plainUnderlying = plainType is null ? null : Nullable.GetUnderlyingType(plainType) ?? plainType;
            if (token.Length == 0)
                return null;
            if (plainUnderlying == typeof(Type))
                return TypeNameOrPrefixSymbol(xaml, documentPath, token, offsetInValue - start, namespaces, provider);
            if (plainUnderlying == typeof(Uri))
                return UriTargetSymbol(value.Trim(), documentPath);
            return plainUnderlying is { IsEnum: true } ? EnumMemberSymbol(plainUnderlying, token) : null;
        }

        var body = value[(open + 1)..].TrimStart();
        var rawExtensionName = new string(body.TakeWhile(c => !char.IsWhiteSpace(c) && c != '}').ToArray());
        var canonical = Canonical(rawExtensionName, namespaces);

        if (canonical == "x:Static")
            return StaticPathSymbol(token, namespaces, provider);

        if (token.Length == 0)
            return null;

        // Parameter NAME (token followed by '=')?
        var after = end;
        while (after < value.Length && char.IsWhiteSpace(value[after]))
            after++;
        if (after < value.Length && value[after] == '=')
            return ExtensionParameterSymbol(rawExtensionName, token, namespaces, provider);

        // Parameter VALUE (token preceded by 'Name=')?
        string? parameterName = null;
        var before = start - 1;
        while (before >= 0 && char.IsWhiteSpace(value[before]))
            before--;
        if (before >= 0 && value[before] == '=')
        {
            var nameEnd = before - 1;
            while (nameEnd >= 0 && char.IsWhiteSpace(value[nameEnd]))
                nameEnd--;
            var nameStart = nameEnd;
            while (nameStart >= 0 && PathChar(value[nameStart]))
                nameStart--;
            parameterName = value[(nameStart + 1)..(nameEnd + 1)];
        }

        if (parameterName == "ElementName")
            return NamedElementSymbol(xaml, documentPath, token);
        if (parameterName == "AncestorType")
            return TypeNameOrPrefixSymbol(xaml, documentPath, token, offsetInValue - start, namespaces, provider);
        if (parameterName == "Path" && canonical is "Binding")
            return BindingPathSymbol(xaml, valueDocumentOffset + offsetInValue, body, elementName, token, namespaces, provider);
        if (parameterName is not null)
        {
            var extensionType = (ResolveElement(rawExtensionName, namespaces, provider) ?? ResolveElement(rawExtensionName + "Extension", namespaces, provider))
                ?.ClrType.UnderlyingSystemType;
            var memberType = extensionType?.GetProperty(parameterName, BindingFlags.Public | BindingFlags.Instance)?.PropertyType;
            var underlying = memberType is null ? null : Nullable.GetUnderlyingType(memberType) ?? memberType;
            return underlying is { IsEnum: true } ? EnumMemberSymbol(underlying, token) : null;
        }

        // Positional argument.
        return canonical switch
        {
            "StaticResource" or "DynamicResource" => ResourceKeySymbol(xaml, documentPath, token),
            "x:Reference" => NamedElementSymbol(xaml, documentPath, token),
            "x:Type" => TypeNameOrPrefixSymbol(xaml, documentPath, token, offsetInValue - start, namespaces, provider),
            "TemplateBinding" when TemplateTargetTypeName(xaml, valueDocumentOffset + offsetInValue) is { } templateTarget
                => MemberSymbol(templateTarget, token, namespaces, provider),
            "Binding" or "TemplateBinding" => BindingPathSymbol(xaml, valueDocumentOffset + offsetInValue, body, elementName, token, namespaces, provider),
            "RelativeSource" => RelativeSourceModeSymbol(token, namespaces, provider),
            _ => null,
        };
    }

    /// <summary>The code-behind type an <c>x:Class</c> value names, resolved across loaded assemblies.</summary>
    private static SymbolInfo? ClassDirectiveSymbol(string fullName)
    {
        if (fullName.Length == 0)
            return null;

        var clr = ResolveClrTypeName(fullName);
        if (clr is null)
            return null;

        var baseSuffix = clr.BaseType is { } baseType && baseType != typeof(object) ? $" : {baseType.Name}" : "";
        return new SymbolInfo(
            clr.Name,
            $"partial class {clr.FullName}{baseSuffix}",
            clr,
            null,
            [$"T:{DocTypeName(clr)}"],
            clr.Assembly.GetName().Name);
    }

    /// <summary>A full CLR type name resolved across every loaded assembly (user assemblies included).</summary>
    internal static Type? ResolveClrTypeName(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (assembly.GetType(fullName, throwOnError: false) is { } type)
                    return type;
            }
            catch (Exception ex) when (ex is NotSupportedException or System.IO.FileLoadException)
            {
            }
        }

        return null;
    }

    /// <summary>
    /// A Uri-typed value (<c>ResourceDictionary Source</c>, image sources): resolves the referenced
    /// document to a FILE next to (or above) the containing document and navigates there. Relative
    /// references probe the document's own directory; <c>cursorial://</c>/<c>embedded://</c>/plain
    /// paths probe each ancestor directory for the URI's path portion — the project-layout
    /// convention behind the embedded-resource scheme (Views/Res.xaml lives at ProjectRoot/Views/).
    /// </summary>
    private static SymbolInfo? UriTargetSymbol(string raw, string? documentPath)
    {
        if (raw.Length == 0 || documentPath is null)
            return null;

        var relative = raw;
        var schemeEnd = raw.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
        {
            var afterAuthority = raw.IndexOf('/', schemeEnd + 3);
            if (afterAuthority < 0)
                return null;
            relative = raw[(afterAuthority + 1)..];
        }

        if (relative.Length == 0)
            return null;

        var probe = relative.Replace('/', Path.DirectorySeparatorChar);
        for (var dir = Path.GetDirectoryName(documentPath); dir is { Length: > 0 }; dir = Path.GetDirectoryName(dir))
        {
            string candidate;
            try
            {
                candidate = Path.GetFullPath(Path.Combine(dir, probe));
            }
            catch (ArgumentException)
            {
                return null; // invalid path characters — not a navigable reference
            }

            if (File.Exists(candidate))
            {
                return new SymbolInfo(
                    Path.GetFileName(candidate),
                    $"resource {raw}",
                    null,
                    null,
                    [],
                    candidate,
                    (candidate, 1, 1));
            }
        }

        return null;
    }

    /// <summary>
    /// A type reference inside a value (<c>DataType="t:Foo"</c>, <c>{x:Type t:Foo}</c>,
    /// <c>AncestorType=t:Foo</c>): caret on the xmlns PREFIX resolves the prefix itself,
    /// anywhere else resolves the type.
    /// </summary>
    private static SymbolInfo? TypeNameOrPrefixSymbol(
        string xaml, string? documentPath, string token, int offsetInToken, Dictionary<string, string> namespaces, IXamlTypeMetadataProvider provider)
    {
        var colon = token.IndexOf(':');
        if (colon > 0 && offsetInToken <= colon)
            return XmlnsPrefixSymbol(xaml, documentPath, token[..colon], namespaces);
        return SymbolFromName(token, offsetInToken, namespaces, provider);
    }

    /// <summary>
    /// Completion inside a style selector, dispatched by the marker before the caret token:
    /// <c>:</c> offers pseudo-classes (interaction-backed + control-defined mappings + the
    /// <c>is()</c> operator), <c>#</c> offers the document's named elements, bare tokens offer
    /// element types. The CSS-reflex tax collector: nobody should have to remember that hover
    /// spells <c>:pointerover</c>.
    /// </summary>
    internal static List<CompletionItemInfo> SelectorCompletions(string prefix, string xaml, Dictionary<string, string> namespaces, IXamlTypeMetadataProvider provider)
    {
        var i = prefix.Length - 1;
        while (i >= 0 && (char.IsLetterOrDigit(prefix[i]) || prefix[i] is '_' or '-'))
            i--;
        var marker = i >= 0 ? prefix[i] : '\0';

        return marker switch
        {
            ':' => PseudoClassItems(namespaces, provider),
            '#' => NamedElementItems(xaml),
            '.' => StyleClassItems(xaml),
            '/' => [new CompletionItemInfo { Text = "template/", Kind = "value", Detail = "combinator" }],
            _ => SelectorTypeItems(namespaces, provider),
        };
    }

    private static List<CompletionItemInfo> PseudoClassItems(Dictionary<string, string> namespaces, IXamlTypeMetadataProvider provider)
    {
        // Control-defined mappings register in static constructors; make sure the known element
        // types have run theirs (provider caches — a one-time sweep per process).
        foreach (var (_, uri) in namespaces)
        {
            foreach (var name in provider.GetKnownTypeNames(uri))
                SafeResolve(provider, uri, name);
        }

        var items = new List<CompletionItemInfo>();
        foreach (var name in Cursorial.UI.InteractionPseudoClasses.Names)
        {
            Cursorial.UI.InteractionPseudoClasses.TryGetState(name, out var state);
            items.Add(new CompletionItemInfo { Text = name.TrimStart(':'), Kind = "value", Detail = $"InteractionState.{state}" });
        }

        foreach (var mapping in Cursorial.UI.PseudoClassMapping.Snapshot())
        {
            foreach (var pseudoClass in mapping.PseudoClasses)
            {
                items.Add(new CompletionItemInfo
                {
                    Text = pseudoClass.TrimStart(':'),
                    Kind = "value",
                    Detail = $"{mapping.OwnerType.Name}.{mapping.Property.Name}",
                });
            }
        }

        items.Add(new CompletionItemInfo { Text = "is", Kind = "value", Detail = "operator", Insert = "is(" });
        return items.DistinctBy(item => item.Text).ToList();
    }

    [GeneratedRegex("\\bClasses\\s*=\\s*\"([^\"]+)\"")]
    private static partial Regex ClassesAttribute();

    /// <summary>Style classes after '.': the framework's caps-* capability catalog + every class the document assigns.</summary>
    private static List<CompletionItemInfo> StyleClassItems(string xaml)
    {
        var items = new List<CompletionItemInfo>();
        foreach (var name in Cursorial.UI.CapabilityClasses.Names)
            items.Add(new CompletionItemInfo { Text = name, Kind = "value", Detail = "capability" });

        foreach (Match match in ClassesAttribute().Matches(xaml))
        {
            foreach (var name in match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!name.StartsWith("caps-", StringComparison.Ordinal) && !name.StartsWith(':'))
                    items.Add(new CompletionItemInfo { Text = name, Kind = "value", Detail = "document" });
            }
        }

        return items.DistinctBy(item => item.Text).ToList();
    }

    private static List<CompletionItemInfo> SelectorTypeItems(Dictionary<string, string> namespaces, IXamlTypeMetadataProvider provider)
    {
        var items = new List<CompletionItemInfo>();
        foreach (var (prefix, uri) in namespaces.OrderBy(n => n.Key, StringComparer.Ordinal))
        {
            foreach (var name in provider.GetKnownTypeNames(uri))
            {
                var clr = SafeResolve(provider, uri, name)?.ClrType.UnderlyingSystemType;
                if (clr is null || !typeof(Cursorial.UI.UIElement).IsAssignableFrom(clr))
                    continue;

                items.Add(new CompletionItemInfo
                {
                    Text = prefix.Length == 0 ? name : $"{prefix}:{name}",
                    Kind = "element",
                    Detail = clr.Namespace,
                });
            }
        }

        return items.DistinctBy(item => item.Text).ToList();
    }

    /// <summary>The symbol under the caret in a style selector: a type, a #named element, a .class, a :pseudo-class, or the ^ nesting anchor.</summary>
    private static SymbolInfo? SelectorSymbol(string xaml, string? documentPath, string value, int offset, int enclosingTagOffset, Dictionary<string, string> namespaces, IXamlTypeMetadataProvider provider)
    {
        static bool IdentChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-';

        // The ^ nesting anchor refers to the PARENT style's selector — jump there (deep style
        // nests make the referent genuinely far away).
        if ((offset < value.Length && value[offset] == '^') || (offset > 0 && value[offset - 1] == '^'))
            return ParentSelectorSymbol(xaml, documentPath, enclosingTagOffset);

        var start = offset;
        while (start > 0 && IdentChar(value[start - 1]))
            start--;
        var end = offset;
        while (end < value.Length && IdentChar(value[end]))
            end++;
        if (end <= start)
            return null;

        var token = value[start..end];
        var marker = start > 0 ? value[start - 1] : '\0';
        return marker switch
        {
            '#' => NamedElementSymbol(xaml, documentPath, token),
            '.' => new SymbolInfo(
                $".{token}",
                Cursorial.UI.CapabilityClasses.Names.Contains(token)
                    ? $"capability class .{token} (engine-stamped from the effective capability record)"
                    : $"style class .{token}",
                null, null, [], null),
            ':' => PseudoClassSymbol(token),
            _ => SymbolFromName(token, offset - start, namespaces, provider),
        };
    }

    /// <summary>A pseudo-class: interaction-backed names document via their InteractionState bit,
    /// mapping-backed ones via their owning property.</summary>
    private static SymbolInfo PseudoClassSymbol(string token)
    {
        var name = ":" + token;
        if (Cursorial.UI.InteractionPseudoClasses.TryGetState(name, out var state))
        {
            var enumType = typeof(Cursorial.UI.InteractionState);
            var field = enumType.GetField(state.ToString(), BindingFlags.Public | BindingFlags.Static);
            return new SymbolInfo(
                name,
                $"pseudo-class {name} — InteractionState.{state}",
                enumType,
                field,
                [$"F:{DocTypeName(enumType)}.{state}"],
                null);
        }

        foreach (var mapping in Cursorial.UI.PseudoClassMapping.Snapshot())
        {
            if (!mapping.PseudoClasses.Contains(name))
                continue;

            var property = mapping.OwnerType.GetProperty(mapping.Property.Name, BindingFlags.Public | BindingFlags.Instance);
            return new SymbolInfo(
                name,
                $"pseudo-class {name} — {mapping.OwnerType.Name}.{mapping.Property.Name}",
                mapping.OwnerType,
                property,
                [$"P:{DocTypeName(mapping.OwnerType)}.{mapping.Property.Name}", $"F:{DocTypeName(mapping.OwnerType)}.{mapping.Property.Name}Property"],
                null);
        }

        return new SymbolInfo(name, $"pseudo-class {name}", null, null, [], null);
    }

    /// <summary>The <c>{x:Static Owner.Member}</c> path symbol: static field (with value) or property.</summary>
    private static SymbolInfo? StaticPathSymbol(string path, Dictionary<string, string> namespaces, IXamlTypeMetadataProvider provider)
    {
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

    /// <summary>
    /// The nearest enclosing (unclosed) parent <c>&lt;Style&gt;</c> before <paramref name="beforeTag"/>:
    /// the referent of a nested selector's <c>^</c> anchor. Hover shows the parent's selector
    /// text; definition jumps to it in-document.
    /// </summary>
    private static SymbolInfo? ParentSelectorSymbol(string xaml, string? documentPath, int beforeTag)
    {
        var stack = new Stack<Match>();
        foreach (Match tag in TagToken().Matches(xaml))
        {
            if (tag.Index >= beforeTag)
                break;
            if (tag.Groups[2].Value != "Style")
                continue;

            var closing = tag.Groups[1].Value.Length > 0;
            var selfClosed = tag.Groups[4].Value.Length > 0;
            if (closing)
            {
                if (stack.Count > 0)
                    stack.Pop();
            }
            else if (!selfClosed)
            {
                stack.Push(tag);
            }
        }

        if (stack.Count == 0)
            return null;

        var parent = stack.Peek();
        var attributes = parent.Groups[3];
        foreach (Match attribute in AttributeToken().Matches(attributes.Value))
        {
            if (attribute.Groups[1].Value != "Selector")
                continue;

            var valueOffset = attributes.Index + attribute.Groups[2].Index;
            return new SymbolInfo(
                "^",
                $"parent selector \"{attribute.Groups[2].Value}\"",
                null,
                null,
                [],
                null,
                DocumentLocation(xaml, documentPath, valueOffset));
        }

        return new SymbolInfo(
            "^", "parent style", null, null, [], null,
            DocumentLocation(xaml, documentPath, parent.Groups[2].Index));
    }

    /// <summary>The shorthand {RelativeSource Self|TemplatedParent|FindAncestor} mode as an enum member.</summary>
    private static SymbolInfo? RelativeSourceModeSymbol(string token, Dictionary<string, string> namespaces, IXamlTypeMetadataProvider provider)
    {
        var modeType = (ResolveElement("RelativeSource", namespaces, provider) ?? ResolveElement("RelativeSourceExtension", namespaces, provider))
            ?.ClrType.UnderlyingSystemType
            ?.GetProperty("Mode", BindingFlags.Public | BindingFlags.Instance)?.PropertyType;
        var underlying = modeType is null ? null : Nullable.GetUnderlyingType(modeType) ?? modeType;
        return underlying is { IsEnum: true } ? EnumMemberSymbol(underlying, token) : null;
    }

    /// <summary>An enum member as a symbol: docs from its field, definition via the enum's declaration.</summary>
    private static SymbolInfo? EnumMemberSymbol(Type enumType, string token)
    {
        var field = enumType.GetFields(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(f => string.Equals(f.Name, token, StringComparison.OrdinalIgnoreCase));
        return field is null ? null : new SymbolInfo(
            $"{enumType.Name}.{field.Name}",
            $"enum {enumType.Name}.{field.Name}",
            enumType,
            field,
            [$"F:{DocTypeName(enumType)}.{field.Name}"],
            null);
    }

    /// <summary>A resource key: a <c>*Keys</c> constant when the value matches one, else a document x:Key.</summary>
    private static SymbolInfo? ResourceKeySymbol(string xaml, string? documentPath, string key)
    {
        foreach (var (type, fieldName, value) in KeyConstants())
        {
            if (value != key || type.GetField(fieldName, BindingFlags.Public | BindingFlags.Static) is not { } field)
                continue;

            return new SymbolInfo(
                $"{type.Name}.{fieldName}",
                $"const {field.FieldType.Name} {type.Name}.{fieldName}",
                type,
                field,
                [$"F:{DocTypeName(type)}.{fieldName}"],
                ValueFormatter.Format(value));
        }

        foreach (Match match in KeyAttribute().Matches(xaml))
        {
            if (match.Groups[1].Value != key)
                continue;

            return new SymbolInfo(
                key,
                $"resource key \"{key}\"",
                null,
                null,
                [],
                "declared in this document",
                DocumentLocation(xaml, documentPath, match.Groups[1].Index));
        }

        return null;
    }

    /// <summary>A named element (<c>x:Reference</c>/<c>ElementName</c> target); definition jumps in-document.</summary>
    private static SymbolInfo? NamedElementSymbol(string xaml, string? documentPath, string name)
    {
        foreach (Match match in NameAttribute().Matches(xaml))
        {
            if (match.Groups[1].Value != name)
                continue;

            return new SymbolInfo(
                name,
                $"named element \"{name}\"",
                null,
                null,
                [],
                null,
                DocumentLocation(xaml, documentPath, match.Groups[1].Index));
        }

        return null;
    }

    /// <summary>
    /// The binding's source type: <c>ElementName=</c> in the same extension wins (the named
    /// element's type); else the nearest ancestor <c>DataTemplate DataType</c> or
    /// <c>d:DataContext</c> (the root included — subsuming the old root-only behavior).
    /// </summary>
    internal static Type? BindingSourceType(string blanked, int caretOffset, string extensionBody, string hostElementName, Dictionary<string, string> namespaces, IXamlTypeMetadataProvider provider)
    {
        var elementName = Regex.Match(extensionBody, "\\bElementName\\s*=\\s*([A-Za-z_][\\w-]*)");
        if (elementName.Success)
            return NamedElementType(blanked, elementName.Groups[1].Value, namespaces, provider);

        // RelativeSource anchors: Self → the host element's own type; FindAncestor → the
        // declared AncestorType; TemplatedParent is statically unknowable (no completion).
        var relativeSource = Regex.Match(extensionBody, "\\bRelativeSource\\b[^}]*");
        if (relativeSource.Success)
        {
            var segment = relativeSource.Value;
            var ancestor = Regex.Match(segment, "\\bAncestorType\\s*=\\s*([A-Za-z_][\\w:.]*)");
            if (ancestor.Success)
                return ResolveElement(ancestor.Groups[1].Value, namespaces, provider)?.ClrType.UnderlyingSystemType;
            if (Regex.IsMatch(segment, "\\bSelf\\b"))
                return ResolveElement(hostElementName, namespaces, provider)?.ClrType.UnderlyingSystemType;
            if (Regex.IsMatch(segment, "\\bTemplatedParent\\b"))
                return null;
        }

        return AmbientDataContextType(blanked, caretOffset, namespaces, provider);
    }

    /// <summary>The xmlns prefix as a symbol: hover shows the URI; definition jumps to the declaration.</summary>
    private static SymbolInfo? XmlnsPrefixSymbol(string xaml, string? documentPath, string prefix, Dictionary<string, string> namespaces)
    {
        if (!namespaces.TryGetValue(prefix, out var uri))
            return null;

        var declaration = Regex.Match(xaml, "xmlns:" + Regex.Escape(prefix) + "\\s*=\\s*\"");
        return new SymbolInfo(
            $"xmlns:{prefix}",
            $"xmlns:{prefix} = \"{uri}\"",
            null,
            null,
            [],
            null,
            declaration.Success ? DocumentLocation(xaml, documentPath, declaration.Index) : null);
    }

    /// <summary>The CLR type of the element declaring <c>Name</c>/<c>x:Name</c> = <paramref name="name"/>.</summary>
    private static Type? NamedElementType(string xaml, string name, Dictionary<string, string> namespaces, IXamlTypeMetadataProvider provider)
    {
        foreach (Match match in NameAttribute().Matches(xaml))
        {
            if (match.Groups[1].Value != name)
                continue;

            foreach (Match tag in TagToken().Matches(xaml))
            {
                if (match.Index < tag.Index || match.Index > tag.Index + tag.Length)
                    continue;
                return ResolveElement(tag.Groups[2].Value, namespaces, provider)?.ClrType.UnderlyingSystemType;
            }

            break;
        }

        return null;
    }

    /// <summary>
    /// The ambient data-context type at a position: the nearest enclosing tag carrying
    /// <c>DataTemplate DataType="…"</c> or <c>d:DataContext="…"</c>, innermost first.
    /// </summary>
    /// <summary>
    /// The TargetType NAME of the nearest enclosing <c>ControlTemplate</c> (falling back to the
    /// nearest enclosing <c>Style</c>) — what a <c>{TemplateBinding}</c> binds against.
    /// </summary>
    internal static string? TemplateTargetTypeName(string blanked, int offset)
    {
        var stack = new Stack<Match>();
        foreach (Match tag in TagToken().Matches(blanked))
        {
            if (tag.Index >= offset)
                break;

            var closing = tag.Groups[1].Value.Length > 0;
            var selfClosed = tag.Groups[4].Value.Length > 0;
            if (closing)
            {
                if (stack.Count > 0)
                    stack.Pop();
            }
            else if (!selfClosed)
            {
                stack.Push(tag);
            }
        }

        string? styleFallback = null;
        foreach (var tag in stack) // innermost first
        {
            var name = tag.Groups[2].Value;
            if (name is not ("ControlTemplate" or "Style"))
                continue;

            var target = Regex.Match(tag.Groups[3].Value, "\\bTargetType\\s*=\\s*\\\"([^\\\"]+)\\\"");
            if (!target.Success)
                continue;
            if (name == "ControlTemplate")
                return target.Groups[1].Value;
            styleFallback ??= target.Groups[1].Value;
        }

        return styleFallback;
    }

    /// <summary>
    /// The type a <c>Setter</c> at <paramref name="offset"/> configures: the nearest enclosing
    /// <c>Style</c>'s <c>TargetType</c>, falling back to its <c>Selector</c>'s leading type token.
    /// </summary>
    internal static string? SetterTargetTypeName(string blanked, int offset)
    {
        var stack = new Stack<Match>();
        foreach (Match tag in TagToken().Matches(blanked))
        {
            if (tag.Index >= offset)
                break;

            var closing = tag.Groups[1].Value.Length > 0;
            var selfClosed = tag.Groups[4].Value.Length > 0;
            if (closing)
            {
                if (stack.Count > 0)
                    stack.Pop();
            }
            else if (!selfClosed)
            {
                stack.Push(tag);
            }
        }

        foreach (var tag in stack) // innermost first — the nearest Style owns the Setter
        {
            if (tag.Groups[2].Value is not ("Style" or "ControlTemplate"))
                continue;

            var attrs = tag.Groups[3].Value;
            var target = Regex.Match(attrs, "\\bTargetType\\s*=\\s*\\\"([^\\\"]+)\\\"");
            if (target.Success)
                return target.Groups[1].Value;

            var selector = Regex.Match(attrs, "\\bSelector\\s*=\\s*\\\"\\s*\\^?\\s*([A-Za-z_]\\w*)");
            if (selector.Success)
                return selector.Groups[1].Value;
        }

        return null;
    }

    /// <summary>
    /// The raw value of an attribute on the tag containing <paramref name="offset"/>, or null.
    /// Works on the UNTERMINATED tag being typed (mid-edit has no closing <c>&gt;</c> yet):
    /// scans from the nearest <c>&lt;</c> to the first unquoted close or end of text.
    /// </summary>
    internal static string? ContainingTagAttribute(string blanked, int offset, string attributeName)
    {
        var open = blanked.LastIndexOf('<', Math.Clamp(offset - 1, 0, Math.Max(0, blanked.Length - 1)));
        if (open < 0)
            return null;

        var end = blanked.Length;
        var quoted = false;
        for (var i = open + 1; i < blanked.Length; i++)
        {
            if (blanked[i] == '"')
            {
                quoted = !quoted;
            }
            else if (blanked[i] == '>' && !quoted)
            {
                end = i;
                break;
            }
        }

        var match = Regex.Match(blanked[open..end], "\\b" + Regex.Escape(attributeName) + "\\s*=\\s*\\\"([^\\\"]*)\\\"");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static Type? AmbientDataContextType(string blanked, int offset, Dictionary<string, string> namespaces, IXamlTypeMetadataProvider provider)
    {
        var designPrefixes = namespaces.Where(n => DesignUris.Contains(n.Value)).Select(n => n.Key).ToList();

        Type? FromTag(Match tag)
        {
            var attributes = tag.Groups[3].Value;
            if (tag.Groups[2].Value == "DataTemplate"
                && Regex.Match(attributes, "\\bDataType\\s*=\\s*\"([^\"]+)\"") is { Success: true } dataType)
            {
                return ResolveElement(dataType.Groups[1].Value, namespaces, provider)?.ClrType.UnderlyingSystemType;
            }

            foreach (var prefix in designPrefixes)
            {
                var match = Regex.Match(attributes, Regex.Escape(prefix) + ":DataContext\\s*=\\s*\"([^\"]+)\"");
                if (match.Success)
                    return ResolveElement(match.Groups[1].Value, namespaces, provider)?.ClrType.UnderlyingSystemType;
            }

            return null;
        }

        Match? containing = null;
        var stack = new Stack<Match>();
        foreach (Match tag in TagToken().Matches(blanked))
        {
            if (tag.Index >= offset)
                break;
            if (offset <= tag.Index + tag.Length)
                containing = tag; // the (possibly self-closed) tag whose attributes hold the caret

            var closing = tag.Groups[1].Value.Length > 0;
            var selfClosed = tag.Groups[4].Value.Length > 0;
            if (closing)
            {
                if (stack.Count > 0)
                    stack.Pop();
            }
            else if (!selfClosed)
            {
                stack.Push(tag);
            }
        }

        if (containing is not null && FromTag(containing) is { } own)
            return own;

        foreach (var tag in stack) // innermost first
        {
            if (FromTag(tag) is { } found)
                return found;
        }

        return null;
    }

    /// <summary>A binding path resolved against the inferred binding source (ElementName / DataTemplate DataType / d:DataContext).</summary>
    private static SymbolInfo? BindingPathSymbol(string xaml, int caretOffset, string extensionBody, string hostElementName, string path, Dictionary<string, string> namespaces, IXamlTypeMetadataProvider provider)
    {
        var current = BindingSourceType(xaml, caretOffset, extensionBody, hostElementName, namespaces, provider);
        if (current is null || path.Length == 0)
            return null;

        PropertyInfo? property = null;
        foreach (var segment in path.Split('.'))
        {
            property = current?.GetProperty(segment, BindingFlags.Public | BindingFlags.Instance);
            if (property is null)
                return null;
            current = property.PropertyType;
        }

        var declaring = property!.DeclaringType ?? property.ReflectedType!;
        return new SymbolInfo(
            $"{declaring.Name}.{property.Name}",
            $"{property.PropertyType.Name} {declaring.Name}.{property.Name}",
            declaring,
            property,
            [$"P:{DocTypeName(declaring)}.{property.Name}"],
            null);
    }

    /// <summary>A named parameter of a markup extension, resolved as a property of its realized type.</summary>
    private static SymbolInfo? ExtensionParameterSymbol(string extensionName, string parameter, Dictionary<string, string> namespaces, IXamlTypeMetadataProvider provider)
    {
        var clr = (ResolveElement(extensionName, namespaces, provider) ?? ResolveElement(extensionName + "Extension", namespaces, provider))
            ?.ClrType.UnderlyingSystemType;
        var property = clr?.GetProperty(parameter, BindingFlags.Public | BindingFlags.Instance);
        return clr is null || property is null ? null : new SymbolInfo(
            $"{clr.Name}.{parameter}",
            $"{property.PropertyType.Name} {clr.Name}.{parameter}",
            clr,
            property,
            [$"P:{DocTypeName(clr)}.{parameter}"],
            null);
    }

    private static (string File, int Line, int Column)? DocumentLocation(string xaml, string? documentPath, int offset)
    {
        if (documentPath is null)
            return null;
        var (line, column) = PositionAt(LineStarts(xaml), offset);
        return (documentPath, line, column);
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

                // Partial classes split across a hand-written half and a GENERATED half (x:Class
                // code-behinds); the generated file often carries more member bodies, but the
                // hand-written one is where a human means to go. Prefer non-generated documents,
                // then the one most members live in.
                var bestFile = hits
                    .GroupBy(h => h.File, StringComparer.Ordinal)
                    .OrderBy(g => g.Key.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
                                  || g.Key.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(g => g.Count())
                    .First().Key;
                var earliest = hits.Where(h => h.File == bestFile).MinBy(h => h.Line);

                // Types have no sequence points of their own, so the earliest member body is the
                // best the PDB offers — but landing on a member reads as a mis-jump. The file is
                // local (callers verify); find the declaration line itself.
                return TypeDeclarationLine(bestFile, type.Name) ?? earliest;
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The 1-based position of <c>class/struct/… {name}</c> in <paramref name="path"/>, when present.</summary>
    private static (string File, int Line, int Column)? TypeDeclarationLine(string path, string name)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            var declaration = new Regex($@"\b(?:class|struct|interface|enum|record)\s+{Regex.Escape(name)}\b");
            var lineNumber = 0;
            foreach (var line in File.ReadLines(path))
            {
                lineNumber++;
                if (declaration.Match(line) is { Success: true } match)
                    return (path, lineNumber, match.Value.LastIndexOf(name, StringComparison.Ordinal) + match.Index + 1);
            }
        }
        catch
        {
        }

        return null;
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
