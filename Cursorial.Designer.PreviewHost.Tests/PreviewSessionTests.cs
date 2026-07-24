using Cursorial.Designer.PreviewHost;
using Cursorial.Designer.Protocol;
using Cursorial.Rendering;
using Cursorial.UI;

namespace Cursorial.Designer.Tests.PreviewHost;

/// <summary>A user control that loads fine but throws on its first layout pass.</summary>
public sealed class ThrowingControl : UIElement
{
    protected override Size MeasureOverride(Size availableSize)
        => throw new InvalidOperationException("designed to fail during measure");
}

/// <summary>A design-time data context for the d:DataContext round-trip test.</summary>
public sealed class DesignViewModel
{
    public string Greeting => "Hello from design data";
}

/// <summary>
/// Drives <see cref="PreviewSession"/> in-process on the test thread (which becomes the preview's
/// UI thread, exactly as the stdio loop thread does in production). Each test owns one session;
/// the harness is single-thread-affine, so sessions never overlap within a test.
/// </summary>
public class PreviewSessionTests : IDisposable
{
    private const string Xmlns = "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    private readonly List<PreviewEvent> _events = [];
    private readonly PreviewSession _session;

    public PreviewSessionTests() => _session = new PreviewSession(_events.Add);

    public void Dispose() => _session.Dispose();

    private void Initialize(int columns = 60, int rows = 16)
        => _session.Execute(new InitializeCommand { ProtocolVersion = 1, Columns = columns, Rows = rows });

    private void Load(string xaml, long? id = null, string? sourceUri = null)
        => _session.Execute(new LoadXamlCommand { Id = id, Xaml = xaml, SourceUri = sourceUri });

    private FrameEvent LastFrame() => Assert.IsType<FrameEvent>(_events.Last(e => e is FrameEvent));

    private sealed record FoldedRun(string Text, int Width, StyleInfo Style);

    /// <summary>Folds the emitted frame stream (fulls + row deltas) into the current screen state.</summary>
    private (int Columns, int Rows, List<List<FoldedRun>> Lines) Fold()
    {
        var columns = 0;
        var rows = 0;
        List<List<FoldedRun>> lines = [];
        foreach (var frame in _events.OfType<FrameEvent>())
        {
            if (frame.Delta == true)
            {
                foreach (var change in frame.Changed ?? [])
                    lines[change.Index] = change.Runs.Select(r => new FoldedRun(r.Text, r.Width, frame.Styles[r.StyleIndex])).ToList();
            }
            else
            {
                columns = frame.Columns;
                rows = frame.Rows;
                lines = frame.Lines.Select(runs => runs.Select(r => new FoldedRun(r.Text, r.Width, frame.Styles[r.StyleIndex])).ToList()).ToList();
            }
        }

        return (columns, rows, lines);
    }

    private string FrameText()
        => string.Join('\n', Fold().Lines.Select(runs => string.Concat(runs.Select(r => r.Text))));

    [Fact]
    public void Space_reaches_text_input_as_a_character()
    {
        // Terminal parity: plain space arrives as Key.Character ' ' (Key.Space exists only for
        // Ctrl+Space). Sending the named key meant buttons still pressed (they accept both
        // forms) while TextBox text input silently dropped every space.
        Initialize();
        Load($"""<StackPanel {Xmlns}><TextBox x:Name="Input"/></StackPanel>""");

        _session.Execute(new PointerCommand { Kind = "down", Column = 2, Row = 0 });
        _session.Execute(new PointerCommand { Kind = "up", Column = 2, Row = 0 });
        _session.Execute(new KeyCommand { Key = "a" });
        _session.Execute(new KeyCommand { Key = "Space" });
        _session.Execute(new KeyCommand { Key = "b" });

        _session.Execute(new HitTestCommand { Id = 91, Column = 2, Row = 0 });
        var hit = Assert.IsType<HitTestResultEvent>(_events.Last(e => e is HitTestResultEvent));
        var textBoxId = Assert.Single(hit.Elements, e => e.ElementType == "TextBox").ElementId;

        _session.Execute(new GetPropertiesCommand { Id = 92, ElementId = textBoxId });
        var properties = Assert.IsType<PropertiesEvent>(_events.Last(e => e is PropertiesEvent));
        var text = Assert.Single(properties.Items, p => p.Name == "Text");
        Assert.Equal("a b", text.Value);
    }

    [Fact]
    public void Load_window_root_shows_through_the_window_manager()
    {
        Initialize();
        Load($"""
              <Window {Xmlns} Width="40" Height="10">
                  <TextBlock Text="window content"/>
              </Window>
              """);

        Assert.DoesNotContain(_events, e => e is ErrorEvent);
        Assert.Contains("window content", FrameText());

        // And a reload swaps the window cleanly. The second window is OFFSET and smaller so a
        // zombie surface from the first document would still peek out of the composite —
        // painting the same rect over it would mask the leak.
        Load($"""
              <Window {Xmlns} Left="10" Top="3" Width="26" Height="8">
                  <TextBlock Text="second revision"/>
              </Window>
              """);
        Assert.DoesNotContain(_events, e => e is ErrorEvent);
        var text = FrameText();
        Assert.Contains("second revision", text);
        Assert.DoesNotContain("window content", text);

        // And switching back to a plain panel root works too.
        Load($"""<StackPanel {Xmlns}><TextBlock Text="plain again"/></StackPanel>""");
        Assert.DoesNotContain(_events, e => e is ErrorEvent);
        var final = FrameText();
        Assert.Contains("plain again", final);
        Assert.DoesNotContain("second revision", final);
    }

