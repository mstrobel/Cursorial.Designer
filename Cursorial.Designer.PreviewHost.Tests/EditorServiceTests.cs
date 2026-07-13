using Cursorial.Designer.PreviewHost;
using Cursorial.Designer.Protocol;

namespace Cursorial.Designer.Tests.PreviewHost;

/// <summary>
/// The editor-service commands (<c>analyze</c>, <c>complete</c>) drive live diagnostics and code
/// completion. They must work with NO preview session (a language-service host never sends
/// <c>initialize</c>) and on mid-edit, malformed documents.
/// </summary>
public class EditorServiceTests : IDisposable
{
    private const string Xmlns = "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    private readonly List<PreviewEvent> _events = [];
    private readonly PreviewSession _session = null!;

    public EditorServiceTests() => _session = new PreviewSession(_events.Add);

    public void Dispose() => _session.Dispose();

    [Fact]
    public void Analyze_works_without_initialize_and_reports_positions()
    {
        _session.Execute(new AnalyzeCommand
        {
            Id = 1,
            Xaml = $"<StackPanel {Xmlns}>\n    <NoSuchControl/>\n</StackPanel>",
            SourceUri = "file:///test/View.xaml",
        });

        var diagnostics = Assert.IsType<DiagnosticsEvent>(Assert.Single(_events, e => e is DiagnosticsEvent));
        Assert.Equal(1, diagnostics.ReplyTo);
        var error = Assert.Single(diagnostics.Items, d => d.Severity == "error");
        Assert.StartsWith("CUR", error.Code);
        Assert.Equal(2, error.Line);
    }

    [Fact]
    public void Analyze_reports_build_phase_diagnostics()
    {
        // CUR2105 (no collection access) is raised by the object-graph BUILDER, not the parser —
        // a parse-only analyze never saw it and the Problems pane stayed quiet.
        _session.Execute(new AnalyzeCommand
        {
            Id = 21,
            Xaml = $"""
                    <StackPanel {Xmlns}>
                        <ContentControl>
                            <ContentControl.Content>
                                <TextBlock Text="a"/>
                                <TextBlock Text="b"/>
                            </ContentControl.Content>
                        </ContentControl>
                    </StackPanel>
                    """,
        });

        var diagnostics = Assert.IsType<DiagnosticsEvent>(Assert.Single(_events, e => e is DiagnosticsEvent));
        var error = Assert.Single(diagnostics.Items, d => d.Severity == "error");
        Assert.Equal("CUR2105", error.Code);
        Assert.True(error.Line > 1, $"expected a positioned error, got line {error.Line}");
    }

    [Fact]
    public void Analyze_tolerates_malformed_mid_edit_documents()
    {
        _session.Execute(new AnalyzeCommand { Id = 2, Xaml = $"<StackPanel {Xmlns}>\n    <Butt" });

        var diagnostics = Assert.IsType<DiagnosticsEvent>(Assert.Single(_events, e => e is DiagnosticsEvent));
        Assert.Contains(diagnostics.Items, d => d.Severity == "error"); // malformed-XML diagnostic, not a crash
    }

    [Fact]
    public void Editor_services_survive_a_user_app_provider_hijack()
    {
        // A user assembly built against Cursorial <= 0.4.0 carries a generated module initializer
        // that installs its CLOSED SET as the process-default metadata provider the first time its
        // code executes (the analyze build-attempt instantiates user controls). Newer generators
        // emit no module initializer (the loader pull-discovers the entry assembly's provider), but
        // the designer must survive already-shipped binaries — and any user code that sets the
        // global default. The editor services must keep resolving through the host's pinned
        // reflection provider — this gutted completion for every session that had touched an app
        // assembly (the Cursorial.Samples bug).
        var original = Cursorial.UI.Xaml.XamlLoaderOptions.DefaultMetadataProvider;
        try
        {
            Cursorial.UI.Xaml.XamlLoaderOptions.DefaultMetadataProvider = new EmptyMetadataProvider();

            _session.Execute(new CompleteCommand
            {
                Id = 140,
                Xaml = $"<StackPanel {Xmlns}>\n    <Butt\n</StackPanel>",
                Line = 2,
                Column = 10,
            });

            var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
            Assert.Contains(completions.Items, i => i is { Text: "Button", Kind: "element" });
        }
        finally
        {
            Cursorial.UI.Xaml.XamlLoaderOptions.DefaultMetadataProvider = original;
        }
    }

    private sealed class EmptyMetadataProvider : Cursorial.UI.Xaml.IXamlTypeMetadataProvider
    {
        public Cursorial.UI.Xaml.XamlTypeResolution TryGetType(string xmlNamespace, string localName)
            => Cursorial.UI.Xaml.XamlTypeResolution.NotFound();

        public string[] GetClrNamespaces(string xmlNamespace) => [];

        public string[] GetKnownTypeNames(string xmlNamespace) => [];

        public string[] GetKnownMemberNames(Cursorial.UI.Xaml.IXamlType type) => [];
    }

    [Fact]
    public void Complete_offers_element_names_after_open_angle()
    {
        _session.Execute(new CompleteCommand
        {
            Id = 3,
            Xaml = $"<StackPanel {Xmlns}>\n    <Butt\n</StackPanel>",
            Line = 2,
            Column = 10, // right after "<Butt"
        });

        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i is { Text: "Button", Kind: "element" });
        Assert.Contains(completions.Items, i => i is { Text: "TextBlock", Kind: "element" });

