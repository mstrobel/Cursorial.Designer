using System.Reflection;
using System.Text.RegularExpressions;

using Cursorial.Designer.Protocol;
using Cursorial.UI;
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
        xaml = BlankNonMarkup(xaml);

        // Find the first real element tag (the XML declaration and comments are blanked).
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

    [GeneratedRegex("<!--.*?(?:-->|$)|<!\\[CDATA\\[.*?(?:]]>|$)|<\\?.*?(?:\\?>|$)", RegexOptions.Singleline)]
    private static partial Regex NonMarkupRegion();

    /// <summary>
    /// Comments, CDATA sections, and processing instructions replaced by spaces — offsets AND
    /// line structure preserved (newlines survive; blanking a multi-line comment must not shift
    /// every subsequent line number for consumers that compute positions over the blanked text,
    /// e.g. in-document definition targets). The textual scanners never read markup out of
    /// non-markup regions — a commented-out tag must not become the "parent element" and a
    /// comment above the root must not become the "root tag". Unterminated regions (routine
    /// mid-edit) blank to end of text, which also makes a caret inside one detect as no
    /// completion context.
    /// </summary>
    internal static string BlankNonMarkup(string xaml)
        => NonMarkupRegion().Replace(xaml, static m =>
            string.Create(m.Length, m.Value, static (span, value) =>
            {
                for (var i = 0; i < span.Length; i++)
                    span[i] = value[i] is '\n' or '\r' ? value[i] : ' ';
            }));

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
        string Prefix,
        string? ParentElement = null);

    [GeneratedRegex("<(/?)([A-Za-z_][\\w.:-]*)((?:[^<>\"]|\"[^\"]*\")*?)(/?)>", RegexOptions.Singleline)]
    private static partial Regex TagToken();

    /// <summary>
    /// The innermost still-open element before <paramref name="beforeOffset"/> — a tolerant
    /// tag-stack walk over possibly-malformed mid-edit text.
    /// </summary>
    internal static string? FindParentElement(string xaml, int beforeOffset)
    {
        var stack = new Stack<string>();
        foreach (Match match in TagToken().Matches(BlankNonMarkup(xaml[..Math.Clamp(beforeOffset, 0, xaml.Length)])))
        {
            var closing = match.Groups[1].Value.Length > 0;
            var selfClosed = match.Groups[4].Value.Length > 0;
            if (closing)
            {
                if (stack.Count > 0)
                    stack.Pop();
            }
            else if (!selfClosed)
            {
                stack.Push(match.Groups[2].Value);
            }
        }

        return stack.Count > 0 ? stack.Peek() : null;
    }

    /// <summary>
    /// Detects what the caret is completing: an element name (right after <c>&lt;</c>), an
    /// attribute name (inside a tag, outside quotes), or an attribute value (inside quotes).
    /// </summary>
    internal static CompletionContext Detect(string xaml, int offset)
    {
        offset = Math.Clamp(offset, 0, xaml.Length);
        xaml = BlankNonMarkup(xaml);
        var open = xaml.LastIndexOf('<', Math.Max(0, offset - 1));
        if (open < 0 || open >= offset)
            return new CompletionContext(ContextKind.None, "", "", ""); // no tag, or caret at/before '<'

        // Between tags? Scan forward from the open honoring quotes — a raw '>' inside an
        // attribute value (legal XML) is not the tag's close.
        var quoted = false;
        for (var i = open + 1; i < offset; i++)
        {
            if (xaml[i] == '"')
                quoted = !quoted;
            else if (xaml[i] == '>' && !quoted)
                return new CompletionContext(ContextKind.None, "", "", ""); // between tags
        }

        var segment = xaml[(open + 1)..offset];
        if (segment.StartsWith('/'))
            return new CompletionContext(ContextKind.None, "", "", ""); // closing tag

        // No whitespace yet → still typing the element name itself.
        if (!segment.Any(char.IsWhiteSpace))
            return new CompletionContext(ContextKind.ElementName, "", "", segment, FindParentElement(xaml, open));

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

        return new CompletionContext(ContextKind.AttributeName, elementName, "", prefix, FindParentElement(xaml, open));
    }

    /// <summary>Computes completion items for a 1-based (line, column) position.</summary>
    internal static List<CompletionItemInfo> Complete(string xaml, int line, int column)
    {
        var offset = OffsetOf(xaml, line, column);
        xaml = BlankNonMarkup(xaml); // document sweeps (x:Key, Name, d:DataContext) skip comments too
        var context = Detect(xaml, offset);
        var namespaces = ScanNamespaces(xaml);
        var provider = XamlLoaderOptions.DefaultMetadataProvider;
        var items = new List<CompletionItemInfo>();

        switch (context.Kind)
        {
            case ContextKind.ElementName:
            {
                // Property-element position: "<Button." offers Button's members as property
                // elements; "<Grid." inside another parent offers Grid's ATTACHED properties.
                var elementDot = context.Prefix.IndexOf('.');
                if (elementDot > 0)
                {
                    var ownerName = context.Prefix[..elementDot];
                    var owner = ResolveElement(ownerName, namespaces, provider);
                    if (owner is not null)
                    {
                        var ownerIsParent = string.Equals(ownerName, context.ParentElement, StringComparison.Ordinal);
                        if (ownerIsParent)
                        {
                            foreach (var member in provider.GetKnownMemberNames(owner.ClrType))
                                items.Add(new CompletionItemInfo { Text = $"{ownerName}.{member}", Kind = "element", Detail = "property element" });
                        }

                        var ownerClr = owner.ClrType.UnderlyingSystemType;
                        if (ownerClr is not null)
                        {
                            foreach (var attached in AttachedPropertyNames(ownerClr))
                                items.Add(new CompletionItemInfo { Text = $"{ownerName}.{attached}", Kind = "element", Detail = "attached" });
                        }
                    }

                    break;
                }

                // Contextual filtering: only instantiable types (statics/interfaces/abstracts
                // have no activation path), narrowed to what the insertion point accepts — a
                // panel's children take UIElements; a collection-typed property element accepts
                // EITHER an instance of the property type (replace) OR its items (add).
                var targets = ResolveInsertionTargets(context.ParentElement, namespaces, provider);

                foreach (var (prefix, uri) in namespaces.OrderBy(n => n.Key, StringComparer.Ordinal))
                {
                    var clrNamespace = provider.GetClrNamespaces(uri).FirstOrDefault();
                    foreach (var name in provider.GetKnownTypeNames(uri))
                    {
                        var candidate = SafeResolve(provider, uri, name);
                        var clr = candidate?.ClrType.UnderlyingSystemType;
                        if (candidate is null || !IsInstantiable(candidate, clr))
                            continue;
                        if (targets is not null && clr is not null && !targets.Any(t => t.IsAssignableFrom(clr)))
                            continue;

                        items.Add(new CompletionItemInfo
                        {
                            Text = prefix.Length == 0 ? name : $"{prefix}:{name}",
                            Kind = "element",
                            Detail = clrNamespace,
                        });
                    }
                }

                // The parent's COLLECTION-typed members as property elements: the ContentProperty
                // narrowing above hides them (Style's content is Setters, so <Style.Styles>
                // never surfaced), yet collections can ONLY be populated in element form.
                // Scalar members stay behind the explicit "<Parent." gesture — offering every
                // property here would be noise.
                if (context.ParentElement is { } parentName2 && !parentName2.Contains('.')
                    && ResolveElement(parentName2, namespaces, provider) is { } parentType)
                {
                    foreach (var member in provider.GetKnownMemberNames(parentType.ClrType))
                    {
                        var memberType = parentType.TryGetMember(member)?.ValueType.UnderlyingSystemType;
                        if (memberType is null || memberType == typeof(string) || ItemTypeOf(memberType) is null)
                            continue;

                        items.Add(new CompletionItemInfo
                        {
                            Text = $"{parentName2}.{member}",
                            Kind = "element",
                            Detail = "property element",
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

                // Attached properties. Explicit owner ("Grid.Ro") completes that owner's attached
                // set — owners may be STATIC classes, so resolution never goes through the
                // instantiability filter. Without a dot, the enclosing parent's attached
                // properties are offered (inside a Grid, a child naturally wants Grid.Row).
                var attachedDot = context.Prefix.IndexOf('.');
                if (attachedDot > 0)
                {
                    var ownerName = context.Prefix[..attachedDot];
                    var owner = ResolveElement(ownerName, namespaces, provider)?.ClrType.UnderlyingSystemType;
                    if (owner is not null)
                    {
                        foreach (var attached in AttachedPropertyNames(owner))
                            items.Add(new CompletionItemInfo { Text = $"{ownerName}.{attached}", Kind = "attribute", Detail = "attached" });
                    }
                }
                else if (context.ParentElement is { } parentName && !parentName.Contains('.'))
                {
                    var parent = ResolveElement(parentName, namespaces, provider)?.ClrType.UnderlyingSystemType;
                    if (parent is not null)
                    {
                        foreach (var attached in AttachedPropertyNames(parent))
                            items.Add(new CompletionItemInfo { Text = $"{parentName}.{attached}", Kind = "attribute", Detail = "attached" });
                    }
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
                if (context.AttributeName == "Selector")
                {
                    items.AddRange(SelectorCompletions(context.Prefix, xaml, namespaces, provider));
                    break;
                }

                var extensionItems = CompleteExtension(context.Prefix, xaml, offset, context.ElementName, namespaces, provider);
                if (extensionItems is not null)
                {
                    items.AddRange(extensionItems);
                    break;
                }

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

    private static bool IsInstantiable(XamlType candidate, Type? clr)
    {
        if (candidate.Activate is not null)
            return true;

        // Construction-immutable types (Setter-style) have no parameterless activation but are
        // legitimate XAML elements when they fit the target — keep anything concretely
        // constructible and let assignability narrow it.
        return clr is { IsAbstract: false, IsInterface: false } && clr.GetConstructors().Length > 0;
    }

    /// <summary>
    /// What the insertion point accepts: the property (or content property) type itself — a
    /// replacing instance — plus, for collection/dictionary types, their item type — an added
    /// element. Null = no constraint: any object-typed side means anything goes (a resources
    /// dictionary holds brushes, styles, templates, …), so we never over-filter.
    /// </summary>
    private static IReadOnlyList<Type>? ResolveInsertionTargets(string? parentElement, Dictionary<string, string> namespaces, IXamlTypeMetadataProvider provider)
    {
        if (parentElement is null)
            return null;

        XamlMember? member;
        var dot = parentElement.IndexOf('.');
        if (dot > 0)
        {
            var owner = ResolveElement(parentElement[..dot], namespaces, provider);
            member = owner?.TryGetMember(parentElement[(dot + 1)..]);
        }
        else
        {
            var parent = ResolveElement(parentElement, namespaces, provider);
            member = parent?.ContentProperty is { } content ? parent.TryGetMember(content) : null;
        }

        var clr = member?.ValueType.UnderlyingSystemType;
        if (clr is null)
            return null;

        var targets = new List<Type> { clr };
        if (ItemTypeOf(clr) is { } item)
            targets.Add(item);

        return targets.Any(t => t == typeof(object)) ? null : targets;
    }

    /// <summary>The item (or dictionary value) type of a collection-ish type; null for non-collections.</summary>
    private static Type? ItemTypeOf(Type type)
    {
        foreach (var contract in type.GetInterfaces())
        {
            if (!contract.IsGenericType)
                continue;
            var definition = contract.GetGenericTypeDefinition();
            if (definition == typeof(ICollection<>) || definition == typeof(IList<>))
                return contract.GetGenericArguments()[0];
            if (definition == typeof(IDictionary<,>))
                return contract.GetGenericArguments()[1];

            // Dictionary-shaped enumerables (e.g. ResourceDictionary : IEnumerable<KVP<object, object?>>).
            if (definition == typeof(IEnumerable<>) &&
                contract.GetGenericArguments()[0] is { IsGenericType: true } pair &&
                pair.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
                return pair.GetGenericArguments()[1];
        }

        // Fallback: the XAML collection contracts — Add(item) or Add(key, value).
        var add = type.GetMethods().FirstOrDefault(m => m.Name == "Add" && m.GetParameters().Length is 1 or 2);
        return add?.GetParameters()[^1].ParameterType;
    }

    /// <summary>
    /// Attached-property names declared by <paramref name="owner"/>. The registry
    /// (<see cref="UIProperties.AttachedBy"/>) knows every registered attached property
    /// regardless of field naming; the <c>public static readonly AttachedProperty&lt;T&gt;
    /// NameProperty</c> field convention additionally covers owners whose static constructor
    /// hasn't run yet (reading field names doesn't trigger it). Union of both.
    /// </summary>
    private static IEnumerable<string> AttachedPropertyNames(Type owner)
        => UIProperties.AttachedBy(owner).Select(p => p.Name)
            .Concat(owner.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f.Name.EndsWith("Property", StringComparison.Ordinal)
                            && f.FieldType.IsGenericType
                            && f.FieldType.GetGenericTypeDefinition() == typeof(AttachedProperty<>))
                .Select(f => f.Name[..^"Property".Length]))
            .Distinct(StringComparer.Ordinal);

    private static XamlType? ResolveElement(string elementName, Dictionary<string, string> namespaces, IXamlTypeMetadataProvider provider)
    {
        var colon = elementName.IndexOf(':');
        var prefix = colon > 0 ? elementName[..colon] : string.Empty;
        var localName = colon > 0 ? elementName[(colon + 1)..] : elementName;
        if (localName.Contains('.'))
            return null; // property elements aren't completable targets here

        return namespaces.TryGetValue(prefix, out var uri) ? SafeResolve(provider, uri, localName) : null;
    }

    /// <summary>
    /// <see cref="IXamlTypeMetadataProvider.TryGetType"/> that swallows resolution failures:
    /// resolving a candidate runs its static constructors, and one user cctor that throws in the
    /// headless host (no terminal, no application state) must not abort the whole request —
    /// it would kill completion for the entire namespace, permanently (failed cctors rethrow).
    /// </summary>
    private static XamlType? SafeResolve(IXamlTypeMetadataProvider provider, string uri, string name)
    {
        try
        {
            return provider.TryGetType(uri, name).Type;
        }
        catch
        {
            return null;
        }
    }

    private static Type? ResolveAttributeValueType(in CompletionContext context, Dictionary<string, string> namespaces, IXamlTypeMetadataProvider provider)
        => AttributeValueType(context.ElementName, context.AttributeName, namespaces, provider);

    /// <summary>The CLR type an attribute's value converts to, or null when unresolvable.</summary>
    internal static Type? AttributeValueType(string elementName, string attribute, Dictionary<string, string> namespaces, IXamlTypeMetadataProvider provider)
    {
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
            owner = elementName;
            member = attribute;
        }

        var type = ResolveElement(owner, namespaces, provider);
        if (type?.TryGetMember(member)?.ValueType.UnderlyingSystemType is { } fromMember)
            return fromMember;

        // Attached properties have no instance member; their value type is the
        // AttachedProperty<T> field's generic argument.
        var field = type?.ClrType.UnderlyingSystemType?.GetField(member + "Property", BindingFlags.Public | BindingFlags.Static);
        return field?.FieldType is { IsGenericType: true } fieldType && fieldType.GetGenericTypeDefinition() == typeof(AttachedProperty<>)
            ? fieldType.GetGenericArguments()[0]
            : null;
    }

    // ── Markup-extension completion ────────────────────────────────────────────────────────────

    private const string IntrinsicsUri = "https://cursorial.dev/xaml";
    private static readonly string[] DesignUris =
    [
        "http://schemas.microsoft.com/expression/blend/2008",
        "https://cursorial.dev/xaml/design",
    ];

    /// <summary>
    /// Completion inside a markup extension, or null when the value carries no unclosed
    /// <c>{</c> (plain-value completion applies). Handles nesting by anchoring on the
    /// INNERMOST open brace: <c>{DynamicResource {x:Static Th</c> completes the x:Static.
    /// </summary>
    private static List<CompletionItemInfo>? CompleteExtension(
        string valuePrefix, string xaml, int caretOffset, string hostElementName, Dictionary<string, string> namespaces, IXamlTypeMetadataProvider provider)
    {
        var open = -1;
        var depth = 0;
        for (var i = valuePrefix.Length - 1; i >= 0; i--)
        {
            if (valuePrefix[i] == '}')
                depth++;
            else if (valuePrefix[i] == '{')
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

        var body = valuePrefix[(open + 1)..].TrimStart();
        if (body.StartsWith('}'))
            return null; // "{}" escape: the rest is a literal

        // Still typing the extension name itself.
        if (!body.Any(char.IsWhiteSpace))
            return CompleteExtensionNames(namespaces, provider);

        var name = Canonical(new string(body.TakeWhile(c => !char.IsWhiteSpace(c)).ToArray()), namespaces);
        var rest = body[(body.IndexOf(' ') is var space && space >= 0 ? space : body.Length)..];
        var argument = rest[(rest.LastIndexOf(',') + 1)..].TrimStart();

        var equals = argument.IndexOf('=');
        return equals >= 0
            ? CompleteExtensionNamedValue(name, argument[..equals].Trim(), argument[(equals + 1)..].TrimStart(), xaml, caretOffset, body, hostElementName, namespaces, provider)
            : CompleteExtensionArgument(name, argument, xaml, caretOffset, body, hostElementName, namespaces, provider);
    }

    /// <summary>Folds a prefixed intrinsic (<c>x:Static</c> under any prefix) to its canonical form.</summary>
    private static string Canonical(string extensionName, Dictionary<string, string> namespaces)
    {
        var colon = extensionName.IndexOf(':');
        if (colon <= 0)
            return extensionName;
        var prefix = extensionName[..colon];
        return namespaces.TryGetValue(prefix, out var uri) && uri == IntrinsicsUri
            ? "x:" + extensionName[(colon + 1)..]
            : extensionName;
    }

    private static List<CompletionItemInfo> CompleteExtensionNames(
        Dictionary<string, string> namespaces, IXamlTypeMetadataProvider provider)
    {
        var items = new List<CompletionItemInfo>();

        var intrinsicsPrefix = namespaces.FirstOrDefault(n => n.Value == IntrinsicsUri).Key;
        foreach (var intrinsic in new[] { "Binding", "StaticResource", "DynamicResource", "TemplateBinding" })
            items.Add(new CompletionItemInfo { Text = intrinsic, Kind = "value", Detail = "markup extension" });
        if (intrinsicsPrefix is { Length: > 0 })
        {
            foreach (var intrinsic in new[] { "Static", "Type", "Null", "Reference" })
                items.Add(new CompletionItemInfo { Text = $"{intrinsicsPrefix}:{intrinsic}", Kind = "value", Detail = "markup extension" });
        }

        // Custom extensions: MarkupExtension-derived types, Extension suffix stripped.
        foreach (var (prefix, uri) in namespaces)
        {
            foreach (var name in provider.GetKnownTypeNames(uri))
            {
                var clr = SafeResolve(provider, uri, name)?.ClrType.UnderlyingSystemType;
                if (clr is null || !typeof(MarkupExtension).IsAssignableFrom(clr))
                    continue;
                var display = name.EndsWith("Extension", StringComparison.Ordinal) ? name[..^"Extension".Length] : name;
                items.Add(new CompletionItemInfo
                {
                    Text = prefix.Length == 0 ? display : $"{prefix}:{display}",
                    Kind = "value",
                    Detail = "markup extension",
                });
            }
        }

        return items.DistinctBy(i => i.Text).ToList();
    }

    private static List<CompletionItemInfo> CompleteExtensionArgument(
        string extension, string argument, string xaml, int caretOffset, string body, string hostElementName, Dictionary<string, string> namespaces, IXamlTypeMetadataProvider provider)
    {
        switch (extension)
        {
            case "StaticResource" or "DynamicResource":
                return ResourceKeyItems(xaml, namespaces, provider);

            case "x:Static":
                return StaticPathItems(argument, namespaces, provider);

            case "x:Type":
                return provider.GetKnownTypeNames(namespaces.GetValueOrDefault("", "https://cursorial.dev/ui"))
                    .Select(n => new CompletionItemInfo { Text = n, Kind = "value" })
                    .ToList();

            case "x:Reference":
                return NamedElementItems(xaml);

            case "Binding":
                return BindingArgumentItems(argument, xaml, caretOffset, body, hostElementName, namespaces, provider);

            case "RelativeSource":
            {
                // Shorthand modes first ({RelativeSource Self}), then the named parameters.
                var items = new List<CompletionItemInfo>();
                var modeType = (ResolveElement("RelativeSource", namespaces, provider) ?? ResolveElement("RelativeSourceExtension", namespaces, provider))
                    ?.ClrType.UnderlyingSystemType
                    ?.GetProperty("Mode", BindingFlags.Public | BindingFlags.Instance)?.PropertyType;
                if (modeType is { IsEnum: true })
                {
                    foreach (var mode in Enum.GetNames(modeType))
                        items.Add(new CompletionItemInfo { Text = mode, Kind = "value", Detail = modeType.Name });
                }

                AddExtensionParameters(items, extension, namespaces, provider);
                return items;
            }

            default:
            {
                // Any resolvable extension offers its members as named parameters.
                var items = new List<CompletionItemInfo>();
                AddExtensionParameters(items, extension, namespaces, provider);
                return items;
            }
        }
    }

    /// <summary>Adds the extension's members as named-parameter items (<c>Mode=</c>, <c>AncestorType=</c>, …).</summary>
    private static void AddExtensionParameters(List<CompletionItemInfo> items, string extension, Dictionary<string, string> namespaces, IXamlTypeMetadataProvider provider)
    {
        var extensionType = ResolveElement(extension, namespaces, provider) ?? ResolveElement(extension + "Extension", namespaces, provider);
        if (extensionType is null)
            return;

        foreach (var member in provider.GetKnownMemberNames(extensionType.ClrType))
            items.Add(new CompletionItemInfo { Text = member, Kind = "value", Detail = "parameter" });
    }

    private static List<CompletionItemInfo> CompleteExtensionNamedValue(
        string extension, string parameter, string value, string xaml, int caretOffset, string body, string hostElementName, Dictionary<string, string> namespaces, IXamlTypeMetadataProvider provider)
    {
        if (extension == "Binding")
        {
            if (parameter == "ElementName")
                return NamedElementItems(xaml);
            if (parameter == "Path")
                return BindingPathItems(value, xaml, caretOffset, body, hostElementName, namespaces, provider); // {Binding Path=Customer.| walks like the positional form
        }

        // RelativeSource= offers the standard shorthands as whole nested extensions:
        // display the mode, insert {RelativeSource Mode} (FindAncestor stubs its AncestorType).
        if (parameter == "RelativeSource")
        {
            var modeType = (ResolveElement("RelativeSource", namespaces, provider) ?? ResolveElement("RelativeSourceExtension", namespaces, provider))
                ?.ClrType.UnderlyingSystemType
                ?.GetProperty("Mode", BindingFlags.Public | BindingFlags.Instance)?.PropertyType;
            if (modeType is { IsEnum: true })
            {
                return Enum.GetNames(modeType)
                    .Select(mode =>
                    {
                        var insert = mode == "FindAncestor"
                            ? "{RelativeSource FindAncestor, AncestorType=}"
                            : "{" + $"RelativeSource {mode}" + "}";
                        return new CompletionItemInfo
                        {
                            Text = mode,
                            Kind = "value",
                            Detail = "shorthand",
                            Insert = insert,
                            Caret = mode == "FindAncestor" ? insert.Length - 1 : null, // inside '}'
                        };
                    })
                    .ToList();
            }
        }

        // Enum/bool parameters (e.g. {Binding …, Mode=OneWay}) via the extension's own type.
        var extensionType = ResolveElement(extension, namespaces, provider)
            ?? ResolveElement(extension + "Extension", namespaces, provider);
        var valueType = extensionType?.TryGetMember(parameter)?.ValueType.UnderlyingSystemType;
        var underlying = valueType is null ? null : Nullable.GetUnderlyingType(valueType) ?? valueType;
        if (underlying is null)
            return [];
        if (underlying.IsEnum)
            return Enum.GetNames(underlying).Select(n => new CompletionItemInfo { Text = n, Kind = "value", Detail = underlying.Name }).ToList();
        if (underlying == typeof(bool))
            return [new CompletionItemInfo { Text = "True", Kind = "value" }, new CompletionItemInfo { Text = "False", Kind = "value" }];
        if (underlying == typeof(Type))
            return SelectorTypeItems(namespaces, provider); // AncestorType= and friends: element types
        return [];
    }

    [GeneratedRegex("\\w+:Key\\s*=\\s*\"([^\"]+)\"")]
    private static partial Regex KeyAttribute();

    [GeneratedRegex("(?:\\w+:)?\\bName\\s*=\\s*\"([^\"]+)\"")]
    private static partial Regex NameAttribute();

    /// <summary>
    /// Document x:Key values (literal — they ARE literals) + the convention sweep of static
    /// <c>*Keys</c> classes, whose entries display as <c>Type.Field</c> and insert an
    /// <c>{x:Static Type.Field}</c> REFERENCE: robust against value changes, find-usages-able,
    /// symbol-validated at build. Falls back to the literal value when the document declares no
    /// intrinsics xmlns or the key type is unreachable from its namespaces.
    /// </summary>
    private static List<CompletionItemInfo> ResourceKeyItems(string xaml, Dictionary<string, string> namespaces, IXamlTypeMetadataProvider provider)
    {
        var items = new List<CompletionItemInfo>();
        foreach (Match match in KeyAttribute().Matches(xaml))
            items.Add(new CompletionItemInfo { Text = match.Groups[1].Value, Kind = "value", Detail = "document" });

        var intrinsicsPrefix = namespaces.FirstOrDefault(n => n.Value == IntrinsicsUri).Key;
        var typePrefixes = new Dictionary<Type, string?>();

        foreach (var (type, fieldName, value) in KeyConstants())
        {
            if (!typePrefixes.TryGetValue(type, out var typePrefix))
                typePrefixes[type] = typePrefix = intrinsicsPrefix is { Length: > 0 } ? XmlPrefixFor(type, namespaces, provider) : null;

            if (typePrefix is null)
            {
                items.Add(new CompletionItemInfo { Text = value, Kind = "value", Detail = type.Name });
                continue;
            }

            var qualified = typePrefix.Length == 0 ? $"{type.Name}.{fieldName}" : $"{typePrefix}:{type.Name}.{fieldName}";
            items.Add(new CompletionItemInfo
            {
                Text = qualified,
                Kind = "value",
                Detail = value,
                Insert = "{" + $"{intrinsicsPrefix}:Static {qualified}" + "}",
            });
        }

        return items.DistinctBy(i => i.Text).ToList();
    }

    /// <summary>(type, field, value) tuples from the <c>*Keys</c> convention sweep, cached with
    /// the AppDomain assembly count as the invalidation key — sweeping every loaded assembly's
    /// types on each keystroke is the single most expensive scan here. The host loop is
    /// single-threaded, so an unsynchronized cache field is safe.</summary>
    private static (int AssemblyCount, List<(Type Type, string Field, string Value)>? Entries) _keyConstants;

    private static List<(Type Type, string Field, string Value)> KeyConstants()
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        if (_keyConstants.Entries is { } cached && _keyConstants.AssemblyCount == assemblies.Length)
            return cached;

        var entries = new List<(Type, string, string)>();
        foreach (var assembly in assemblies)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch
            {
                continue;
            }

            foreach (var type in types)
            {
                if (!type.IsClass || !(type.IsAbstract && type.IsSealed) || !type.Name.EndsWith("Keys", StringComparison.Ordinal))
                    continue;
                if (KeySweepBlacklist.Contains(type.Name))
                    continue;
                foreach (var field in type.GetFields())
                {
                    if (field.IsLiteral && field.FieldType == typeof(string) && field.GetRawConstantValue() is string value)
                        entries.Add((type, field.Name, value));
                }
            }
        }

        _keyConstants = (assemblies.Length, entries);
        return entries;
    }

    /// <summary>
    /// *Keys classes that match the naming convention but are not resource keys (per Mike:
    /// option/setting key catalogs pollute resource completion). A framework-level opt-out
    /// convention would be the principled replacement for this list.
    /// </summary>
    private static readonly HashSet<string> KeySweepBlacklist = new(StringComparer.Ordinal) { "UserOptionKeys" };

    /// <summary>
    /// The document xmlns prefix under which <paramref name="type"/> resolves (empty string for
    /// the default xmlns), or null when no declared namespace reaches it.
    /// </summary>
    private static string? XmlPrefixFor(Type type, Dictionary<string, string> namespaces, IXamlTypeMetadataProvider provider)
    {
        foreach (var (prefix, uri) in namespaces.OrderBy(n => n.Key.Length))
        {
            if (SafeResolve(provider, uri, type.Name)?.ClrType.UnderlyingSystemType == type)
                return prefix;
        }

        return null;
    }

    private static List<CompletionItemInfo> NamedElementItems(string xaml)
        => NameAttribute().Matches(xaml)
            .Select(m => new CompletionItemInfo { Text = m.Groups[1].Value, Kind = "value", Detail = "x:Name" })
            .DistinctBy(i => i.Text)
            .ToList();

    /// <summary>
    /// <c>{x:Static Owner.Member}</c>: before the dot, types with public statics (static classes
    /// very much included); after it, the owner's public static fields and properties.
    /// </summary>
    private static List<CompletionItemInfo> StaticPathItems(string argument, Dictionary<string, string> namespaces, IXamlTypeMetadataProvider provider)
    {
        var dot = argument.LastIndexOf('.');
        if (dot < 0)
        {
            var items = new List<CompletionItemInfo>();
            foreach (var (prefix, uri) in namespaces)
            {
                foreach (var name in provider.GetKnownTypeNames(uri))
                {
                    var clr = SafeResolve(provider, uri, name)?.ClrType.UnderlyingSystemType;
                    if (clr is null)
                        continue;
                    var hasStatics = clr.GetFields(BindingFlags.Public | BindingFlags.Static).Length > 0
                        || clr.GetProperties(BindingFlags.Public | BindingFlags.Static).Length > 0;
                    if (!hasStatics)
                        continue;
                    items.Add(new CompletionItemInfo { Text = prefix.Length == 0 ? name : $"{prefix}:{name}", Kind = "value" });
                }
            }

            return items.DistinctBy(i => i.Text).ToList();
        }

        var owner = ResolveElement(argument[..dot], namespaces, provider)?.ClrType.UnderlyingSystemType;
        if (owner is null)
            return [];

        var members = new List<CompletionItemInfo>();
        foreach (var field in owner.GetFields(BindingFlags.Public | BindingFlags.Static))
            members.Add(new CompletionItemInfo { Text = field.Name, Kind = "value", Detail = owner.Name });
        foreach (var property in owner.GetProperties(BindingFlags.Public | BindingFlags.Static))
            members.Add(new CompletionItemInfo { Text = property.Name, Kind = "value", Detail = owner.Name });
        return members;
    }

    /// <summary>First positional Binding argument: data-context paths plus Binding's named parameters.</summary>
    private static List<CompletionItemInfo> BindingArgumentItems(string argument, string xaml, int caretOffset, string body, string hostElementName, Dictionary<string, string> namespaces, IXamlTypeMetadataProvider provider)
    {
        var items = BindingPathItems(argument, xaml, caretOffset, body, hostElementName, namespaces, provider);

        // Named parameters (Mode=, ElementName=, …) from the Binding type itself, only while the
        // argument has no dots (a dotted path is unambiguous).
        if (!argument.Contains('.'))
        {
            var binding = ResolveElement("Binding", namespaces, provider);
            if (binding is not null)
            {
                foreach (var member in provider.GetKnownMemberNames(binding.ClrType))
                    items.Add(new CompletionItemInfo { Text = member, Kind = "value", Detail = "parameter" });
            }
        }

        return items.DistinctBy(i => i.Text).ToList();
    }

    /// <summary>The CLR type named by the document's d:DataContext design attribute, when resolvable.</summary>
    internal static Type? DesignDataContextType(string xaml, Dictionary<string, string> namespaces, IXamlTypeMetadataProvider provider)
    {
        foreach (var prefix in namespaces.Where(n => DesignUris.Contains(n.Value)).Select(n => n.Key))
        {
            var match = Regex.Match(xaml, Regex.Escape(prefix) + ":DataContext\\s*=\\s*\"([^\"]+)\"");
            if (match.Success)
                return ResolveElement(match.Groups[1].Value, namespaces, provider)?.ClrType.UnderlyingSystemType;
        }

        return null;
    }

    /// <summary>Property paths rooted at the document's d:DataContext design type, dot-walking property types.</summary>
    private static List<CompletionItemInfo> BindingPathItems(string path, string xaml, int caretOffset, string body, string hostElementName, Dictionary<string, string> namespaces, IXamlTypeMetadataProvider provider)
    {
        var current = BindingSourceType(xaml, caretOffset, body, hostElementName, namespaces, provider);
        if (current is null)
            return [];

        var segments = path.Split('.');
        foreach (var segment in segments[..^1])
        {
            current = current?.GetProperty(segment, BindingFlags.Public | BindingFlags.Instance)?.PropertyType;
            if (current is null)
                return [];
        }

        return current!.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => new CompletionItemInfo { Text = p.Name, Kind = "value", Detail = p.PropertyType.Name })
            .ToList();
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