    [Fact]
    public void Initialize_emits_a_frame_of_the_requested_size()
    {
        Initialize(columns: 42, rows: 7);

        var frame = LastFrame();
        Assert.Equal(42, frame.Columns);
        Assert.Equal(7, frame.Rows);
        Assert.Equal(7, frame.Lines.Count);
        Assert.All(frame.Lines, runs => Assert.Equal(42, runs.Sum(r => r.Width)));
    }

    [Fact]
    public void Initialize_paints_the_themed_desktop_background()
    {
        Initialize();

        // Loaded roots sit in a chrome Border backed by ThemeKeys.ElevationDesktop, so even the
        // empty preview renders the theme's desktop fill rather than terminal-default cells.
        var frame = LastFrame();
        Assert.Contains(frame.Styles, s => s.Bg is not null);
    }

    [Fact]
    public void HitTest_on_empty_desktop_returns_no_user_elements()
    {
        Initialize();
        Load($"""<StackPanel {Xmlns} HorizontalAlignment="Left" VerticalAlignment="Top"><TextBlock Text="x"/></StackPanel>""");

        // Bottom-right corner: preview chrome only — the container Border is not user content.
        _session.Execute(new HitTestCommand { Id = 4, Column = 59, Row = 15 });

        var result = Assert.IsType<HitTestResultEvent>(_events.Last(e => e is HitTestResultEvent));
        Assert.Empty(result.Elements);
    }

    [Fact]
    public void Commands_before_initialize_degrade_gracefully()
    {
        // Transient input racing a (re)initialize drops with a debug log — the user clicked
        // while the host restarted; killing the fresh session over it is the wrong trade.
        _session.Execute(new ResizeCommand { Columns = 10, Rows = 5 });
        _session.Execute(new PointerCommand { Kind = "press", Column = 1, Row = 1 });
        Assert.DoesNotContain(_events, e => e is ErrorEvent);

        // Queries answer with a REPLY-BEARING error so the plugin's correlation resolves
        // instead of waiting out its timeout.
        _session.Execute(new HitTestCommand { Id = 7, Column = 0, Row = 0 });
        var error = Assert.IsType<ErrorEvent>(_events.Last(e => e is ErrorEvent));
        Assert.Equal(7, error.ReplyTo);
        Assert.Contains("initialize", error.Message);

        // State commands still fail loudly — loading into no session is a programming error.
        var ex = Assert.Throws<InvalidOperationException>(
            () => _session.Execute(new LoadXamlCommand { Xaml = "<x/>" }));
        Assert.Contains("initialize", ex.Message);
    }

    [Fact]
    public void LoadXaml_renders_real_content_through_the_real_pipeline()
    {
        Initialize();
        Load($"""
              <StackPanel {Xmlns}>
                  <TextBlock Text="Hello Designer"/>
                  <Button Content="Press Me"/>
              </StackPanel>
              """, id: 5);

        var diagnostics = Assert.IsType<DiagnosticsEvent>(Assert.Single(_events, e => e is DiagnosticsEvent));
        Assert.Equal(5, diagnostics.ReplyTo);
        Assert.Empty(diagnostics.Items);

        var text = FrameText();
        Assert.Contains("Hello Designer", text);
        Assert.Contains("Press Me", text);
    }

    [Fact]
    public void LoadXaml_reports_diagnostics_with_positions_for_bad_markup()
    {
        Initialize();
        Load($"""
              <StackPanel {Xmlns}>
                  <NoSuchControl Text="oops"/>
              </StackPanel>
              """);

        var diagnostics = Assert.IsType<DiagnosticsEvent>(Assert.Single(_events, e => e is DiagnosticsEvent));
        var error = Assert.Single(diagnostics.Items, d => d.Severity == "error");
        Assert.StartsWith("CUR", error.Code);
        Assert.True(error.Line >= 1);
        Assert.True(error.Column >= 1);
    }

    [Fact]
    public void Failed_load_keeps_the_previous_content()
    {
        Initialize();
        Load($"""<StackPanel {Xmlns}><TextBlock Text="Survivor"/></StackPanel>""");
        var framesBefore = _events.Count(e => e is FrameEvent);

        Load($"""<StackPanel {Xmlns}><Broken/></StackPanel>""");

        Assert.Equal(framesBefore, _events.Count(e => e is FrameEvent)); // no new frame for the bad load
        Assert.Contains("Survivor", FrameText());
    }

    [Fact]
    public void Resize_reflows_and_emits_the_new_geometry()
    {
        Initialize(columns: 60, rows: 16);
        Load($"""<StackPanel {Xmlns}><TextBlock Text="resize me"/></StackPanel>""");

        _session.Execute(new ResizeCommand { Columns = 100, Rows = 30 });

        var folded = Fold();
        Assert.Equal(100, folded.Columns);
        Assert.Equal(30, folded.Rows);
        Assert.Contains("resize me", FrameText());
    }

