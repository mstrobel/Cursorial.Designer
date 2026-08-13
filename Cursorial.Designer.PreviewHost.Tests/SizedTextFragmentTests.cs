using Cursorial.Designer.PreviewHost;
using Cursorial.Designer.Protocol;
using Cursorial.Text;
using Cursorial.UI.Controls;

namespace Cursorial.Designer.Tests.PreviewHost;

/// <summary>A TextBlock that renders at 2× via OSC 66 text-sizing (set in code — TextSizing has no XAML converter).</summary>
public sealed class SizedTextProbe : TextBlock
{
    public SizedTextProbe()
    {
        Text = "BIG";
        TextElement.SetSizing(this, TextSizing.Double);
    }
}

/// <summary>
/// Scaled text (OSC 66) reaches the wire as a frame fragment under the sizing-capable profile, and
/// falls back to the plain cell grid without it — the two halves of task #24's sized-text arm.
/// </summary>
public class SizedTextFragmentTests : IDisposable
{
    private const string Xmlns = "xmlns=\"https://cursorial.dev/ui\" xmlns:x=\"https://cursorial.dev/xaml\"";

    private const string ProbeNs =
        "xmlns:t=\"clr-namespace:Cursorial.Designer.Tests.PreviewHost;assembly=Cursorial.Designer.PreviewHost.Tests\"";

    private readonly List<PreviewEvent> _events = [];
    private readonly PreviewSession _session;

    public SizedTextFragmentTests() => _session = new PreviewSession(_events.Add);

    public void Dispose() => _session.Dispose();

    private void Load(string capabilities)
    {
        _session.Execute(new InitializeCommand { ProtocolVersion = 1, Columns = 40, Rows = 12, Capabilities = capabilities });
        _session.Execute(new LoadXamlCommand
        {
            Id = 1,
            Xaml = $"<StackPanel {Xmlns} {ProbeNs}><t:SizedTextProbe/></StackPanel>",
            Assemblies = [typeof(SizedTextProbe).Assembly.Location],
        });
    }

    [Fact]
    public void Sized_text_becomes_a_fragment_under_the_kitty_sizing_profile()
    {
        Load("kitty-sizing");

        var frame = _events.OfType<FrameEvent>().Last();
        var fragment = Assert.Single(frame.Fragments!);

        Assert.Equal("sizedText", fragment.Kind);
        Assert.Equal(2, fragment.Scale);                 // TextSizing.Double
        Assert.Equal(["BIG"], fragment.Lines!);
        Assert.Equal(2, fragment.Rows);                  // 1 line × scale 2
        Assert.Equal(6, fragment.Columns);               // 3 cells × scale 2
        Assert.NotNull(fragment.Style);
    }

    [Fact]
    public void Sized_text_falls_back_to_the_cell_grid_without_the_sizing_profile()
    {
        Load("kitty-truecolor");

        var frame = _events.OfType<FrameEvent>().Last();
        // No text-sizing capability → the framework renders the FIGlet/monospace fallback into the
        // cell grid, so there is no sized-text fragment on the wire.
        Assert.True(frame.Fragments is null or { Count: 0 });
    }
}
