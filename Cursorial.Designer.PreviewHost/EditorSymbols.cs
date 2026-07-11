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
                Add(start, dot, "element");
            Add(start + dot, 1, "dot");
            if (memberName.Length > 0)
                Add(start + dot + 1, memberName.Length, DottedMemberKind(ownerName, memberName));
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
                            Add(contentStart + valueStart, value.Length, kind);
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
                        Add(absStart, lastDot, "element");
                        Add(absStart + lastDot, 1, "dot");
                        if (lastDot + 1 < token.Length)
                            Add(absStart + lastDot + 1, token.Length - lastDot - 1, "staticMember");
                    }
                    else
                    {
                        Add(absStart, token.Length, "element");
                    }

                    break;

                case "x:Type":
                    Add(absStart, token.Length, "element");
                    break;

                case "x:Reference":
                    Add(absStart, token.Length, "elementRef");
                    break;

                case "Binding" or "TemplateBinding" when index == 0:
                    Add(absStart, token.Length, "bindingPath");
                    break;
            }
        }

        string? ParameterValueKind(string extensionName, string parameter, string value)
        {
            if (parameter == "ElementName")
                return "elementRef";
            if (extensionName == "Binding" && parameter == "Path")
                return "bindingPath";

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
                Add(nameGroup.Index, nameGroup.Length, "element");

            var attributes = tag.Groups[3];
            foreach (Match attribute in AttributeToken().Matches(attributes.Value))
            {
                var attrName = attribute.Groups[1];
                var colon = attrName.Value.IndexOf(':');
                if (colon > 0 && intrinsicsPrefix is { Length: > 0 } && attrName.Value[..colon] == intrinsicsPrefix)
                    Add(attributes.Index + attrName.Index, attrName.Length, "directive");
                else
                    AddName(attributes.Index + attrName.Index, attrName.Value);

                var value = attribute.Groups[2];
                if (value.Value.Contains('{'))
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
                return SymbolFromName(nameGroup.Value, offset - nameGroup.Index, namespaces, provider);

            var attributes = tag.Groups[3];
            foreach (Match attribute in AttributeToken().Matches(attributes.Value))
            {
                var attrName = attribute.Groups[1];
                var nameStart = attributes.Index + attrName.Index;
                if (offset >= nameStart && offset <= nameStart + attrName.Length)
                    return SymbolFromAttribute(nameGroup.Value, attrName.Value, offset - nameStart, namespaces, provider);

                var value = attribute.Groups[2];
                var valueStart = attributes.Index + value.Index;
                if (offset >= valueStart && offset <= valueStart + value.Length)
                    return SymbolFromValue(blanked, documentPath, nameGroup.Value, attrName.Value, value.Value, offset - valueStart, namespaces, provider);
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
        Dictionary<string, string> namespaces,
        IXamlTypeMetadataProvider provider)
    {
        static bool PathChar(char c) => char.IsLetterOrDigit(c) || c is '.' or ':' or '_';

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
            // A plain value: enum members resolve to their fields (docs + definition).
            var plainType = AttributeValueType(elementName, attributeName, namespaces, provider);
            var plainEnum = plainType is null ? null : Nullable.GetUnderlyingType(plainType) ?? plainType;
            return plainEnum is { IsEnum: true } && token.Length > 0 ? EnumMemberSymbol(plainEnum, token) : null;
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
        if (parameterName == "Path" && canonical is "Binding")
            return BindingPathSymbol(xaml, token, namespaces, provider);
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
            "Binding" or "TemplateBinding" => BindingPathSymbol(xaml, token, namespaces, provider),
            _ => null,
        };
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

    /// <summary>A binding path resolved against the document's d:DataContext design type.</summary>
    private static SymbolInfo? BindingPathSymbol(string xaml, string path, Dictionary<string, string> namespaces, IXamlTypeMetadataProvider provider)
    {
        var current = DesignDataContextType(xaml, namespaces, provider);
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

                var bestFile = hits.GroupBy(h => h.File, StringComparer.Ordinal).MaxBy(g => g.Count())!.Key;
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