    [Fact]
    public void HitTest_returns_the_element_chain_with_screen_bounds()
    {
        Initialize();
        Load($"""
              <StackPanel {Xmlns}>
                  <TextBlock x:Name="Title" Text="Click here"/>
              </StackPanel>
              """);

        // Row 0 starts with the TextBlock's text at the panel's top-left.
        _session.Execute(new HitTestCommand { Id = 9, Column = 1, Row = 0 });

        var result = Assert.IsType<HitTestResultEvent>(_events.Last(e => e is HitTestResultEvent));
        Assert.Equal(9, result.ReplyTo);
        Assert.NotEmpty(result.Elements);

        var innermost = result.Elements[0];
        Assert.Equal("TextBlock", innermost.ElementType);
        Assert.Equal("Title", innermost.Name);
        Assert.True(innermost.Bounds.Columns > 0);
        Assert.True(innermost.Bounds.Rows > 0);
        Assert.Equal(0, innermost.Bounds.Row);

        // The chain walks parents up to the root.
        Assert.Contains(result.Elements, e => e.ElementType == "StackPanel");
    }

    [Fact]
    public void HitTest_carries_source_positions_for_document_elements()
    {
        Initialize();
        Load($"""
              <StackPanel {Xmlns}>
                  <TextBlock x:Name="First" Text="line two"/>
                  <Button Content="line three" HorizontalAlignment="Left"/>
              </StackPanel>
              """, sourceUri: "file:///test/View.xaml");

        // The TextBlock renders on the first row; its tag sits on line 2 of the document.
        _session.Execute(new HitTestCommand { Id = 41, Column = 1, Row = 0 });
        var hit = Assert.IsType<HitTestResultEvent>(_events.Last(e => e is HitTestResultEvent));

        var text = hit.Elements[0];
        Assert.Equal("TextBlock", text.ElementType);
        Assert.Equal(2, text.Line);
        Assert.True(text.Column > 1);
        Assert.Equal("file:///test/View.xaml", text.SourceUri);
        Assert.True(text.InDocument);

        // The chain's outermost element is the root StackPanel — line 1.
        var root = hit.Elements[^1];
        Assert.Equal("StackPanel", root.ElementType);
        Assert.Equal(1, root.Line);
        Assert.True(root.InDocument);

        // Clicking the Button hits its template internals first: those come from the code-first
        // theme (no XAML span), so they carry no source info — the provenance ladder's fallback
        // case — while the Button itself (further up the chain) is document-owned.
        _session.Execute(new HitTestCommand { Id = 42, Column = 3, Row = 1 });
        var buttonHit = Assert.IsType<HitTestResultEvent>(_events.Last(e => e is HitTestResultEvent));
        var button = Assert.Single(buttonHit.Elements, e => e.ElementType == "Button");
        Assert.Equal(3, button.Line);
        Assert.True(button.InDocument);
        if (buttonHit.Elements[0].ElementType != "Button")
            Assert.Null(buttonHit.Elements[0].InDocument); // template part: untracked span
    }

    [Fact]
    public void GetChildren_descends_below_the_hit_test_anchor()
    {
        Initialize();
        Load($"""
              <StackPanel {Xmlns}>
                  <TextBlock Text="one"/>
                  <Button Content="two" HorizontalAlignment="Left"/>
              </StackPanel>
              """, sourceUri: "file:///test/Children.xaml");

        _session.Execute(new HitTestCommand { Id = 51, Column = 1, Row = 0 });
        var hit = Assert.IsType<HitTestResultEvent>(_events.Last(e => e is HitTestResultEvent));
        var rootId = hit.Elements[^1].ElementId; // the StackPanel

        _session.Execute(new GetChildrenCommand { Id = 52, ElementId = rootId });

        var children = Assert.IsType<ChildrenEvent>(_events.Last(e => e is ChildrenEvent));
        Assert.Equal(52, children.ReplyTo);
        Assert.Equal(rootId, children.ParentId);
        Assert.Equal(2, children.Elements.Count);
        Assert.Equal("TextBlock", children.Elements[0].ElementType);
        Assert.Equal("Button", children.Elements[1].ElementType);
        Assert.True(children.Elements[0].InDocument);
        Assert.Equal(2, children.Elements[0].Line);
    }

    [Fact]
    public void GetChildren_with_stale_id_reports_an_error()
    {
        Initialize();
        _session.Execute(new GetChildrenCommand { Id = 53, ElementId = 999 });
        Assert.Equal(53, Assert.IsType<ErrorEvent>(_events.Last(e => e is ErrorEvent)).ReplyTo);
    }

    [Fact]
    public void GetProperties_reports_set_values_with_provenance()
    {
        Initialize();
        Load($"""
              <StackPanel {Xmlns}>
                  <TextBlock x:Name="Title" Text="Inspect me"/>
              </StackPanel>
              """);

        _session.Execute(new HitTestCommand { Id = 1, Column = 1, Row = 0 });
        var hit = Assert.IsType<HitTestResultEvent>(_events.Last(e => e is HitTestResultEvent));

        _session.Execute(new GetPropertiesCommand { Id = 2, ElementId = hit.Elements[0].ElementId });

        var properties = Assert.IsType<PropertiesEvent>(_events.Last(e => e is PropertiesEvent));
        Assert.Equal(2, properties.ReplyTo);
        var text = Assert.Single(properties.Items, p => p.Name == "Text");
        Assert.Equal("Inspect me", text.Value);
        Assert.Equal("Local", text.ValueSource);

        // Layout facts lead the grid — not UIProperties, but the numbers every question starts from.
        Assert.Equal("DesiredSize", properties.Items[0].Name);
        Assert.Equal("Bounds", properties.Items[1].Name);
        Assert.All(properties.Items.Take(2), p => Assert.Equal("Layout", p.ValueSource));
        Assert.All(properties.Items.Take(2), p => Assert.False(string.IsNullOrEmpty(p.Value)));
    }

