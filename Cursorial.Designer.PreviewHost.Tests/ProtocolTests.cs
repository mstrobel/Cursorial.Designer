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
    public void Pointer_command_round_trips_modifiers()
    {
        var line = PreviewProtocol.Serialize(new PointerCommand
        {
            Kind = "down",
            Column = 5,
            Row = 2,
            Button = "left",
            Modifiers = ["ctrl", "shift"],
        });

        Assert.DoesNotContain('\n', line);
        Assert.Contains("\"modifiers\":[\"ctrl\",\"shift\"]", line); // ambient snapshot the terminal can't read

        var parsed = Assert.IsType<PointerCommand>(PreviewProtocol.DeserializeCommand(line));
        Assert.Equal("down", parsed.Kind);
        Assert.Equal(["ctrl", "shift"], parsed.Modifiers);
    }

    [Fact]
    public void Pointer_command_omits_absent_modifiers()
    {
        // A move with no modifiers must not carry an empty array — the field is optional on the wire.
        var line = PreviewProtocol.Serialize(new PointerCommand { Kind = "move", Column = 1, Row = 1 });
        Assert.DoesNotContain("\"modifiers\"", line);

        var parsed = Assert.IsType<PointerCommand>(PreviewProtocol.DeserializeCommand(line));
        Assert.Null(parsed.Modifiers);
    }

    [Fact]
    public void Ready_event_round_trips_framework_report()
    {
        var line = PreviewProtocol.Serialize(new ReadyEvent
        {
            ProtocolVersion = 1,
            Pid = 99,
            FrameworkVersion = "0.5.0.0",
            FrameworkSource = "bundled",
            FrameworkPath = "/opt/previewHost",
            FallbackReason = "this project targets the older 0.4.0",
            UserDirMissing = true,
        });

        Assert.Contains("\"frameworkVersion\":\"0.5.0.0\"", line);
        Assert.Contains("\"frameworkSource\":\"bundled\"", line);
        Assert.Contains("\"frameworkPath\":\"/opt/previewHost\"", line);
        Assert.Contains("\"fallbackReason\":", line);
        Assert.Contains("\"userDirMissing\":true", line);

        var parsed = Assert.IsType<ReadyEvent>(PreviewProtocol.DeserializeEvent(line));
        Assert.Equal("0.5.0.0", parsed.FrameworkVersion);
        Assert.Equal("bundled", parsed.FrameworkSource);
        Assert.Equal("/opt/previewHost", parsed.FrameworkPath);
        Assert.Equal("this project targets the older 0.4.0", parsed.FallbackReason);
        Assert.True(parsed.UserDirMissing);
    }

    [Fact]
    public void Ready_event_omits_absent_framework_report_fields()
    {
        // The fields are ADDITIVE: an unpopulated report must serialize exactly like a
        // pre-report host's payload, so older plugin builds parse it unchanged.
        var line = PreviewProtocol.Serialize(new ReadyEvent { ProtocolVersion = 1, Pid = 99 });

        Assert.DoesNotContain("framework", line);
        Assert.DoesNotContain("fallbackReason", line);
        Assert.DoesNotContain("userDirMissing", line);

        var parsed = Assert.IsType<ReadyEvent>(PreviewProtocol.DeserializeEvent(line));
        Assert.Null(parsed.FrameworkVersion);
        Assert.Null(parsed.FrameworkSource);
        Assert.Null(parsed.FrameworkPath);
        Assert.Null(parsed.FallbackReason);
        Assert.Null(parsed.UserDirMissing);
    }

    [Fact]
    public void Unknown_command_type_throws()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(
            () => PreviewProtocol.DeserializeCommand("""{"type":"fabricated"}"""));
    }
}
