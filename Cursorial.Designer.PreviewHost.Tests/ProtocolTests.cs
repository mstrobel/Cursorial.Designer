using Cursorial.Designer.Protocol;

namespace Cursorial.Designer.Tests.PreviewHost;

public class ProtocolTests
{
    [Fact]
    public void Command_round_trips_through_wire_form()
    {
        var line = PreviewProtocol.Serialize(new LoadXamlCommand
        {
            Id = 42,
            Xaml = "<StackPanel/>",
            SourceUri = "file:///tmp/View.xaml",
            Assemblies = ["/tmp/App.dll"],
        });

        Assert.DoesNotContain('\n', line);
        Assert.Contains("\"type\":\"loadXaml\"", line);

        var parsed = Assert.IsType<LoadXamlCommand>(PreviewProtocol.DeserializeCommand(line));
        Assert.Equal(42, parsed.Id);
        Assert.Equal("<StackPanel/>", parsed.Xaml);
        Assert.Equal("file:///tmp/View.xaml", parsed.SourceUri);
        Assert.Equal(["/tmp/App.dll"], parsed.Assemblies);
    }

    [Fact]
    public void Command_parses_with_out_of_order_discriminator()
    {
        // The Kotlin side does not guarantee that "type" is the first member.
        var parsed = PreviewProtocol.DeserializeCommand("""{"columns":100,"rows":30,"type":"resize"}""");

        var resize = Assert.IsType<ResizeCommand>(parsed);
        Assert.Equal(100, resize.Columns);
        Assert.Equal(30, resize.Rows);
    }

    [Fact]
    public void Event_round_trips_and_omits_nulls()
    {
        var line = PreviewProtocol.Serialize(new FrameEvent
        {
            Columns = 2,
            Rows = 1,
            Cursor = new CursorInfo { Row = 0, Column = 0, Visible = false, Shape = "default" },
            Styles = [new StyleInfo { Fg = "#ff0000" }],
            Lines = [[new TextRun { Text = "hi", StyleIndex = 0, Width = 2 }]],
        });

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain("\"bg\"", line);      // null members are omitted
        Assert.Contains("\"t\":\"hi\"", line);       // runs use the compact member names

        var parsed = Assert.IsType<FrameEvent>(PreviewProtocol.DeserializeEvent(line));
        Assert.Equal(2, parsed.Columns);
        Assert.Equal("#ff0000", parsed.Styles[0].Fg);
        Assert.Null(parsed.Styles[0].Bg);
        Assert.Equal("hi", parsed.Lines[0][0].Text);
    }

    [Fact]
    public void Unknown_command_type_throws()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(
            () => PreviewProtocol.DeserializeCommand("""{"type":"fabricated"}"""));
    }
}
