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

    private static string FrameText(FrameEvent frame)
        => string.Join('\n', frame.Lines.Select(runs => string.Concat(runs.Select(r => r.Text))));

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
    public void Commands_before_initialize_fail_loudly()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => _session.Execute(new ResizeCommand { Columns = 10, Rows = 5 }));
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

        var text = FrameText(LastFrame());
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
        Assert.Contains("Survivor", FrameText(LastFrame()));
    }

    [Fact]
    public void Resize_reflows_and_emits_the_new_geometry()
    {
        Initialize(columns: 60, rows: 16);
        Load($"""<StackPanel {Xmlns}><TextBlock Text="resize me"/></StackPanel>""");

        _session.Execute(new ResizeCommand { Columns = 100, Rows = 30 });

        var frame = LastFrame();
        Assert.Equal(100, frame.Columns);
        Assert.Equal(30, frame.Rows);
        Assert.Contains("resize me", FrameText(frame));
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
        var darkFrame = LastFrame();

        _session.Execute(new SetThemeCommand { ThemeBase = "light" });

        var lightFrame = LastFrame();
        Assert.NotSame(darkFrame, lightFrame);
        Assert.Contains("Theme probe", FrameText(lightFrame));

        // The style tables should differ between dark and light renders.
        Assert.NotEqual(
            darkFrame.Styles.Select(s => (s.Fg, s.Bg)).ToList(),
            lightFrame.Styles.Select(s => (s.Fg, s.Bg)).ToList());
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
        Assert.Contains("Survivor", FrameText(LastFrame()));

        // The session must remain fully functional after the broken document.
        _session.Execute(new ResizeCommand { Columns = 80, Rows = 20 });
        var frame = LastFrame();
        Assert.Equal(80, frame.Columns);
        Assert.Contains("Survivor", FrameText(frame));
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

        _session.Execute(new AdvanceTimeCommand { Milliseconds = 100 });

        Assert.Equal(frames + 1, _events.Count(e => e is FrameEvent));
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

        Assert.Contains("hi", FrameText(LastFrame()));
    }

    [Fact]
    public void Ansi16_profile_quantizes_wire_colors_to_the_16_color_palette()
    {
        _session.Execute(new InitializeCommand { ProtocolVersion = 1, Columns = 60, Rows = 16, Capabilities = "ansi16" });
        Load($"""<StackPanel {Xmlns}><Button Content="tiered"/><TextBlock Text="palette"/></StackPanel>""");

        var allowed = Enumerable.Range(0, 16).Select(i => XtermPalette.ToHex((byte)i)).ToHashSet();
        var frame = LastFrame();
        Assert.All(frame.Styles, s =>
        {
            if (s.Fg is not null)
                Assert.Contains(s.Fg, allowed);
            if (s.Bg is not null)
                Assert.Contains(s.Bg, allowed);
        });
    }

    [Fact]
    public void Window_rooted_documents_render_undimmed()
    {
        Initialize();
        Load($"""<Window {Xmlns} Width="40" Height="10"><TextBlock Text="windowed"/></Window>""");

        Assert.Contains("windowed", FrameText(LastFrame()));
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
        var idle = LastFrame();

        _session.Execute(new KeyCommand { Key = "Space", Kind = "down" });
        var held = LastFrame();
        Assert.NotEqual(FrameStyleSignature(idle), FrameStyleSignature(held));

        _session.Execute(new KeyCommand { Key = "Space", Kind = "down" }); // auto-repeat: stays pressed, no error
        Assert.Equal(FrameStyleSignature(held), FrameStyleSignature(LastFrame()));

        _session.Execute(new KeyCommand { Key = "Space", Kind = "up" });
        Assert.Equal(FrameStyleSignature(idle), FrameStyleSignature(LastFrame()));
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
        Assert.Contains("Hello from design data", FrameText(LastFrame()));

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
        Assert.Contains("Save", FrameText(LastFrame()));
    }

    [Fact]
    public void Alt_down_shows_access_key_underlines_until_alt_up()
    {
        Initialize();
        Load($"""<StackPanel {Xmlns}><Button Content="_Cancel" HorizontalAlignment="Left"/></StackPanel>""");

        // The theme may use underline of its own, so assert on the *count* of underlined cells:
        // the access-key cue must add underlines on Alt down and remove them on Alt up.
        static int UnderlinedCells(FrameEvent frame)
            => frame.Lines.Sum(runs => runs.Where(r => frame.Styles[r.StyleIndex].Attrs?.Contains("underline") == true).Sum(r => r.Width));

        var idle = UnderlinedCells(LastFrame());

        _session.Execute(new KeyCommand { Key = "Alt", Kind = "down" });
        Assert.True(UnderlinedCells(LastFrame()) > idle); // the access-key cue underlines the C

        // WPF-style semantics: releasing Alt without a letter LATCHES the cue (menu mode) …
        _session.Execute(new KeyCommand { Key = "Alt", Kind = "up" });
        Assert.True(UnderlinedCells(LastFrame()) > idle);

        // … and Escape exits it.
        _session.Execute(new KeyCommand { Key = "Escape" });
        Assert.Equal(idle, UnderlinedCells(LastFrame()));
        Assert.DoesNotContain(_events, e => e is ErrorEvent);
    }

    private static string FrameStyleSignature(FrameEvent frame)
        => string.Join('|', frame.Lines.Select(runs => string.Join(',', runs.Select(r => $"{r.Text}:{frame.Styles[r.StyleIndex].Fg}/{frame.Styles[r.StyleIndex].Bg}"))));

    [Fact]
    public void Pointer_click_reaches_the_content()
    {
        Initialize();
        Load($"""
              <StackPanel {Xmlns}>
                  <CheckBox x:Name="Toggle" Content="Toggle me"/>
              </StackPanel>
              """);
        var before = FrameText(LastFrame());

        // Click the checkbox glyph area, then verify the frame changed (checked state).
        _session.Execute(new PointerCommand { Kind = "down", Column = 1, Row = 0 });
        _session.Execute(new PointerCommand { Kind = "up", Column = 1, Row = 0 });

        var after = FrameText(LastFrame());
        Assert.NotEqual(before, after);
    }
}