    [Fact]
    public void LoadXaml_resolves_relative_resource_dictionaries_next_to_the_document()
    {
        // The Cursorial.Samples regression (2026-07-12): a document on disk links a sibling
        // dictionary with a RELATIVE Source. Resolution walks document URI → file provider.
        var dir = Directory.CreateTempSubdirectory("cursorial-designer-test-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "Res.xaml"),
                $"""<ResourceDictionary {Xmlns}><x:String x:Key="Probe">from-disk</x:String></ResourceDictionary>""");

            Initialize();
            var main = Path.Combine(dir.FullName, "Main.xaml");
            var xaml = $$"""
                        <StackPanel {{Xmlns}}>
                            <StackPanel.Resources>
                                <ResourceDictionary Source="Res.xaml"/>
                            </StackPanel.Resources>
                            <TextBlock Text="{StaticResource Probe}"/>
                        </StackPanel>
                        """;
            _session.Execute(new LoadXamlCommand { Id = 31, Xaml = xaml, SourceUri = new Uri(main).AbsoluteUri });

            var diagnostics = Assert.IsType<DiagnosticsEvent>(_events.Last(e => e is DiagnosticsEvent));
            Assert.DoesNotContain(diagnostics.Items, d => d.Severity == "error");
            Assert.Contains("from-disk", Fold().Lines.SelectMany(l => l).Select(r => r.Text).Aggregate((a, b) => a + b));

            // The consumed files report as the IDE's reload-watch list.
            var dependencies = Assert.IsType<DependenciesEvent>(_events.Last(e => e is DependenciesEvent));
            Assert.Equal(31, dependencies.ReplyTo);
            Assert.Contains(dependencies.Files, f => f.EndsWith("Res.xaml", StringComparison.Ordinal));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void DescribeElement_refreshes_bounds_after_resize()
    {
        Initialize();
        Load($"""<DockPanel {Xmlns}><Button Content="X" HorizontalAlignment="Right" VerticalAlignment="Top"/></DockPanel>""");

        _session.Execute(new HitTestCommand { Id = 21, Column = 58, Row = 0 });
        var hit = Assert.IsType<HitTestResultEvent>(_events.Last(e => e is HitTestResultEvent));
        var button = Assert.Single(hit.Elements, e => e.ElementType == "Button");

        _session.Execute(new ResizeCommand { Columns = 40, Rows = 16 });
        _session.Execute(new DescribeElementCommand { Id = 22, ElementId = button.ElementId });

        // Same identity, fresh geometry: the right-aligned button followed the narrower surface.
        var described = Assert.IsType<HitTestResultEvent>(_events.Last(e => e is HitTestResultEvent));
        Assert.Equal(22, described.ReplyTo);
        var refreshed = Assert.Single(described.Elements, e => e.ElementType == "Button");
        Assert.Equal(button.ElementId, refreshed.ElementId);
        Assert.True(refreshed.Bounds.Column < button.Bounds.Column,
            $"expected the button to move left of {button.Bounds.Column}, got {refreshed.Bounds.Column}");
    }

    [Fact]
    public void Ansi_palette_pairs_with_the_theme_base()
    {
        // xterm's dark-base "white" (#e5e5e5) vanishes on a light background; the light base
        // resolves ANSI indices through a light-terminal palette instead. Cube + grayscale
        // entries are absolute on both bases.
        Assert.Equal("#e5e5e5", XtermPalette.ToHex(7));
        Assert.Equal("#555555", XtermPalette.ToHex(7, lightBase: true));
        Assert.Equal("#ffffff", XtermPalette.ToHex(15));
        Assert.Equal("#a5a5a5", XtermPalette.ToHex(15, lightBase: true));
        Assert.Equal(XtermPalette.ToHex(196), XtermPalette.ToHex(196, lightBase: true)); // cube
        Assert.Equal(XtermPalette.ToHex(244), XtermPalette.ToHex(244, lightBase: true)); // gray ramp
    }

    [Fact]
    public void Resize_clamps_to_the_roots_minimum_size()
    {
        Initialize();
        Load($"""<StackPanel {Xmlns} MinWidth="50" MinHeight="10"><TextBlock Text="wide"/></StackPanel>""");

        _session.Execute(new ResizeCommand { Columns = 30, Rows = 8 });

        // The frame reports the ACTUAL surface size — the IDE scrolls the overflow.
        var frame = Assert.IsType<FrameEvent>(_events.Last(e => e is FrameEvent));
        Assert.Equal(50, frame.Columns);
        Assert.Equal(10, frame.Rows);
    }

    [Fact]
    public void GetProperties_includes_defaults_on_request()
    {
        Initialize();
        Load($"""
              <StackPanel {Xmlns}>
                  <TextBlock x:Name="Title" Text="Inspect me"/>
              </StackPanel>
              """);

        _session.Execute(new HitTestCommand { Id = 11, Column = 1, Row = 0 });
        var hit = Assert.IsType<HitTestResultEvent>(_events.Last(e => e is HitTestResultEvent));
        var id = hit.Elements[0].ElementId;

        _session.Execute(new GetPropertiesCommand { Id = 12, ElementId = id });
        var withoutDefaults = Assert.IsType<PropertiesEvent>(_events.Last(e => e is PropertiesEvent));
        Assert.DoesNotContain(withoutDefaults.Items, p => p.ValueSource == "Default");

        _session.Execute(new GetPropertiesCommand { Id = 13, ElementId = id, IncludeDefaults = true });
        var withDefaults = Assert.IsType<PropertiesEvent>(_events.Last(e => e is PropertiesEvent));
        Assert.Contains(withDefaults.Items, p => p.ValueSource == "Default");
        // The set/inherited rows still lead, and nothing appears twice.
        Assert.Contains(withDefaults.Items, p => p.Name == "Text" && p.ValueSource == "Local");
        Assert.Equal(withDefaults.Items.Count, withDefaults.Items.Select(p => $"{p.DeclaringType}.{p.Name}").Distinct().Count());

        // A declared member no theme touches sits in the default lane, unqualified. (The
        // AddOwner-surfacing that motivated this sweep — TextBlock.Foreground — is pinned in
        // the framework's UIPropertiesTests; its lane here shifts with theme styling.)
        var visibility = Assert.Single(withDefaults.Items, p => p.Name == "Visibility");
        Assert.Null(visibility.DeclaringType);
        Assert.Equal("Default", visibility.ValueSource);

        // Theme-reactive defaults annotate the key they resolve through.
        var foreground = Assert.Single(withDefaults.Items, p => p.Name == "Foreground");
        Assert.Equal("Default", foreground.ValueSource);
        Assert.Equal("Theme.TextBrush", foreground.ResourceKey);
    }

    [Fact]
    public void GetProperties_reports_inherited_values_without_local_entries()
    {
        Initialize();
        Load($"""
              <StackPanel {Xmlns} Icon.IconBrush="Red">
                  <TextBlock x:Name="Child" Text="inheriting"/>
              </StackPanel>
              """);

        _session.Execute(new HitTestCommand { Id = 81, Column = 1, Row = 0 });
        var hit = Assert.IsType<HitTestResultEvent>(_events.Last(e => e is HitTestResultEvent));
        var textId = Assert.Single(hit.Elements, e => e.ElementType == "TextBlock").ElementId;

        _session.Execute(new GetPropertiesCommand { Id = 82, ElementId = textId });
        var properties = Assert.IsType<PropertiesEvent>(_events.Last(e => e is PropertiesEvent));

        // The child never sets IconBrush; the value flows from the ancestor. GetSetProperties
        // excludes inherited-only contributions by design — the registry's inheriting set
        // (UIProperties.Inheriting, framework PR #17) is how the inspector knows to ask.
        // (IconBrush rather than Foreground: no theme touches it, so the lane is stable.)
        var attrs = Assert.Single(properties.Items, p => p.Name == "IconBrush");
        Assert.Equal("Inherited", attrs.ValueSource);
        // True attached usage (no TextBlock AddOwner) keeps the owner qualification.
        Assert.Equal("Icon", attrs.DeclaringType);
    }

    [Fact]
    public void GetProperties_includes_style_frames_for_styled_values()
    {
        Initialize();
        Load($"""<StackPanel {Xmlns}><Button Content="styled" HorizontalAlignment="Left"/></StackPanel>""");

        _session.Execute(new HitTestCommand { Id = 61, Column = 3, Row = 0 });
        var hit = Assert.IsType<HitTestResultEvent>(_events.Last(e => e is HitTestResultEvent));
        var buttonId = Assert.Single(hit.Elements, e => e.ElementType == "Button").ElementId;

        _session.Execute(new GetPropertiesCommand { Id = 62, ElementId = buttonId });
        var properties = Assert.IsType<PropertiesEvent>(_events.Last(e => e is PropertiesEvent));

        // The built-in theme styles buttons richly (Background, Foreground, Padding, …), so
        // styled properties must carry the full frame breakdown: layer + selector + status.
        var styled = properties.Items.First(p => p.Frames is { Count: > 0 } && p.ValueSource == "StyleSetter");
        var frame = styled.Frames![0];
        Assert.False(string.IsNullOrEmpty(frame.Layer));
        Assert.False(string.IsNullOrEmpty(frame.Status));
        Assert.NotNull(styled.Priority);
    }

    [Fact]
    public void GetProperties_includes_binding_expressions_for_bound_values()
    {
        Initialize();
        var ns = "clr-namespace:Cursorial.Designer.Tests.PreviewHost;assembly=Cursorial.Designer.PreviewHost.Tests";
        _session.Execute(new LoadXamlCommand
        {
            Xaml = $$"""
                     <StackPanel {{Xmlns}}
                                 xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
                                 xmlns:t="{{ns}}"
                                 d:DataContext="t:DesignViewModel">
                         <TextBlock Text="{Binding Greeting}"/>
                     </StackPanel>
                     """,
            Assemblies = [typeof(DesignViewModel).Assembly.Location],
        });

        _session.Execute(new HitTestCommand { Id = 71, Column = 1, Row = 0 });
        var hit = Assert.IsType<HitTestResultEvent>(_events.Last(e => e is HitTestResultEvent));
        var textId = Assert.Single(hit.Elements, e => e.ElementType == "TextBlock").ElementId;

        _session.Execute(new GetPropertiesCommand { Id = 72, ElementId = textId });
        var properties = Assert.IsType<PropertiesEvent>(_events.Last(e => e is PropertiesEvent));

        var text = Assert.Single(properties.Items, p => p.Name == "Text");
        Assert.NotNull(text.BindingTarget);
        var expression = Assert.Single(text.Bindings!);
        Assert.Equal("Greeting", expression.Path);
        Assert.Equal("Hello from design data", expression.Value);
        Assert.Null(expression.LastFailure);
        Assert.False(string.IsNullOrEmpty(expression.EffectiveMode));
    }

    [Fact]
    public void SampleCell_reports_every_layer_with_style_and_parameters()
    {
        Initialize();
        Load($"""<StackPanel {Xmlns}><TextBlock Text="sampled"/></StackPanel>""");

        // (1, 0) is inside the TextBlock's text — the root surface must contribute a glyph.
        _session.Execute(new SampleCellCommand { Id = 81, Column = 1, Row = 0 });

        var samples = Assert.IsType<CellSamplesEvent>(_events.Last(e => e is CellSamplesEvent));
        Assert.Equal(81, samples.ReplyTo);
        Assert.Equal(1, samples.Column);
        Assert.NotEmpty(samples.Layers);

        var bottom = samples.Layers[0];
        Assert.Equal("a", bottom.Grapheme); // "sampled"[1]
        Assert.Equal("Single", bottom.Kind);
        Assert.NotNull(bottom.Style);
        Assert.Equal(255, bottom.Parameters.Opacity);
        Assert.False(string.IsNullOrEmpty(bottom.Element));
    }

    [Fact]
    public void GetProperties_with_stale_id_reports_an_error()
    {
        Initialize();
        _session.Execute(new GetPropertiesCommand { Id = 3, ElementId = 999 });

        var error = Assert.IsType<ErrorEvent>(_events.Last(e => e is ErrorEvent));
        Assert.Equal(3, error.ReplyTo);
    }

    [Fact]
    public void SetTheme_flips_variant_and_rerenders()
    {
        Initialize();
        Load($"""<StackPanel {Xmlns}><Button Content="Theme probe"/></StackPanel>""");
        var darkStyles = Fold().Lines.SelectMany(r => r).Select(r => (r.Style.Fg, r.Style.Bg)).ToHashSet();

        _session.Execute(new SetThemeCommand { ThemeBase = "light" });

        Assert.Contains("Theme probe", FrameText());
        var lightStyles = Fold().Lines.SelectMany(r => r).Select(r => (r.Style.Fg, r.Style.Bg)).ToHashSet();
        Assert.NotEqual(darkStyles, lightStyles);
    }

    [Fact]
    public void Content_that_throws_during_layout_rolls_back_and_the_session_survives()
    {
        Initialize();
        Load($"""<StackPanel {Xmlns}><TextBlock Text="Survivor"/></StackPanel>""");

        var ns = "clr-namespace:Cursorial.Designer.Tests.PreviewHost;assembly=Cursorial.Designer.PreviewHost.Tests";
        _session.Execute(new LoadXamlCommand
        {
            Id = 7,
            Xaml = $"""<StackPanel {Xmlns} xmlns:t="{ns}"><t:ThrowingControl/></StackPanel>""",
            Assemblies = [typeof(ThrowingControl).Assembly.Location],
        });

        var error = Assert.IsType<ErrorEvent>(_events.Last(e => e is ErrorEvent));
        Assert.Equal(7, error.ReplyTo);
        Assert.Contains("reverted", error.Message);
        Assert.Contains("Survivor", FrameText());

        // The session must remain fully functional after the broken document.
        _session.Execute(new ResizeCommand { Columns = 80, Rows = 20 });
        Assert.Equal(80, Fold().Columns);
        Assert.Contains("Survivor", FrameText());
    }

    [Fact]
    public void Reload_invalidates_element_ids()
    {
        Initialize();
        Load($"""<StackPanel {Xmlns}><TextBlock x:Name="A" Text="first"/></StackPanel>""");
        _session.Execute(new HitTestCommand { Id = 1, Column = 1, Row = 0 });
        var id = Assert.IsType<HitTestResultEvent>(_events.Last(e => e is HitTestResultEvent)).Elements[0].ElementId;

        Load($"""<StackPanel {Xmlns}><TextBlock x:Name="B" Text="second"/></StackPanel>""");
        _session.Execute(new GetPropertiesCommand { Id = 2, ElementId = id });

        var error = Assert.IsType<ErrorEvent>(_events.Last(e => e is ErrorEvent));
        Assert.Equal(2, error.ReplyTo);
    }

    [Fact]
    public void AdvanceTime_steps_the_clock_and_emits_a_frame()
    {
        Initialize();
        Load($"""<StackPanel {Xmlns}><TextBlock Text="tick"/></StackPanel>""");
        var frames = _events.Count(e => e is FrameEvent);

        // Static content: advancing the clock changes nothing, so nothing is emitted — the
        // whole point of no-change suppression for play mode.
        _session.Execute(new AdvanceTimeCommand { Milliseconds = 100 });
        Assert.Equal(frames, _events.Count(e => e is FrameEvent));
    }

    [Fact]
    public void Error_events_echo_the_command_id()
    {
        Initialize();
        _session.Execute(new KeyCommand { Id = 11, Key = "NotARealKey" });
        Assert.Equal(11, Assert.IsType<ErrorEvent>(_events.Last(e => e is ErrorEvent)).ReplyTo);

        _session.Execute(new KeyCommand { Id = 12, Key = "Enter", Modifiers = ["hyperdrive"] });
        Assert.Equal(12, Assert.IsType<ErrorEvent>(_events.Last(e => e is ErrorEvent)).ReplyTo);

        _session.Execute(new PointerCommand { Id = 13, Kind = "down", Column = 0, Row = 0, Button = "chorded" });
        Assert.Equal(13, Assert.IsType<ErrorEvent>(_events.Last(e => e is ErrorEvent)).ReplyTo);

        _session.Execute(new PointerCommand { Id = 14, Kind = "down", Column = 0, Row = 0, Modifiers = ["hyperdrive"] });
        Assert.Equal(14, Assert.IsType<ErrorEvent>(_events.Last(e => e is ErrorEvent)).ReplyTo);
    }

    [Fact]
    public void Ctrl_click_toggles_multi_select_via_forwarded_modifier()
    {
        // A terminal can't read ambient modifier state, so the previewer snapshots it onto the pointer
        // command and the host applies it. End-to-end proof: in Multiple mode a plain click SELECTS an item
        // and a Ctrl+click on that same item TOGGLES it back off — IsSelected reverting to false is only
        // possible if the "ctrl" modifier crossed the wire and was applied to the injected mouse event.
        Initialize();
        Load($"""
            <ListBox {Xmlns} SelectionMode="Multiple">
              <ListBoxItem Content="One"/>
              <ListBoxItem Content="Two"/>
            </ListBox>
            """);

        ClickItem(ctrl: false);                                 // plain click selects item 0
        Assert.True(ItemIsSelected(), "a plain click should select the item");

        ClickItem(ctrl: true);                                  // Ctrl+click the same item toggles it off
        Assert.False(ItemIsSelected(), "a Ctrl+click should toggle the item back off — the modifier was applied");

        void ClickItem(bool ctrl)
        {
            var modifiers = ctrl ? new[] { "ctrl" } : System.Array.Empty<string>();
            var (col, row) = ItemCell();
            _session.Execute(new PointerCommand { Kind = "down", Column = col, Row = row, Modifiers = modifiers });
            _session.Execute(new PointerCommand { Kind = "up", Column = col, Row = row, Modifiers = modifiers });
        }
    }

    // The first cell (scanning the top rows) that hit-tests onto a ListBoxItem — robust to the ListBox's
    // border/padding so the click lands on a real item container rather than a guessed coordinate.
    private (int Column, int Row) ItemCell()
    {
        for (var row = 0; row < 8; row++)
        for (var col = 0; col < 20; col++)
        {
            _session.Execute(new HitTestCommand { Column = col, Row = row });
            var hit = Assert.IsType<HitTestResultEvent>(_events.Last(e => e is HitTestResultEvent));
            if (hit.Elements.Any(e => e.ElementType == "ListBoxItem"))
                return (col, row);
        }

        throw new Xunit.Sdk.XunitException("No ListBoxItem was found in the rendered frame.");
    }

    // Whether the first item container reports ListBoxItem.IsSelected (a StyledProperty the inspector
    // surfaces; IncludeDefaults so the toggled-off `False` still appears rather than dropping out).
    private bool ItemIsSelected()
    {
        var (col, row) = ItemCell();
        _session.Execute(new HitTestCommand { Column = col, Row = row });
        var hit = Assert.IsType<HitTestResultEvent>(_events.Last(e => e is HitTestResultEvent));
        var itemId = hit.Elements.First(e => e.ElementType == "ListBoxItem").ElementId;
        _session.Execute(new GetPropertiesCommand { ElementId = itemId, IncludeDefaults = true });
        var props = Assert.IsType<PropertiesEvent>(_events.Last(e => e is PropertiesEvent));
        return props.Items.FirstOrDefault(p => p.Name == "IsSelected")?.Value == "True";
    }

    [Fact]
    public void Protocol_version_mismatch_is_rejected()
    {
        _session.Execute(new InitializeCommand { Id = 1, ProtocolVersion = 99, Columns = 10, Rows = 5 });

        var error = Assert.IsType<ErrorEvent>(_events.Last(e => e is ErrorEvent));
        Assert.Equal(1, error.ReplyTo);
        Assert.Contains("version", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_events, e => e is FrameEvent);
    }

    [Fact]
    public void Unknown_capability_profile_falls_back_with_an_error()
    {
        _session.Execute(new InitializeCommand { Id = 2, ProtocolVersion = 1, Columns = 20, Rows = 5, Capabilities = "vt52" });

        Assert.Contains(_events, e => e is ErrorEvent { ReplyTo: 2 });
        Assert.Equal(20, LastFrame().Columns); // session still initialized on the default profile
    }

    [Fact]
    public void Text_command_types_into_focused_content()
    {
        Initialize();
        Load($"""<StackPanel {Xmlns}><TextBox x:Name="Input" Width="20"/></StackPanel>""");

        _session.Execute(new PointerCommand { Kind = "down", Column = 2, Row = 0 });
        _session.Execute(new PointerCommand { Kind = "up", Column = 2, Row = 0 });
        _session.Execute(new TextCommand { Text = "hi" });

        Assert.Contains("hi", FrameText());
    }

    [Fact]
    public void Ansi16_profile_quantizes_wire_colors_to_the_16_color_palette()
    {
        _session.Execute(new InitializeCommand { ProtocolVersion = 1, Columns = 60, Rows = 16, Capabilities = "ansi16" });
        Load($"""<StackPanel {Xmlns}><Button Content="tiered"/><TextBlock Text="palette"/></StackPanel>""");

        var allowed = Enumerable.Range(0, 16).Select(i => XtermPalette.ToHex((byte)i)).ToHashSet();
        Assert.All(Fold().Lines.SelectMany(r => r), run =>
        {
            if (run.Style.Fg is not null)
                Assert.Contains(run.Style.Fg, allowed);
            if (run.Style.Bg is not null)
                Assert.Contains(run.Style.Bg, allowed);
        });
    }

    [Fact]
    public void Window_rooted_documents_render_undimmed()
    {
        Initialize();
        Load($"""<Window {Xmlns} Width="40" Height="10"><TextBlock Text="windowed"/></Window>""");

        Assert.Contains("windowed", FrameText());
    }

    [Fact]
    public void Key_down_holds_the_pressed_state_until_key_up()
    {
        Initialize();
        Load($"""<StackPanel {Xmlns}><Button x:Name="Hold" Content="Hold me" HorizontalAlignment="Left"/></StackPanel>""");

        // Focus the button, then hold Space: pressed visuals must persist across the down and
        // clear on the up — not flash for a single frame like a synthesized press.
        _session.Execute(new PointerCommand { Kind = "down", Column = 2, Row = 0 });
        _session.Execute(new PointerCommand { Kind = "up", Column = 2, Row = 0 });
        var idle = FrameStyleSignature();

        _session.Execute(new KeyCommand { Key = "Space", Kind = "down" });
        var held = FrameStyleSignature();
        Assert.NotEqual(idle, held);

        _session.Execute(new KeyCommand { Key = "Space", Kind = "down" }); // auto-repeat: stays pressed, no error
        Assert.Equal(held, FrameStyleSignature());

        _session.Execute(new KeyCommand { Key = "Space", Kind = "up" });
        Assert.Equal(idle, FrameStyleSignature());
        Assert.DoesNotContain(_events, e => e is ErrorEvent);
    }

    [Fact]
    public void Design_time_metadata_sizes_the_root_and_binds_design_data()
    {
        Initialize(columns: 60, rows: 16);
        var ns = "clr-namespace:Cursorial.Designer.Tests.PreviewHost;assembly=Cursorial.Designer.PreviewHost.Tests";
        _session.Execute(new LoadXamlCommand
        {
            Id = 31,
            Xaml = $$"""
                     <StackPanel {{Xmlns}}
                                 xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
                                 xmlns:t="{{ns}}"
                                 d:DesignWidth="30" d:DesignHeight="5"
                                 d:DataContext="t:DesignViewModel">
                         <TextBlock Text="{Binding Greeting}"/>
                     </StackPanel>
                     """,
            Assemblies = [typeof(DesignViewModel).Assembly.Location],
        });

        var diagnostics = Assert.IsType<DiagnosticsEvent>(Assert.Single(_events, e => e is DiagnosticsEvent));
        Assert.Empty(diagnostics.Items);

        // d:DataContext constructed the viewmodel, so the binding renders real design data.
        Assert.Contains("Hello from design data", FrameText());

        // d:DesignWidth/Height constrain the root: hit the surface center and read the
        // outermost user element's bounds (the chrome container is never reported).
        _session.Execute(new HitTestCommand { Id = 32, Column = 30, Row = 7 });
        var hit = Assert.IsType<HitTestResultEvent>(_events.Last(e => e is HitTestResultEvent));
        Assert.NotEmpty(hit.Elements);
        var root = hit.Elements[^1];
        Assert.Equal("StackPanel", root.ElementType);
        Assert.Equal(30, root.Bounds.Columns);
        Assert.Equal(5, root.Bounds.Rows);
    }

    [Fact]
    public void Bars_controls_resolve_and_render_out_of_the_box()
    {
        Initialize();
        Load($"""
              <StackPanel {Xmlns}>
                  <Toolbar>
                      <BarButton Content="Save"/>
                  </Toolbar>
              </StackPanel>
              """, id: 21);

        var diagnostics = Assert.IsType<DiagnosticsEvent>(Assert.Single(_events, e => e is DiagnosticsEvent));
        Assert.Empty(diagnostics.Items);
        Assert.Contains("Save", FrameText());
    }

    [Fact]
    public void Alt_down_shows_access_key_underlines_until_alt_up()
    {
        Initialize();
        Load($"""<StackPanel {Xmlns}><Button Content="_Cancel" HorizontalAlignment="Left"/></StackPanel>""");

        // The theme may use underline of its own, so assert on the *count* of underlined cells:
        // the access-key cue must add underlines on Alt down and remove them on Alt up.
        int UnderlinedCells()
            => Fold().Lines.Sum(runs => runs.Where(r => r.Style.Attrs?.Contains("underline") == true).Sum(r => r.Width));

        var idle = UnderlinedCells();

        _session.Execute(new KeyCommand { Key = "Alt", Kind = "down" });
        Assert.True(UnderlinedCells() > idle); // the access-key cue underlines the C

        // WPF-style semantics: releasing Alt without a letter LATCHES the cue (menu mode) …
        _session.Execute(new KeyCommand { Key = "Alt", Kind = "up" });
        Assert.True(UnderlinedCells() > idle);

        // … and Escape exits it.
        _session.Execute(new KeyCommand { Key = "Escape" });
        Assert.Equal(idle, UnderlinedCells());
        Assert.DoesNotContain(_events, e => e is ErrorEvent);
    }

    private string FrameStyleSignature()
        => string.Join('|', Fold().Lines.Select(runs => string.Join(',', runs.Select(r => $"{r.Text}:{r.Style.Fg}/{r.Style.Bg}"))));

    [Fact]
    public void Pointer_click_reaches_the_content()
    {
        Initialize();
        Load($"""
              <StackPanel {Xmlns}>
                  <CheckBox x:Name="Toggle" Content="Toggle me"/>
              </StackPanel>
              """);
        var before = FrameText();

        // Click the checkbox glyph area, then verify the frame changed (checked state).
        _session.Execute(new PointerCommand { Kind = "down", Column = 1, Row = 0 });
        _session.Execute(new PointerCommand { Kind = "up", Column = 1, Row = 0 });

        var after = FrameText();
        Assert.NotEqual(before, after);
    }
}
