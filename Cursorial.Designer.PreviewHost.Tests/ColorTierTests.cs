using Cursorial.Designer.PreviewHost;
using Cursorial.Designer.Protocol;

namespace Cursorial.Designer.Tests.PreviewHost;

/// <summary>
/// The color tier rides on the capabilities (the gallery's OnCapabilitiesChanged pattern), so it forces
/// the app to actually render at that depth — not just re-theme. An explicit RGB colour reaches the wire
/// under truecolor and is quantized away under nocolor, both when baked in at initialize and when switched
/// live.
/// </summary>
public class ColorTierTests : IDisposable
{
    private const string Xmlns = "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    private readonly List<PreviewEvent> _events = [];
    private readonly PreviewSession _session;

    public ColorTierTests() => _session = new PreviewSession(_events.Add);

    public void Dispose() => _session.Dispose();

    private void Load(long id) => _session.Execute(new LoadXamlCommand
    {
        Id = id,
        Xaml = $"<Border {Xmlns} Background=\"#FF0000\"><TextBlock Text=\"hi\"/></Border>",
    });

    private static bool HasRgb(FrameEvent frame) =>
        frame.Styles.Any(s => (s.Bg is { } bg && bg.StartsWith('#')) || (s.Fg is { } fg && fg.StartsWith('#')));

    [Fact]
    public void Live_switch_to_nocolor_quantizes_the_frame_monochrome()
    {
        _session.Execute(new InitializeCommand { ProtocolVersion = 1, Columns = 20, Rows = 6, Capabilities = "kitty-truecolor" });
        Load(1);

        // Truecolor: the red background reaches the wire as an RGB colour.
        var colored = _events.OfType<FrameEvent>().Last();
        Assert.Contains(colored.Styles, s => s.Bg == "#ff0000");

        // Switch to nocolor live: OnCapabilitiesChanged forces the depth, and the rebuilt quantizer strips
        // colour — no RGB survives on the wire.
        var before = _events.Count;
        _session.Execute(new SetThemeCommand { ColorTier = "nocolor" });
        var mono = _events.Skip(before).OfType<FrameEvent>().Last();
        Assert.False(HasRgb(mono), "nocolor tier must leave no RGB colours on the wire");
    }

    [Fact]
    public void Nocolor_baked_in_at_initialize_renders_monochrome_from_the_first_frame()
    {
        _session.Execute(new InitializeCommand { ProtocolVersion = 1, Columns = 20, Rows = 6, Capabilities = "kitty-truecolor", ColorTier = "nocolor" });
        Load(1);

        var frame = _events.OfType<FrameEvent>().Last();
        Assert.False(HasRgb(frame), "a nocolor tier baked in at initialize must render monochrome");
    }
}