        // Contextual filtering: statics/interfaces are not instantiable, and a StackPanel's
        // children take UIElements — brushes are instantiable but don't fit.
        Assert.DoesNotContain(completions.Items, i => i.Text == "AnimationDiagnostics");
        Assert.DoesNotContain(completions.Items, i => i.Text == "SolidColorBrush");
    }

    [Fact]
    public void Complete_offers_collection_property_elements_alongside_content()
    {
        // Style's ContentProperty is Setters, so the narrowed element list never surfaced
        // <Style.Styles> — but collections can ONLY be populated in element form.
        _session.Execute(new CompleteCommand
        {
            Id = 34,
            Xaml = $"<StackPanel {Xmlns}>\n  <Style>\n    <St\n</StackPanel>",
            Line = 3,
            Column = 7,
        });

        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i is { Text: "Style.Setters", Detail: "property element" });
        Assert.Contains(completions.Items, i => i is { Text: "Style.Children", Detail: "property element" }); // nested styles
        // Scalar members stay behind the explicit "<Style." gesture.
        Assert.DoesNotContain(completions.Items, i => i.Text == "Style.Selector");
    }

    [Fact]
    public void Complete_narrows_elements_to_a_property_elements_type()
    {
        _session.Execute(new CompleteCommand
        {
            Id = 31,
            Xaml = $"<StackPanel {Xmlns}>\n  <StackPanel.Children>\n    <Bu\n</StackPanel>",
            Line = 3,
            Column = 8,
        });

        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i.Text == "Button");
        Assert.DoesNotContain(completions.Items, i => i.Text == "SolidColorBrush");
    }

    [Fact]
    public void Complete_inside_resources_offers_the_whole_world()
    {
        // A resources property element accepts a replacing ResourceDictionary OR any keyed
        // value (brushes, styles, templates) — object-typed values mean no narrowing.
        _session.Execute(new CompleteCommand
        {
            Id = 33,
            Xaml = $"<DockPanel {Xmlns}>\n  <DockPanel.Resources>\n    <So\n</DockPanel>",
            Line = 3,
            Column = 7,
        });

        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i.Text == "SolidColorBrush");
        Assert.Contains(completions.Items, i => i.Text == "Style");
        Assert.Contains(completions.Items, i => i.Text == "ResourceDictionary");
        Assert.DoesNotContain(completions.Items, i => i.Text == "AnimationDiagnostics");
    }

    [Fact]
    public void Complete_leaves_object_typed_content_unfiltered()
    {
        // Button.Content is object: a brush is a legitimate child, so no narrowing applies.
        _session.Execute(new CompleteCommand
        {
            Id = 32,
            Xaml = $"<StackPanel {Xmlns}>\n  <Button>\n    <So\n</StackPanel>",
            Line = 3,
            Column = 7,
        });

        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i.Text == "SolidColorBrush");
        Assert.DoesNotContain(completions.Items, i => i.Text == "AnimationDiagnostics"); // still not instantiable
    }

    [Fact]
    public void Complete_offers_attribute_names_and_directives_inside_a_tag()
    {
        _session.Execute(new CompleteCommand
        {
            Id = 4,
            Xaml = $"<StackPanel {Xmlns}>\n    <Button Co\n</StackPanel>",
            Line = 2,
            Column = 15, // right after "Co"
        });

        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i is { Text: "Content", Kind: "attribute" });
        Assert.Contains(completions.Items, i => i is { Text: "IsEnabled", Kind: "attribute" });
        Assert.Contains(completions.Items, i => i is { Text: "x:Name", Kind: "attribute" });
    }

    [Fact]
    public void Complete_offers_enum_values_inside_attribute_quotes()
    {
        var xaml = $"<StackPanel {Xmlns}>\n    <Button Visibility=\"\n</StackPanel>";
        _session.Execute(new CompleteCommand { Id = 5, Xaml = xaml, Line = 2, Column = 25 });

        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i is { Text: "Visible", Kind: "value" });
        Assert.Contains(completions.Items, i => i.Text is "Collapsed" or "Hidden");
    }

    [Fact]
    public void Complete_offers_booleans_for_bool_attributes()
    {
        var xaml = $"<StackPanel {Xmlns}>\n    <Button IsEnabled=\"\n</StackPanel>";
        _session.Execute(new CompleteCommand { Id = 6, Xaml = xaml, Line = 2, Column = 24 });

        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i is { Text: "True", Kind: "value" });
        Assert.Contains(completions.Items, i => i is { Text: "False", Kind: "value" });
    }

    [Fact]
    public void Complete_between_tags_returns_nothing()
    {
        var xaml = $"<StackPanel {Xmlns}>\n    \n</StackPanel>";
        _session.Execute(new CompleteCommand { Id = 7, Xaml = xaml, Line = 2, Column = 3 });

        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Empty(completions.Items);
    }

    [Fact]
    public void Complete_offers_property_elements_after_element_dot()
    {
        // "<Button." inside a Button offers its members as property elements.
        var xaml = $"<StackPanel {Xmlns}>\n  <Button>\n    <Button.\n</StackPanel>";
        _session.Execute(new CompleteCommand { Id = 61, Xaml = xaml, Line = 3, Column = 13 });

        // BARE names: the buffer already carries "Button." and the IDE prefix breaks at the
        // dot — qualified inserts doubled the owner.
        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i is { Text: "Content", Detail: "property element" });
        Assert.Contains(completions.Items, i => i.Text == "Resources");
    }

    [Fact]
    public void Complete_offers_attached_properties_of_the_enclosing_parent()
    {
        var xaml = $"<DockPanel {Xmlns}>\n    <Button Do\n</DockPanel>";
        _session.Execute(new CompleteCommand { Id = 51, Xaml = xaml, Line = 2, Column = 15 });

        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i is { Text: "DockPanel.Dock", Kind: "attribute", Detail: "attached" });
    }

    [Fact]
    public void Complete_offers_attached_properties_of_an_explicit_owner()
    {
        // Grid is not the parent here; the explicit dotted owner still completes.
        var xaml = $"<StackPanel {Xmlns}>\n    <Button Grid.\n</StackPanel>";
        _session.Execute(new CompleteCommand { Id = 52, Xaml = xaml, Line = 2, Column = 18 });

        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i.Text == "Grid.Row");
        Assert.Contains(completions.Items, i => i.Text == "Grid.Column");
    }

    [Fact]
    public void Complete_offers_enum_values_for_attached_attributes()
    {
        var xaml = $"<DockPanel {Xmlns}>\n    <Button DockPanel.Dock=\"\n</DockPanel>";
        _session.Execute(new CompleteCommand { Id = 53, Xaml = xaml, Line = 2, Column = 29 });

        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i.Text == "Left");
        Assert.Contains(completions.Items, i => i.Text == "Bottom");
    }

    [Fact]
    public void Complete_offers_markup_extension_names_after_open_brace()
    {
        var xaml = $"<StackPanel {Xmlns}>\n    <TextBlock Text=\"{{\n</StackPanel>";
        _session.Execute(new CompleteCommand { Id = 41, Xaml = xaml, Line = 2, Column = 23 });

        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i.Text == "Binding");
        Assert.Contains(completions.Items, i => i.Text == "StaticResource");
        Assert.Contains(completions.Items, i => i.Text == "DynamicResource");
        Assert.Contains(completions.Items, i => i.Text == "x:Static");
    }

    [Fact]
    public void Complete_offers_resource_keys_from_document_and_key_classes()
    {
        var xaml = $"<StackPanel {Xmlns}>\n" +
                   "    <StackPanel.Resources><SolidColorBrush x:Key=\"PanelAccent\" Color=\"#3050c0\"/></StackPanel.Resources>\n" +
                   "    <Border Background=\"{DynamicResource \n</StackPanel>";
        _session.Execute(new CompleteCommand { Id = 42, Xaml = xaml, Line = 3, Column = 42 });

        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i is { Text: "PanelAccent", Detail: "document", Insert: null });

        // *Keys entries display as Type.Field, show the literal as detail, and INSERT an
        // x:Static reference — robust against value changes and symbol-validated at build.
        var themed = Assert.Single(completions.Items, i => i.Text == "ThemeKeys.ElevationDesktop");
        Assert.Equal("Theme.ElevationDesktop", themed.Detail);
        Assert.Equal("{x:Static ThemeKeys.ElevationDesktop}", themed.Insert);
    }

    [Fact]
    public void Complete_offers_static_types_then_members_for_x_static()
    {
        // Type position: statics very much included (ThemeKeys is a static class).
        var typesXaml = $"<StackPanel {Xmlns}>\n    <Border Background=\"{{DynamicResource {{x:Static \n</StackPanel>";
        _session.Execute(new CompleteCommand { Id = 43, Xaml = typesXaml, Line = 2, Column = 53 });
        var types = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(types.Items, i => i.Text == "ThemeKeys");

        // Member position after the dot.
        var membersXaml = $"<StackPanel {Xmlns}>\n    <Border Background=\"{{DynamicResource {{x:Static ThemeKeys.\n</StackPanel>";
        _session.Execute(new CompleteCommand { Id = 44, Xaml = membersXaml, Line = 2, Column = 63 });
        var members = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(members.Items, i => i is { Text: "ElevationDesktop", Detail: "ThemeKeys" });
    }

    [Fact]
    public void Complete_offers_binding_paths_from_design_data_context()
    {
        var ns = "clr-namespace:Cursorial.Designer.Tests.PreviewHost;assembly=Cursorial.Designer.PreviewHost.Tests";
        var xaml = "<StackPanel " + Xmlns +
                   " xmlns:d=\"http://schemas.microsoft.com/expression/blend/2008\"" +
                   $" xmlns:t=\"{ns}\" d:DataContext=\"t:DesignViewModel\">\n" +
                   "    <TextBlock Text=\"{Binding \n</StackPanel>";
        _session.Execute(new CompleteCommand
        {
            Id = 45,
            Xaml = xaml,
            Line = 2,
            Column = 31,
            Assemblies = [typeof(DesignViewModel).Assembly.Location],
        });

        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i is { Text: "Greeting", Detail: "String" });
        Assert.Contains(completions.Items, i => i is { Text: "Mode", Detail: "parameter" });
    }

    [Fact]
    public void Complete_offers_enum_values_for_binding_mode()
    {
        var xaml = $"<StackPanel {Xmlns}>\n    <TextBlock Text=\"{{Binding Greeting, Mode=\n</StackPanel>";
        _session.Execute(new CompleteCommand { Id = 46, Xaml = xaml, Line = 2, Column = 47 });

        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i.Text == "OneWay");
        Assert.Contains(completions.Items, i => i.Text == "TwoWay");
    }

    [Fact]
    public void Complete_ignores_commented_out_markup()
    {
        // A comment above the root must not become the "root tag" (its xmlns would poison the
        // namespace map), and a commented-out tag must not become the parent element.
        var xaml = "<!-- TODO: replace <OldRoot xmlns=\"https://nowhere\"> -->\n" +
                   $"<DockPanel {Xmlns}>\n" +
                   "    <!-- <Grid> -->\n" +
                   "    <Button Do\n</DockPanel>";
        _session.Execute(new CompleteCommand { Id = 71, Xaml = xaml, Line = 4, Column = 15 });

        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i is { Text: "DockPanel.Dock", Detail: "attached" });
        Assert.DoesNotContain(completions.Items, i => i.Text == "Grid.Row");
    }

    [Fact]
    public void Complete_at_document_start_returns_empty_not_error()
    {
        _session.Execute(new CompleteCommand { Id = 72, Xaml = $"<StackPanel {Xmlns}/>", Line = 1, Column = 1 });

        Assert.DoesNotContain(_events, e => e is ErrorEvent);
        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Empty(completions.Items);
    }

    [Fact]
    public void Complete_offers_attributes_after_a_value_containing_gt()
    {
        // A raw '>' inside an attribute value is legal XML and must not read as the tag's close.
        var xaml = $"<StackPanel {Xmlns}>\n    <TextBlock Text=\"a > b\" Vi\n</StackPanel>";
        _session.Execute(new CompleteCommand { Id = 73, Xaml = xaml, Line = 2, Column = 31 });

        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i is { Text: "Visibility", Kind: "attribute" });
    }

    [Fact]
    public void Complete_walks_dotted_paths_in_named_binding_path_form()
    {
        var ns = "clr-namespace:Cursorial.Designer.Tests.PreviewHost;assembly=Cursorial.Designer.PreviewHost.Tests";
        var xaml = "<StackPanel " + Xmlns +
                   " xmlns:d=\"http://schemas.microsoft.com/expression/blend/2008\"" +
                   $" xmlns:t=\"{ns}\" d:DataContext=\"t:DesignViewModel\">\n" +
                   "    <TextBlock Text=\"{Binding Path=Greeting.\n</StackPanel>";
        _session.Execute(new CompleteCommand
        {
            Id = 74,
            Xaml = xaml,
            Line = 2,
            Column = 45,
            Assemblies = [typeof(DesignViewModel).Assembly.Location],
        });

        // Path=Greeting. walks into String like the positional form — not the root properties.
        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i is { Text: "Length", Detail: "Int32" });
        Assert.DoesNotContain(completions.Items, i => i.Text == "Greeting");
    }

    [Fact]
    public void Complete_x_reference_offers_unprefixed_name_attributes()
    {
        // UIElement.Name is a plain CLR property: Name="…" (no x: prefix) names an element too.
        var xaml = $"<StackPanel {Xmlns}>\n    <Button Name=\"okButton\"/>\n    <TextBlock Text=\"{{x:Reference \n</StackPanel>";
        _session.Execute(new CompleteCommand { Id = 75, Xaml = xaml, Line = 3, Column = 35 });

        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i.Text == "okButton");
    }

    [Fact]
    public void Analyze_with_classify_returns_semantic_tokens()
    {
        var xaml = $"<StackPanel {Xmlns}>\n    <!-- note -->\n    <TextBlock x:Name=\"Title\" Grid.Row=\"1\" Visibility=\"Hidden\" IsVisible=\"x\" Text=\"{{Binding Path=Greeting, Mode=OneWay}}\" Tag=\"{{StaticResource PanelAccent}}\"/>\n</StackPanel>";
        _session.Execute(new AnalyzeCommand { Id = 81, Xaml = xaml, Classify = true });

        var diagnostics = Assert.IsType<DiagnosticsEvent>(_events.Last(e => e is DiagnosticsEvent));
        var tokens = diagnostics.Tokens!;
        Assert.Contains(tokens, t => t is { Kind: "element", Line: 3 });   // TextBlock, and Grid of Grid.Row
        Assert.Contains(tokens, t => t.Kind == "directive");               // x:Name
        Assert.Contains(tokens, t => t is { Kind: "attached", Length: 3 }); // "Row" (split from Grid)
        Assert.Contains(tokens, t => t is { Kind: "dot", Length: 1 });      // the '.' delimiter
        Assert.Contains(tokens, t => t.Kind == "extension");               // Binding / StaticResource
        Assert.Contains(tokens, t => t is { Kind: "comment", Line: 2 });   // <!-- note -->
        Assert.Contains(tokens, t => t is { Kind: "number", Length: 1 });  // Grid.Row="1" (attached value type)
        Assert.Contains(tokens, t => t is { Kind: "enumValue", Length: 6 }); // Hidden
        Assert.Contains(tokens, t => t is { Kind: "enumValue", Length: 6 } && t.Kind == "enumValue"); // OneWay via Binding.Mode
        Assert.Contains(tokens, t => t.Kind == "parameter");               // Path / Mode
        Assert.Contains(tokens, t => t is { Kind: "bindingPath", Length: 8 }); // Greeting
        Assert.Contains(tokens, t => t is { Kind: "resourceKey", Length: 11 }); // PanelAccent
        Assert.Contains(tokens, t => t.Kind == "brace");                   // extension delimiters
        Assert.Contains(tokens, t => t.Kind == "string");                  // x:Name's "Title" (untypeable)
    }

    [Fact]
    public void Hover_resolves_resource_keys_to_their_constants()
    {
        var xaml = $"<StackPanel {Xmlns} Tag=\"{{DynamicResource Theme.ElevationDesktop}}\"/>";
        var column = xaml.IndexOf("Theme.Elevation", StringComparison.Ordinal) + 3;
        _session.Execute(new HoverCommand { Id = 86, Xaml = xaml, Line = 1, Column = column });

        var hover = Assert.IsType<HoverInfoEvent>(_events.Last(e => e is HoverInfoEvent));
        Assert.Contains("const", hover.Signature);
        Assert.Contains("ThemeKeys.ElevationDesktop", hover.Signature);
    }

    [Fact]
    public void Hover_resolves_named_binding_paths_against_design_data_context()
    {
        var ns = "clr-namespace:Cursorial.Designer.Tests.PreviewHost;assembly=Cursorial.Designer.PreviewHost.Tests";
        var xaml = "<StackPanel " + Xmlns +
                   " xmlns:d=\"http://schemas.microsoft.com/expression/blend/2008\"" +
                   $" xmlns:t=\"{ns}\" d:DataContext=\"t:DesignViewModel\">\n" +
                   "    <TextBlock Text=\"{Binding Path=Greeting}\"/>\n</StackPanel>";
        var line2 = "    <TextBlock Text=\"{Binding Path=Greeting}\"/>";
        var column = line2.IndexOf("Greeting", StringComparison.Ordinal) + 3;
        _session.Execute(new HoverCommand
        {
            Id = 87,
            Xaml = xaml,
            Line = 2,
            Column = column,
            Assemblies = [typeof(DesignViewModel).Assembly.Location],
        });

        var hover = Assert.IsType<HoverInfoEvent>(_events.Last(e => e is HoverInfoEvent));
        Assert.Contains("DesignViewModel.Greeting", hover.Signature);
        Assert.Contains("String", hover.Signature);
    }

    [Fact]
    public void Definition_on_x_reference_jumps_in_document()
    {
        var xaml = $"<StackPanel {Xmlns}>\n    <Button x:Name=\"okButton\"/>\n    <TextBlock Text=\"{{x:Reference okButton}}\"/>\n</StackPanel>";
        var line3 = "    <TextBlock Text=\"{x:Reference okButton}\"/>";
        var column = line3.IndexOf("okButton", StringComparison.Ordinal) + 3;
        _session.Execute(new DefinitionCommand { Id = 88, Xaml = xaml, Line = 3, Column = column, FilePath = "/tmp/View.xaml" });

        var definition = Assert.IsType<DefinitionEvent>(_events.Last(e => e is DefinitionEvent));
        Assert.Equal("/tmp/View.xaml", definition.File);
        Assert.Equal(2, definition.Line); // the x:Name declaration
    }

    [Fact]
    public void Analyze_classifies_selector_paths()
    {
        var xaml = $"<StackPanel {Xmlns}>\n    <Style Selector=\"Button.accent:pointerover > TextBlock\"/>\n</StackPanel>";
        _session.Execute(new AnalyzeCommand { Id = 91, Xaml = xaml, Classify = true });

        var tokens = Assert.IsType<DiagnosticsEvent>(_events.Last(e => e is DiagnosticsEvent)).Tokens!;
        Assert.Contains(tokens, t => t is { Kind: "element", Line: 2, Length: 6 });        // Button
        Assert.Contains(tokens, t => t is { Kind: "styleClass", Length: 7 });              // .accent
        Assert.Contains(tokens, t => t is { Kind: "pseudoClass", Length: 12 });            // :pointerover
        Assert.Contains(tokens, t => t is { Kind: "element", Line: 2, Length: 9 });        // TextBlock
        Assert.Contains(tokens, t => t is { Kind: "dot", Length: 1 });                     // '>' combinator
    }

    [Fact]
    public void Hover_resolves_selector_type_tokens()
    {
        var xaml = $"<StackPanel {Xmlns}>\n    <Style Selector=\"Button.accent\"/>\n</StackPanel>";
        var line2 = "    <Style Selector=\"Button.accent\"/>";
        var column = line2.IndexOf("Button", StringComparison.Ordinal) + 3;
        _session.Execute(new HoverCommand { Id = 92, Xaml = xaml, Line = 2, Column = column });

        var hover = Assert.IsType<HoverInfoEvent>(_events.Last(e => e is HoverInfoEvent));
        Assert.StartsWith("class ", hover.Signature);
        Assert.Contains("Button", hover.Signature);
    }

    [Fact]
    public void Definition_on_selector_name_reference_jumps_in_document()
    {
        var xaml = $"<StackPanel {Xmlns}>\n    <Button x:Name=\"ok\"/>\n    <Style Selector=\"Button#ok\"/>\n</StackPanel>";
        var line3 = "    <Style Selector=\"Button#ok\"/>";
        var column = line3.IndexOf("#ok", StringComparison.Ordinal) + 2;
        _session.Execute(new DefinitionCommand { Id = 93, Xaml = xaml, Line = 3, Column = column, FilePath = "/tmp/View.xaml" });

        var definition = Assert.IsType<DefinitionEvent>(_events.Last(e => e is DefinitionEvent));
        Assert.Equal("/tmp/View.xaml", definition.File);
        Assert.Equal(2, definition.Line);
    }

    [Fact]
    public void Definition_on_selector_nesting_anchor_jumps_to_parent_selector()
    {
        var xaml = $"<StackPanel {Xmlns}>\n" +
                   "    <Style Selector=\"Button.accent\">\n" +
                   "        <Style Selector=\"^:pointerover\"/>\n" +
                   "    </Style>\n</StackPanel>";
        var line3 = "        <Style Selector=\"^:pointerover\"/>";
        var column = line3.IndexOf('^') + 1;
        _session.Execute(new DefinitionCommand { Id = 94, Xaml = xaml, Line = 3, Column = column, FilePath = "/tmp/View.xaml" });

        var definition = Assert.IsType<DefinitionEvent>(_events.Last(e => e is DefinitionEvent));
        Assert.Equal("/tmp/View.xaml", definition.File);
        Assert.Equal(2, definition.Line); // the parent <Style Selector="Button.accent">
    }

    [Fact]
    public void Definition_targets_survive_multiline_comments_above()
    {
        // BlankNonMarkup must preserve newlines: a multi-line comment above the target used to
        // shift every subsequent line number for in-document locations (the Shell.xaml ^ bug).
        var xaml = $"<StackPanel {Xmlns}>\n" +
                   "    <!-- a\n       multi-line\n       comment -->\n" +
                   "    <Style Selector=\"Button.accent\">\n" +
                   "      <Style.Children>\n" +
                   "        <Style Selector=\"^:pointerover\"/>\n" +
                   "      </Style.Children>\n" +
                   "    </Style>\n</StackPanel>";
        var line7 = "        <Style Selector=\"^:pointerover\"/>";
        var column = line7.IndexOf('^') + 1;
        _session.Execute(new DefinitionCommand { Id = 95, Xaml = xaml, Line = 7, Column = column, FilePath = "/tmp/View.xaml" });

        var definition = Assert.IsType<DefinitionEvent>(_events.Last(e => e is DefinitionEvent));
        Assert.Equal("/tmp/View.xaml", definition.File);
        Assert.Equal(5, definition.Line); // the parent Style — NOT shifted by the comment's newlines
    }

    [Fact]
    public void Complete_infers_binding_source_from_data_template()
    {
        var ns = "clr-namespace:Cursorial.Designer.Tests.PreviewHost;assembly=Cursorial.Designer.PreviewHost.Tests";
        var xaml = "<StackPanel " + Xmlns + $" xmlns:t=\"{ns}\">\n" +
                   "    <DataTemplate DataType=\"t:DesignViewModel\">\n" +
                   "        <TextBlock Text=\"{Binding \n" +
                   "    </DataTemplate>\n</StackPanel>";
        var line3 = "        <TextBlock Text=\"{Binding ";
        _session.Execute(new CompleteCommand
        {
            Id = 102,
            Xaml = xaml,
            Line = 3,
            Column = line3.Length + 1,
            Assemblies = [typeof(DesignViewModel).Assembly.Location],
        });

        // No root d:DataContext anywhere: the enclosing DataTemplate's DataType is the source.
        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i is { Text: "Greeting", Detail: "String" });
    }

    [Fact]
    public void Complete_infers_binding_source_from_element_name()
    {
        var xaml = $"<StackPanel {Xmlns}>\n" +
                   "    <Button x:Name=\"ok\" Content=\"X\"/>\n" +
                   "    <TextBlock Text=\"{Binding ElementName=ok, Path=\n</StackPanel>";
        var line3 = "    <TextBlock Text=\"{Binding ElementName=ok, Path=";
        _session.Execute(new CompleteCommand { Id = 103, Xaml = xaml, Line = 3, Column = line3.Length + 1 });

        // The named Button is the binding source: its properties complete.
        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i.Text == "Content");
        Assert.Contains(completions.Items, i => i.Text == "IsEnabled");
        Assert.DoesNotContain(completions.Items, i => i.Text == "Greeting");
    }

    [Fact]
    public void Analyze_classifies_relative_source_modes()
    {
        var xaml = $"<StackPanel {Xmlns}>\n    <TextBlock Text=\"{{Binding Path=X, RelativeSource={{RelativeSource Self}}}}\" Tag=\"{{Binding RelativeSource={{RelativeSource FindAncestor, AncestorType=DockPanel}}}}\"/>\n</StackPanel>";
        _session.Execute(new AnalyzeCommand { Id = 104, Xaml = xaml, Classify = true });

        var tokens = Assert.IsType<DiagnosticsEvent>(_events.Last(e => e is DiagnosticsEvent)).Tokens!;
        Assert.Contains(tokens, t => t is { Kind: "enumValue", Length: 4 });   // Self (shorthand)
        Assert.Contains(tokens, t => t is { Kind: "enumValue", Length: 12 });  // FindAncestor
        Assert.Contains(tokens, t => t is { Kind: "element", Length: 9 });     // DockPanel (AncestorType)
    }

    [Fact]
    public void Complete_infers_binding_source_from_relative_source_self()
    {
        var xaml = $"<StackPanel {Xmlns}>\n    <TextBlock Text=\"{{Binding RelativeSource={{RelativeSource Self}}, Path=\n</StackPanel>";
        var line2 = "    <TextBlock Text=\"{Binding RelativeSource={RelativeSource Self}, Path=";
        _session.Execute(new CompleteCommand { Id = 105, Xaml = xaml, Line = 2, Column = line2.Length + 1 });

        // Self anchors the binding at the TextBlock itself.
        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i.Text == "Text");
        Assert.Contains(completions.Items, i => i.Text == "Visibility");
    }

    [Fact]
    public void Complete_infers_binding_source_from_find_ancestor()
    {
        var xaml = $"<DockPanel {Xmlns}>\n    <TextBlock Text=\"{{Binding RelativeSource={{RelativeSource FindAncestor, AncestorType=DockPanel}}, Path=\n</DockPanel>";
        var line2 = "    <TextBlock Text=\"{Binding RelativeSource={RelativeSource FindAncestor, AncestorType=DockPanel}, Path=";
        _session.Execute(new CompleteCommand { Id = 106, Xaml = xaml, Line = 2, Column = line2.Length + 1 });

        // FindAncestor anchors at the declared AncestorType.
        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i.Text == "LastChildFill");
    }

    [Fact]
    public void Complete_offers_relative_source_modes_and_parameters()
    {
        var xaml = $"<StackPanel {Xmlns}>\n    <TextBlock Tag=\"{{Binding RelativeSource={{RelativeSource \n</StackPanel>";
        var line2 = "    <TextBlock Tag=\"{Binding RelativeSource={RelativeSource ";
        _session.Execute(new CompleteCommand { Id = 108, Xaml = xaml, Line = 2, Column = line2.Length + 1 });

        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i is { Text: "Self", Detail: "RelativeSourceMode" });
        Assert.Contains(completions.Items, i => i is { Text: "FindAncestor" });
        Assert.Contains(completions.Items, i => i is { Text: "Mode", Detail: "parameter" });
        Assert.Contains(completions.Items, i => i is { Text: "AncestorType", Detail: "parameter" });
    }

    [Fact]
    public void Complete_offers_relative_source_shorthands_for_the_parameter()
    {
        var xaml = $"<StackPanel {Xmlns}>\n    <TextBlock Tag=\"{{Binding RelativeSource=\n</StackPanel>";
        var line2 = "    <TextBlock Tag=\"{Binding RelativeSource=";
        _session.Execute(new CompleteCommand { Id = 109, Xaml = xaml, Line = 2, Column = line2.Length + 1 });

        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        var self = Assert.Single(completions.Items, i => i.Text == "Self");
        Assert.Equal("{RelativeSource Self}", self.Insert);
        var ancestor = Assert.Single(completions.Items, i => i.Text == "FindAncestor");
        Assert.Equal("{RelativeSource FindAncestor, AncestorType=}", ancestor.Insert);
        Assert.Equal(ancestor.Insert!.Length - 1, ancestor.Caret); // caret parks inside '}'

    }

    [Fact]
    public void Complete_offers_element_types_for_ancestor_type()
    {
        var xaml = $"<StackPanel {Xmlns}>\n    <TextBlock Tag=\"{{Binding RelativeSource={{RelativeSource FindAncestor, AncestorType=\n</StackPanel>";
        var line2 = "    <TextBlock Tag=\"{Binding RelativeSource={RelativeSource FindAncestor, AncestorType=";
        _session.Execute(new CompleteCommand { Id = 110, Xaml = xaml, Line = 2, Column = line2.Length + 1 });

        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i is { Text: "DockPanel", Kind: "element" });
        Assert.Contains(completions.Items, i => i is { Text: "Button", Kind: "element" });
        Assert.DoesNotContain(completions.Items, i => i.Text == "SolidColorBrush"); // not a UIElement
    }

    [Fact]
    public void Analyze_classifies_grid_lengths()
    {
        var xaml = $"<Grid {Xmlns}>\n    <Grid.ColumnDefinitions>\n        <ColumnDefinition Width=\"Auto\"/>\n        <ColumnDefinition Width=\"2*\"/>\n        <ColumnDefinition Width=\"12\"/>\n    </Grid.ColumnDefinitions>\n</Grid>";
        _session.Execute(new AnalyzeCommand { Id = 111, Xaml = xaml, Classify = true });

        var tokens = Assert.IsType<DiagnosticsEvent>(_events.Last(e => e is DiagnosticsEvent)).Tokens!;
        Assert.Contains(tokens, t => t is { Kind: "enumValue", Line: 3, Length: 4 }); // Auto
        Assert.Contains(tokens, t => t is { Kind: "number", Line: 4, Length: 2 });    // 2*
        Assert.Contains(tokens, t => t is { Kind: "number", Line: 5, Length: 2 });    // 12
    }

    [Fact]
    public void Hover_resolves_relative_source_modes()
    {
        var xaml = $"<StackPanel {Xmlns}>\n    <TextBlock Text=\"{{Binding Path=X, RelativeSource={{RelativeSource Self}}}}\"/>\n</StackPanel>";
        var line2 = "    <TextBlock Text=\"{Binding Path=X, RelativeSource={RelativeSource Self}}\"/>";
        var column = line2.IndexOf("Self", StringComparison.Ordinal) + 2;
        _session.Execute(new HoverCommand { Id = 107, Xaml = xaml, Line = 2, Column = column });

        var hover = Assert.IsType<HoverInfoEvent>(_events.Last(e => e is HoverInfoEvent));
        Assert.Contains("RelativeSourceMode.Self", hover.Signature);
    }

    [Fact]
    public void Complete_narrows_element_names_by_typed_xmlns_prefix()
    {
        var ns = "clr-namespace:Cursorial.UI.Bars;assembly=Cursorial.UI.Bars";
        var xaml = $"<StackPanel {Xmlns} xmlns:bars=\"{ns}\">\n    <bars:\n</StackPanel>";
        _session.Execute(new CompleteCommand
        {
            Id = 112,
            Xaml = xaml,
            Line = 2,
            Column = 11,
            Assemblies = [typeof(Cursorial.UI.Bars.Toolbar).Assembly.Location],
        });

        // The prefix is already typed: bare names, that namespace only.
        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i is { Text: "Toolbar", Kind: "element" });
        Assert.DoesNotContain(completions.Items, i => i.Text == "Button");
        Assert.DoesNotContain(completions.Items, i => i.Text.StartsWith("bars:", StringComparison.Ordinal));
    }

    [Fact]
    public void Complete_narrows_extension_names_by_typed_xmlns_prefix()
    {
        var xaml = $"<StackPanel {Xmlns} Tag=\"{{x:\n</StackPanel>";
        var line1 = $"<StackPanel {Xmlns} Tag=\"{{x:";
        _session.Execute(new CompleteCommand { Id = 113, Xaml = xaml, Line = 1, Column = line1.Length + 1 });

        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i is { Text: "Static" });
        Assert.Contains(completions.Items, i => i is { Text: "Reference" });
        Assert.DoesNotContain(completions.Items, i => i.Text == "Binding"); // wrong namespace
    }

    [Fact]
    public void Complete_offers_types_for_type_typed_attributes()
    {
        var ns = "clr-namespace:Cursorial.Designer.Tests.PreviewHost;assembly=Cursorial.Designer.PreviewHost.Tests";
        var xaml = $"<StackPanel {Xmlns} xmlns:t=\"{ns}\">\n    <DataTemplate DataType=\"t:\n</StackPanel>";
        var line2 = "    <DataTemplate DataType=\"t:";
        _session.Execute(new CompleteCommand
        {
            Id = 114,
            Xaml = xaml,
            Line = 2,
            Column = line2.Length + 1,
            Assemblies = [typeof(DesignViewModel).Assembly.Location],
        });

        // Type-typed values offer types — narrowed by the typed prefix, bare names.
        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i is { Text: "DesignViewModel", Kind: "element" });
        Assert.DoesNotContain(completions.Items, i => i.Text == "Button");
    }

    [Fact]
    public void Complete_offers_types_in_x_type_extension()
    {
        var ns = "clr-namespace:Cursorial.Designer.Tests.PreviewHost;assembly=Cursorial.Designer.PreviewHost.Tests";
        var xaml = $"<StackPanel {Xmlns} xmlns:t=\"{ns}\">\n    <ContentControl Content=\"{{x:Type t:\n</StackPanel>";
        var line2 = "    <ContentControl Content=\"{x:Type t:";
        _session.Execute(new CompleteCommand
        {
            Id = 117,
            Xaml = xaml,
            Line = 2,
            Column = line2.Length + 1,
            Assemblies = [typeof(DesignViewModel).Assembly.Location],
        });

        // The positional argument is a type — narrowed by the typed prefix, bare names.
        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i is { Text: "DesignViewModel", Kind: "element" });
        Assert.DoesNotContain(completions.Items, i => i.Text == "Button");
    }

    [Fact]
    public void Complete_offers_types_for_design_data_context()
    {
        var xaml = $"<StackPanel {Xmlns} xmlns:d=\"https://cursorial.dev/xaml/design\">\n    <StackPanel d:DataContext=\"\n</StackPanel>";
        var line2 = "    <StackPanel d:DataContext=\"";
        _session.Execute(new CompleteCommand { Id = 120, Xaml = xaml, Line = 2, Column = line2.Length + 1 });

        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i is { Text: "Button", Kind: "element" });
    }

    [Fact]
    public void Complete_offers_resource_extension_stubs_for_attribute_values()
    {
        var xaml = $"<StackPanel {Xmlns}>\n    <Button Background=\"\n</StackPanel>";
        var line2 = "    <Button Background=\"";
        _session.Execute(new CompleteCommand { Id = 124, Xaml = xaml, Line = 2, Column = line2.Length + 1 });

        // Just the extensions, caret parked inside — the key list is the NEXT completion.
        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i is { Text: "StaticResource", Insert: "{StaticResource }", Caret: 16 });
        Assert.Contains(completions.Items, i => i is { Text: "DynamicResource", Insert: "{DynamicResource }", Caret: 17 });
    }

    [Fact]
    public void Complete_offers_binding_paths_before_following_named_arguments()
    {
        var xaml = $"<StackPanel {Xmlns}>\n    <TextBlock Text=\"{{Binding  RelativeSource={{RelativeSource Self}}}}\"/>\n</StackPanel>";
        var line2 = "    <TextBlock Text=\"{Binding  RelativeSource={RelativeSource Self}}\"/>";
        var column = line2.IndexOf("{Binding ", StringComparison.Ordinal) + "{Binding ".Length + 1; // between the two spaces
        _session.Execute(new CompleteCommand { Id = 125, Xaml = xaml, Line = 2, Column = column });

        // Source inference reads the RelativeSource clause AFTER the caret: Self → TextBlock.
        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i.Text == "Text");
    }

    [Fact]
    public void Complete_wraps_registered_key_constants_in_static_references()
    {
        var xaml = $"<StackPanel {Xmlns}>\n    <Button Background=\"{{StaticResource \n</StackPanel>";
        var line2 = "    <Button Background=\"{StaticResource ";
        _session.Execute(new CompleteCommand { Id = 123, Xaml = xaml, Line = 2, Column = line2.Length + 1 });

        // Key constants from every REGISTERED assembly insert as static references, not raw
        // strings — CursorialDialogThemeKeys regressed here when Dialogs had no xmlns map.
        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i =>
            i.Text == "CursorialDialogThemeKeys.CommandLinkIcon"
            && i.Insert == "{x:Static CursorialDialogThemeKeys.CommandLinkIcon}");
    }

    [Fact]
    public void Analyze_classifies_xmlns_prefixes()
    {
        var ns = "clr-namespace:Cursorial.UI.Bars;assembly=Cursorial.UI.Bars";
        var xaml = $"<StackPanel {Xmlns} xmlns:bars=\"{ns}\">\n    <bars:Toolbar/>\n</StackPanel>";
        _session.Execute(new AnalyzeCommand { Id = 122, Xaml = xaml, Classify = true });

        var tokens = Assert.IsType<DiagnosticsEvent>(_events.Last(e => e is DiagnosticsEvent)).Tokens!;
        Assert.Contains(tokens, t => t is { Kind: "namespace", Line: 1, Length: 4 }); // bars in the declaration
        Assert.Contains(tokens, t => t is { Kind: "namespace", Line: 2, Length: 4 }); // bars in the tag
        Assert.Contains(tokens, t => t is { Kind: "element", Line: 2, Length: 7 });   // Toolbar, bare
    }

    [Fact]
    public void Analyze_classifies_design_data_context_types()
    {
        var xaml = $"<StackPanel {Xmlns} xmlns:d=\"https://cursorial.dev/xaml/design\">\n    <StackPanel d:DataContext=\"Button\"/>\n</StackPanel>";
        _session.Execute(new AnalyzeCommand { Id = 121, Xaml = xaml, Classify = true });

        var tokens = Assert.IsType<DiagnosticsEvent>(_events.Last(e => e is DiagnosticsEvent)).Tokens!;
        Assert.Contains(tokens, t => t is { Kind: "element", Line: 2, Length: 6 }); // the type reference
    }

    [Fact]
    public void Complete_offers_intrinsic_built_in_types()
    {
        var xaml = $"<StackPanel {Xmlns}>\n    <DataTemplate DataType=\"x:\n</StackPanel>";
        var line2 = "    <DataTemplate DataType=\"x:";
        _session.Execute(new CompleteCommand { Id = 126, Xaml = xaml, Line = 2, Column = line2.Length + 1 });

        // The intrinsics prefix carries the XAML2009 built-ins (bare — the prefix is typed).
        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i is { Text: "String", Kind: "element" });
        Assert.Contains(completions.Items, i => i is { Text: "Int32", Kind: "element" });
    }

    [Fact]
    public void Complete_offers_design_and_compat_attributes()
    {
        var extra = "xmlns:d=\"https://cursorial.dev/xaml/design\" xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\"";
        var xaml = $"<StackPanel {Xmlns} {extra}>\n    <TextBlock \n</StackPanel>";
        _session.Execute(new CompleteCommand { Id = 127, Xaml = xaml, Line = 2, Column = "    <TextBlock ".Length + 1 });

        // The declared design/compat prefixes carry their vocabularies (no IDE offers our xmlns
        // URIs, so the templates preset the prefixes and the attributes complete from there).
        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i is { Text: "d:DataContext", Kind: "attribute", Detail: "design-time" });
        Assert.Contains(completions.Items, i => i is { Text: "d:DesignWidth", Kind: "attribute" });
        Assert.Contains(completions.Items, i => i is { Text: "d:DesignHeight", Kind: "attribute" });
        Assert.Contains(completions.Items, i => i is { Text: "mc:Ignorable", Kind: "attribute", Detail: "markup compatibility" });
    }

    [Fact]
    public void Complete_offers_design_prefixes_for_mc_ignorable()
    {
        var extra = "xmlns:d=\"https://cursorial.dev/xaml/design\" xmlns:mc=\"http://schemas.openxmlformats.org/markup-compatibility/2006\"";
        var xaml = $"<StackPanel {Xmlns} {extra}>\n    <TextBlock mc:Ignorable=\"\n</StackPanel>";
        var line2 = "    <TextBlock mc:Ignorable=\"";
        _session.Execute(new CompleteCommand { Id = 128, Xaml = xaml, Line = 2, Column = line2.Length + 1 });

        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i is { Text: "d", Kind: "value" });
    }

    [Fact]
    public void Complete_offers_property_elements_for_element_form_only_scalars()
    {
        // Template has no string converter: element form is its only authoring, so it earns a
        // spot in the unqualified child list (convertible scalars stay behind "<Parent.").
        _session.Execute(new CompleteCommand
        {
            Id = 134,
            Xaml = $"<StackPanel {Xmlns}>\n  <ToggleButton>\n    <Tem\n</StackPanel>",
            Line = 3,
            Column = 9,
        });

        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i is { Text: "ToggleButton.Template", Detail: "property element" });
        Assert.DoesNotContain(completions.Items, i => i.Text == "ToggleButton.IsChecked"); // convertible: attribute form
    }

    [Fact]
    public void Complete_emits_bare_members_after_a_typed_qualifier()
    {
        // The buffer already carries "ToggleButton." and the IDE's prefix breaks at the dot —
        // qualified inserts doubled the owner (ToggleButton.ToggleButton.Template).
        _session.Execute(new CompleteCommand
        {
            Id = 135,
            Xaml = $"<StackPanel {Xmlns}>\n  <ToggleButton>\n    <ToggleButton.Tem\n</StackPanel>",
            Line = 3,
            Column = 22,
        });

        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i is { Text: "Template", Kind: "element" });
        Assert.DoesNotContain(completions.Items, i => i.Text.Contains('.'));
    }

    [Fact]
    public void Complete_offers_style_target_members_for_setter_property()
    {
        var xaml = $"<StackPanel {Xmlns}>\n  <Style Selector=\"Button\" TargetType=\"Button\">\n    <Setter Property=\"\n</StackPanel>";
        var line3 = "    <Setter Property=\"";
        _session.Execute(new CompleteCommand { Id = 136, Xaml = xaml, Line = 3, Column = line3.Length + 1 });

        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i is { Text: "Content", Detail: "Button" });
        Assert.Contains(completions.Items, i => i is { Text: "Visibility" });
    }

    [Fact]
    public void Complete_offers_attached_members_for_dotted_setter_property()
    {
        var xaml = $"<StackPanel {Xmlns}>\n  <Style Selector=\"Button\">\n    <Setter Property=\"TextElement.\n</StackPanel>";
        var line3 = "    <Setter Property=\"TextElement.";
        _session.Execute(new CompleteCommand { Id = 137, Xaml = xaml, Line = 3, Column = line3.Length + 1 });

        // Bare names after the dot (the prefix breaks there); TextElement's attached set.
        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i is { Text: "TextAttributes" });
        Assert.DoesNotContain(completions.Items, i => i.Text.Contains('.'));
    }

    [Fact]
    public void Complete_types_setter_value_from_the_property_reference()
    {
        var xaml = $"<StackPanel {Xmlns}>\n  <Style Selector=\"Button\" TargetType=\"Button\">\n    <Setter Property=\"Visibility\" Value=\"\n</StackPanel>";
        var line3 = "    <Setter Property=\"Visibility\" Value=\"";
        _session.Execute(new CompleteCommand { Id = 138, Xaml = xaml, Line = 3, Column = line3.Length + 1 });

        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i is { Text: "Visible", Kind: "value" });
    }

    [Fact]
    public void Complete_offers_target_members_for_template_binding()
    {
        var xaml = $$"""
                    <StackPanel {{Xmlns}}>
                        <Style Selector="Expander" TargetType="Expander">
                            <Setter Property="Template">
                                <ControlTemplate TargetType="Expander">
                                    <Border Background="{TemplateBinding 
                    """;
        var caretLine = "                                <Border Background=\"{TemplateBinding ";
        _session.Execute(new CompleteCommand { Id = 133, Xaml = xaml, Line = 5, Column = caretLine.Length + 1 });

        // The bindable names are the enclosing ControlTemplate's TargetType members.
        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i is { Text: "IsExpanded", Detail: "Expander" });
        Assert.Contains(completions.Items, i => i is { Text: "Header" });
    }

    [Fact]
    public void Complete_offers_grid_length_keywords()
    {
        var xaml = $"<ColumnDefinition {Xmlns} Width=\"\n";
        var line1 = $"<ColumnDefinition {Xmlns} Width=\"";
        _session.Execute(new CompleteCommand { Id = 115, Xaml = xaml, Line = 1, Column = line1.Length + 1 });

        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i is { Text: "Auto", Kind: "value" });
        Assert.Contains(completions.Items, i => i is { Text: "*", Kind: "value" });
    }

    [Fact]
    public void Hover_resolves_x_class_to_the_code_behind_type()
    {
        // DesignViewModel stands in for a code-behind type living in a registered user assembly.
        var xaml = $"<StackPanel {Xmlns} x:Class=\"Cursorial.Designer.Tests.PreviewHost.DesignViewModel\"/>";
        var line1 = $"<StackPanel {Xmlns} x:Class=\"Cursorial.Designer.Tests.PreviewHost.";
        _session.Execute(new HoverCommand
        {
            Id = 132,
            Xaml = xaml,
            Line = 1,
            Column = line1.Length + 3,
            Assemblies = [typeof(DesignViewModel).Assembly.Location],
        });

        var hover = Assert.IsType<HoverInfoEvent>(_events.Last(e => e is HoverInfoEvent));
        Assert.Contains("Cursorial.Designer.Tests.PreviewHost.DesignViewModel", hover.Signature);
        Assert.Contains("partial class", hover.Signature);
    }

    [Fact]
    public void Definition_on_resource_uri_opens_the_referenced_document()
    {
        var dir = Directory.CreateTempSubdirectory("cursorial-editor-test-");
        try
        {
            var views = Directory.CreateDirectory(Path.Combine(dir.FullName, "Views"));
            var target = Path.Combine(views.FullName, "Res.xaml");
            File.WriteAllText(target, "<ResourceDictionary/>");
            var main = Path.Combine(views.FullName, "Main.xaml");

            // Relative reference: probes next to the document.
            var relXaml = $"<StackPanel {Xmlns}>\n    <StackPanel.Resources><ResourceDictionary Source=\"Res.xaml\"/></StackPanel.Resources>\n</StackPanel>";
            var relLine = "    <StackPanel.Resources><ResourceDictionary Source=\"Res.xaml\"/></StackPanel.Resources>";
            _session.Execute(new DefinitionCommand
            {
                Id = 130, Xaml = relXaml, Line = 2,
                Column = relLine.IndexOf("Res.xaml", StringComparison.Ordinal) + 3,
                FilePath = main,
            });
            var relative = Assert.IsType<DefinitionEvent>(_events.Last(e => e is DefinitionEvent));
            Assert.Equal(target, relative.File);

            // Scheme reference: the URI's path portion probes each ancestor (project layout).
            var absXaml = $"<StackPanel {Xmlns}>\n    <StackPanel.Resources><ResourceDictionary Source=\"cursorial://App/Views/Res.xaml\"/></StackPanel.Resources>\n</StackPanel>";
            var absLine = "    <StackPanel.Resources><ResourceDictionary Source=\"cursorial://App/Views/Res.xaml\"/></StackPanel.Resources>";
            _session.Execute(new DefinitionCommand
            {
                Id = 131, Xaml = absXaml, Line = 2,
                Column = absLine.IndexOf("Views/Res.xaml", StringComparison.Ordinal) + 8,
                FilePath = main,
            });
            var schemed = Assert.IsType<DefinitionEvent>(_events.Last(e => e is DefinitionEvent));
            Assert.Equal(target, schemed.File);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Definition_on_xmlns_prefix_jumps_to_declaration()
    {
        var ns = "clr-namespace:Cursorial.UI.Bars;assembly=Cursorial.UI.Bars";
        var xaml = $"<StackPanel {Xmlns} xmlns:bars=\"{ns}\">\n    <bars:Toolbar/>\n</StackPanel>";
        var line2 = "    <bars:Toolbar/>";
        var column = line2.IndexOf("bars", StringComparison.Ordinal) + 2; // caret inside the prefix
        _session.Execute(new DefinitionCommand { Id = 116, Xaml = xaml, Line = 2, Column = column, FilePath = "/tmp/View.xaml" });

        var definition = Assert.IsType<DefinitionEvent>(_events.Last(e => e is DefinitionEvent));
        Assert.Equal("/tmp/View.xaml", definition.File);
        Assert.Equal(1, definition.Line); // the xmlns:bars declaration in the root tag
        Assert.Contains("xmlns:bars", definition.Symbol);
    }

    [Fact]
    public void Definition_on_xmlns_prefix_in_type_value_jumps_to_declaration()
    {
        var ns = "clr-namespace:Cursorial.UI.Bars;assembly=Cursorial.UI.Bars";
        var xaml = $"<StackPanel {Xmlns} xmlns:bars=\"{ns}\">\n    <DataTemplate DataType=\"bars:Toolbar\"/>\n</StackPanel>";
        var line2 = "    <DataTemplate DataType=\"bars:Toolbar\"/>";
        var column = line2.IndexOf("bars:Toolbar", StringComparison.Ordinal) + 2; // caret inside the prefix
        _session.Execute(new DefinitionCommand { Id = 118, Xaml = xaml, Line = 2, Column = column, FilePath = "/tmp/View.xaml" });

        var definition = Assert.IsType<DefinitionEvent>(_events.Last(e => e is DefinitionEvent));
        Assert.Equal("/tmp/View.xaml", definition.File);
        Assert.Equal(1, definition.Line); // the xmlns:bars declaration in the root tag
        Assert.Contains("xmlns:bars", definition.Symbol);
    }

    [Fact]
    public void Hover_resolves_type_name_in_x_type_extension()
    {
        var xaml = $"<StackPanel {Xmlns}>\n    <ContentControl Content=\"{{x:Type Button}}\"/>\n</StackPanel>";
        var line2 = "    <ContentControl Content=\"{x:Type Button}\"/>";
        var column = line2.IndexOf("Button", StringComparison.Ordinal) + 3;
        _session.Execute(new HoverCommand { Id = 119, Xaml = xaml, Line = 2, Column = column });

        var hover = Assert.IsType<HoverInfoEvent>(_events.Last(e => e is HoverInfoEvent));
        Assert.Contains("Cursorial.UI.Controls.Button", hover.Signature);
    }

    [Fact]
    public void Complete_offers_pseudo_classes_in_selectors()
    {
        var xaml = $"<StackPanel {Xmlns}>\n    <Style Selector=\"Button:\n</StackPanel>";
        _session.Execute(new CompleteCommand { Id = 96, Xaml = xaml, Line = 2, Column = 29 }); // after ':'


        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i is { Text: "pointerover", Detail: "InteractionState.PointerOver" });
        Assert.Contains(completions.Items, i => i is { Text: "disabled" });
        Assert.Contains(completions.Items, i => i is { Text: "is", Insert: "is(" });
        // Control-defined mappings (registered in static ctors, warmed by the completion sweep).
        Assert.Contains(completions.Items, i => i.Text == "today" && i.Detail!.Contains("IsToday"));
    }

    [Fact]
    public void Complete_offers_element_types_in_selectors()
    {
        var xaml = $"<StackPanel {Xmlns}>\n    <Style Selector=\"But\n</StackPanel>";
        _session.Execute(new CompleteCommand { Id = 97, Xaml = xaml, Line = 2, Column = 25 });

        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i is { Text: "Button", Kind: "element" });
        Assert.DoesNotContain(completions.Items, i => i.Text == "SolidColorBrush"); // not a UIElement
    }

    [Fact]
    public void Complete_offers_style_classes_in_selectors()
    {
        var xaml = $"<StackPanel {Xmlns} Classes=\"fancy accent\">\n    <Style Selector=\"Button.\n</StackPanel>";
        var line2 = "    <Style Selector=\"Button.";
        _session.Execute(new CompleteCommand { Id = 100, Xaml = xaml, Line = 2, Column = line2.Length + 1 });

        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i is { Text: "caps-unicode", Detail: "capability" });
        Assert.Contains(completions.Items, i => i is { Text: "caps-nocolor", Detail: "capability" });
        Assert.Contains(completions.Items, i => i is { Text: "fancy", Detail: "document" });
        Assert.Contains(completions.Items, i => i is { Text: "accent", Detail: "document" });
        Assert.DoesNotContain(completions.Items, i => i.Text == "caps-ascii"); // reserved, never stamped
    }

    [Fact]
    public void Complete_offers_template_combinator_after_slash()
    {
        var xaml = $"<StackPanel {Xmlns}>\n    <Style Selector=\"Button:pressed /\n</StackPanel>";
        var line2 = "    <Style Selector=\"Button:pressed /";
        _session.Execute(new CompleteCommand { Id = 101, Xaml = xaml, Line = 2, Column = line2.Length + 1 });

        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i is { Text: "template/", Detail: "combinator" });
    }

    [Fact]
    public void Hover_resolves_interaction_pseudo_classes()
    {
        var xaml = $"<StackPanel {Xmlns}>\n    <Style Selector=\"Button:pointerover\"/>\n</StackPanel>";
        var line2 = "    <Style Selector=\"Button:pointerover\"/>";
        var column = line2.IndexOf("pointerover", StringComparison.Ordinal) + 3;
        _session.Execute(new HoverCommand { Id = 98, Xaml = xaml, Line = 2, Column = column });

        var hover = Assert.IsType<HoverInfoEvent>(_events.Last(e => e is HoverInfoEvent));
        Assert.Contains("InteractionState.PointerOver", hover.Signature);
    }

    [Fact]
    public void Hover_resolves_mapping_backed_pseudo_classes()
    {
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Cursorial.UI.Controls.CalendarButton).TypeHandle);
        var xaml = $"<StackPanel {Xmlns}>\n    <Style Selector=\"CalendarButton:today\"/>\n</StackPanel>";
        var line2 = "    <Style Selector=\"CalendarButton:today\"/>";
        var column = line2.IndexOf("today", StringComparison.Ordinal) + 2;
        _session.Execute(new HoverCommand { Id = 99, Xaml = xaml, Line = 2, Column = column });

        var hover = Assert.IsType<HoverInfoEvent>(_events.Last(e => e is HoverInfoEvent));
        Assert.Contains("CalendarButton.IsToday", hover.Signature);
    }

    [Fact]
    public void Hover_resolves_plain_enum_values()
    {
        var xaml = $"<StackPanel {Xmlns}>\n    <Button Visibility=\"Hidden\"/>\n</StackPanel>";
        var line2 = "    <Button Visibility=\"Hidden\"/>";
        var column = line2.IndexOf("Hidden", StringComparison.Ordinal) + 2;
        _session.Execute(new HoverCommand { Id = 89, Xaml = xaml, Line = 2, Column = column });

        var hover = Assert.IsType<HoverInfoEvent>(_events.Last(e => e is HoverInfoEvent));
        Assert.Contains("enum Visibility.Hidden", hover.Signature);
    }

    [Fact]
    public void Hover_reports_type_signature_and_doc_summary()
    {
        var xaml = $"<StackPanel {Xmlns}>\n    <Button Content=\"hi\"/>\n</StackPanel>";
        _session.Execute(new HoverCommand { Id = 82, Xaml = xaml, Line = 2, Column = 7 });

        var hover = Assert.IsType<HoverInfoEvent>(_events.Last(e => e is HoverInfoEvent));
        Assert.StartsWith("class ", hover.Signature);
        Assert.Contains("Button", hover.Signature);

        // Summary comes from the assembly's XML doc file when it ships one.
        var ui = AppDomain.CurrentDomain.GetAssemblies().First(a => a.GetName().Name == "Cursorial.UI");
        if (File.Exists(Path.ChangeExtension(ui.Location, ".xml")))
            Assert.False(string.IsNullOrWhiteSpace(hover.Summary));
    }

    [Fact]
    public void Hover_reports_member_signature_for_attributes()
    {
        var xaml = $"<StackPanel {Xmlns}>\n    <Button Content=\"hi\"/>\n</StackPanel>";
        var column = "    <Button Content".IndexOf("Content", StringComparison.Ordinal) + 3;
        _session.Execute(new HoverCommand { Id = 83, Xaml = xaml, Line = 2, Column = column });

        var hover = Assert.IsType<HoverInfoEvent>(_events.Last(e => e is HoverInfoEvent));
        Assert.Contains("Button.Content", hover.Signature);
    }

    [Fact]
    public void Hover_resolves_x_static_paths_with_values()
    {
        var xaml = $"<StackPanel {Xmlns} Background=\"{{DynamicResource {{x:Static ThemeKeys.ElevationDesktop}}}}\"/>";
        var column = xaml.IndexOf("ElevationDesktop", StringComparison.Ordinal) + 3;
        _session.Execute(new HoverCommand { Id = 84, Xaml = xaml, Line = 1, Column = column });

        var hover = Assert.IsType<HoverInfoEvent>(_events.Last(e => e is HoverInfoEvent));
        Assert.Contains("const", hover.Signature);
        Assert.Contains("ThemeKeys.ElevationDesktop", hover.Signature);
        Assert.Contains("Theme.ElevationDesktop", hover.Detail); // the resolved key value
    }

    [Fact]
    public void Definition_resolves_framework_types_to_source_via_pdb()
    {
        var xaml = $"<StackPanel {Xmlns}>\n    <Button Content=\"hi\"/>\n</StackPanel>";
        _session.Execute(new DefinitionCommand { Id = 85, Xaml = xaml, Line = 2, Column = 7 });

        var definition = Assert.IsType<DefinitionEvent>(_events.Last(e => e is DefinitionEvent));
        Assert.Equal("Button", definition.Symbol);
        Assert.NotNull(definition.File);
        Assert.EndsWith("Button.cs", definition.File);
        Assert.True(File.Exists(definition.File)); // built from the sibling checkout: PDB paths are real

        // Types have no sequence points; the host lands on the DECLARATION line, not the first
        // member body the PDB happens to mention.
        var declarationLine = File.ReadLines(definition.File!)
            .Select((text, index) => (text, line: index + 1))
            .First(l => l.text.Contains("class Button")).line;
        Assert.Equal(declarationLine, definition.Line);
    }

    [Fact]
    public void Analyze_with_unloadable_assembly_still_replies_with_diagnostics()
    {
        _session.Execute(new AnalyzeCommand
        {
            Id = 76,
            Xaml = $"<StackPanel {Xmlns}/>",
            Assemblies = ["/nonexistent/Not.There.dll"],
        });

        // The load failure is advisory (a warn log) and NEVER an ErrorEvent carrying the
        // command's reply id — that would satisfy the IDE's pending request first and the real
        // reply would find nobody waiting.
        Assert.DoesNotContain(_events, e => e is ErrorEvent { ReplyTo: 76 });
        Assert.Contains(_events, e => e is LogEvent { Level: "warn" } log && log.Message.Contains("Not.There.dll"));
        var diagnostics = Assert.IsType<DiagnosticsEvent>(Assert.Single(_events, e => e is DiagnosticsEvent));
        Assert.Equal(76, diagnostics.ReplyTo);
    }
}
