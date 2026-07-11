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
}
