using System.Text;

using Cursorial.Designer.Protocol;
using Cursorial.Output;
using Cursorial.Rendering;

namespace Cursorial.Designer.PreviewHost;

/// <summary>
/// Turns a composited <see cref="CellBuffer"/> into the wire <see cref="FrameEvent"/>: per-row
/// run-length encoding over a per-frame deduplicated style table. This reads the same buffer the
/// framework's own tests assert against, so what the IDE paints is what a terminal would show —
/// minus fragments (Kitty/Sixel/iTerm2 images), which are not carried in v1.
/// </summary>
internal static class FrameSerializer
{
    /// <param name="buffer">The composited frame to encode.</param>
    /// <param name="quantizer">
    /// When supplied, styles are quantized to the profiled terminal's capabilities before
    /// encoding, so a reduced-capability preview (ansi16, no-color) shows what that terminal
    /// could actually display rather than the pre-quantization intent.
    /// </param>
    public static FrameEvent Serialize(CellBuffer buffer, StyleQuantizer? quantizer = null)
    {
        var styles = new List<StyleInfo>();
        var styleIndices = new Dictionary<Style, int>();
        var lines = new List<IReadOnlyList<Protocol.TextRun>>(buffer.Rows);

        for (var row = 0; row < buffer.Rows; row++)
        {
            var runs = new List<Protocol.TextRun>();
            var runText = new StringBuilder();
            var runWidth = 0;
            var runStyle = -1;

            for (var column = 0; column < buffer.Columns; column++)
            {
                var cell = buffer[column, row];
                if (cell.Kind == CellKind.WideContinuation)
                    continue; // the preceding WideLeft already covered this column

                var style = GetStyleIndex(cell.Style, quantizer, styles, styleIndices);
                if (style != runStyle && runWidth > 0)
                {
                    runs.Add(new Protocol.TextRun { Text = runText.ToString(), StyleIndex = runStyle, Width = runWidth });
                    runText.Clear();
                    runWidth = 0;
                }

                runStyle = style;

                // Blank and durable-empty (NBSP) cells both paint as a space — the same
                // normalization the terminal renderer applies at emission.
                var grapheme = cell.Grapheme;
                runText.Append(string.IsNullOrEmpty(grapheme) || grapheme == CellBuffer.DurableEmptyGrapheme ? " " : grapheme);
                runWidth += cell.Width; // Single = 1, WideLeft = 2
            }

            if (runWidth > 0)
                runs.Add(new Protocol.TextRun { Text = runText.ToString(), StyleIndex = runStyle, Width = runWidth });

            lines.Add(runs);
        }

        return new FrameEvent
        {
            Columns = buffer.Columns,
            Rows = buffer.Rows,
            Cursor = new CursorInfo
            {
                Row = buffer.CursorRow,
                Column = buffer.CursorColumn,
                Visible = buffer.CursorVisible,
                Shape = ShapeName(buffer.CursorShape),
            },
            Styles = styles,
            Lines = lines,
        };
    }

    private static int GetStyleIndex(in Style style, StyleQuantizer? quantizer, List<StyleInfo> styles, Dictionary<Style, int> indices)
    {
        // Dedup on the raw style, quantize once per unique entry.
        if (indices.TryGetValue(style, out var existing))
            return existing;

        var index = styles.Count;
        styles.Add(ToStyleInfo(quantizer is null ? style : quantizer.Quantize(in style)));
        indices.Add(style, index);
        return index;
    }

    internal static StyleInfo ToStyleInfo(Style style)
    {
        List<string>? attrs = null;
        void Add(TextAttributes flag, string name)
        {
            if ((style.Attributes & flag) != 0)
                (attrs ??= []).Add(name);
        }

        Add(TextAttributes.Bold, "bold");
        Add(TextAttributes.Faint, "dim");
        Add(TextAttributes.Italic, "italic");
        Add(TextAttributes.Underline, "underline");
        Add(TextAttributes.Blink, "blink");
        Add(TextAttributes.Inverse, "reverse");
        Add(TextAttributes.Hidden, "hidden");
        Add(TextAttributes.Strikethrough, "strikethrough");
        Add(TextAttributes.Overline, "overline");

        var underlined = (style.Attributes & TextAttributes.Underline) != 0;
        return new StyleInfo
        {
            Fg = ColorHex(style.Foreground),
            Bg = ColorHex(style.Background),
            Attrs = attrs,
            Underline = underlined && style.UnderlineStyle != UnderlineStyle.Single
                ? style.UnderlineStyle switch
                {
                    UnderlineStyle.Double => "double",
                    UnderlineStyle.Curly => "curly",
                    UnderlineStyle.Dotted => "dotted",
                    UnderlineStyle.Dashed => "dashed",
                    _ => null,
                }
                : null,
            UnderlineColor = underlined && !style.UnderlineColor.IsDefault ? ColorHex(style.UnderlineColor) : null,
            Link = style.Hyperlink.IsEmpty ? null : style.Hyperlink.Uri,
        };
    }

    /// <summary>
    /// <c>#RRGGBB</c> for concrete colors, <see langword="null"/> for the terminal-default color
    /// (the viewer supplies its own default fg/bg). Palette entries resolve through the standard
    /// xterm-256 palette; alpha is dropped — cells arrive composited.
    /// </summary>
    private static string? ColorHex(in Color color) => color.Kind switch
    {
        ColorKind.Default => null,
        ColorKind.Palette => XtermPalette.ToHex(color.PaletteIndex),
        _ => $"#{color.Red:x2}{color.Green:x2}{color.Blue:x2}",
    };

    private static string ShapeName(CursorShape shape) => shape switch
    {
        CursorShape.BlinkingBlock or CursorShape.SteadyBlock => "block",
        CursorShape.BlinkingUnderline or CursorShape.SteadyUnderline => "underline",
        CursorShape.BlinkingBar or CursorShape.SteadyBar => "bar",
        _ => "default",
    };
}

/// <summary>The standard xterm-256 palette, for resolving indexed colors to RGB for the IDE panel.</summary>
internal static class XtermPalette
{
    private static readonly uint[] Base16 =
    [
        0x000000, 0xcd0000, 0x00cd00, 0xcdcd00, 0x0000ee, 0xcd00cd, 0x00cdcd, 0xe5e5e5,
        0x7f7f7f, 0xff0000, 0x00ff00, 0xffff00, 0x5c5cff, 0xff00ff, 0x00ffff, 0xffffff,
    ];

    public static string ToHex(byte index)
    {
        uint rgb;
        if (index < 16)
        {
            rgb = Base16[index];
        }
        else if (index < 232)
        {
            // 6×6×6 color cube: component n ∈ [0,5] maps to 0 or 55 + 40n.
            var value = index - 16;
            var r = CubeComponent(value / 36);
            var g = CubeComponent(value / 6 % 6);
            var b = CubeComponent(value % 6);
            rgb = (uint)(r << 16 | g << 8 | b);
        }
        else
        {
            // 24-step grayscale ramp: 8, 18, …, 238.
            var gray = 8 + 10 * (index - 232);
            rgb = (uint)(gray << 16 | gray << 8 | gray);
        }

        return $"#{rgb:x6}";
    }

    private static int CubeComponent(int n) => n == 0 ? 0 : 55 + 40 * n;
}
