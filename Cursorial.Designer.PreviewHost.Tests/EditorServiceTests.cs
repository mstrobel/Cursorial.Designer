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
    public void Analyze_tolerates_malformed_mid_edit_documents()
    {
        _session.Execute(new AnalyzeCommand { Id = 2, Xaml = $"<StackPanel {Xmlns}>\n    <Butt" });

        var diagnostics = Assert.IsType<DiagnosticsEvent>(Assert.Single(_events, e => e is DiagnosticsEvent));
        Assert.Contains(diagnostics.Items, d => d.Severity == "error"); // malformed-XML diagnostic, not a crash
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

        var completions = Assert.IsType<CompletionsEvent>(_events.Last(e => e is CompletionsEvent));
        Assert.Contains(completions.Items, i => i is { Text: "Button.Content", Detail: "property element" });
        Assert.Contains(completions.Items, i => i.Text == "Button.Resources");
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
