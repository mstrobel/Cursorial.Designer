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
    public static FrameEvent Serialize(CellBuffer buffer, StyleQuantizer? quantizer = null, bool lightBase = false)
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

    /// <summary>
    /// The event to emit for <paramref name="next"/> given the previously emitted
    /// <paramref name="last"/>: the full frame when there is no baseline or the dimensions
    /// changed, a row-level delta when some rows (or the cursor) differ, or null when nothing
    /// changed at all — play-mode ticks and pointer moves over static content cost nothing.
    /// </summary>
    internal static FrameEvent? MakeDelta(FrameEvent? last, FrameEvent next)
    {
        if (last is null || last.Columns != next.Columns || last.Rows != next.Rows)
            return next;

        var changedRows = new List<int>();
        for (var row = 0; row < next.Lines.Count; row++)
        {
            if (!RowsEqual(last, next, row))
                changedRows.Add(row);
        }

        var cursorChanged = !CursorsEqual(last.Cursor, next.Cursor);
        if (changedRows.Count == 0 && !cursorChanged)
            return null;

        // A local style table holding only what the changed rows reference.
        var styles = new List<StyleInfo>();
        var remap = new Dictionary<int, int>();
        var changed = new List<ChangedRowInfo>(changedRows.Count);
        foreach (var row in changedRows)
        {
            var runs = new List<Protocol.TextRun>();
            foreach (var run in next.Lines[row])
            {
                if (!remap.TryGetValue(run.StyleIndex, out var mapped))
                {
                    mapped = styles.Count;
                    styles.Add(next.Styles[run.StyleIndex]);
                    remap[run.StyleIndex] = mapped;
                }

                runs.Add(new Protocol.TextRun { Text = run.Text, StyleIndex = mapped, Width = run.Width });
            }

            changed.Add(new ChangedRowInfo { Index = row, Runs = runs });
        }

        return new FrameEvent
        {
            Columns = next.Columns,
            Rows = next.Rows,
            Cursor = next.Cursor,
            Styles = styles,
            Lines = [],
            Delta = true,
            Changed = changed,
        };
    }

    private static bool RowsEqual(FrameEvent last, FrameEvent next, int row)
    {
        var a = last.Lines[row];
        var b = next.Lines[row];
        if (a.Count != b.Count)
            return false;

        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Text != b[i].Text || a[i].Width != b[i].Width)
                return false;
            if (!StylesEqual(last.Styles[a[i].StyleIndex], next.Styles[b[i].StyleIndex]))
                return false;
        }

        return true;
    }

    private static bool StylesEqual(StyleInfo a, StyleInfo b)
        => a.Fg == b.Fg
           && a.Bg == b.Bg
           && a.Underline == b.Underline
           && a.UnderlineColor == b.UnderlineColor
           && a.Link == b.Link
           && (a.Attrs ?? []).SequenceEqual(b.Attrs ?? []);

    private static bool CursorsEqual(CursorInfo a, CursorInfo b)
        => a.Row == b.Row && a.Column == b.Column && a.Visible == b.Visible && a.Shape == b.Shape;

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

    internal static StyleInfo ToStyleInfo(Style style, bool lightBase = false)
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
            Fg = ColorHex(style.Foreground, lightBase),
            Bg = ColorHex(style.Background, lightBase),
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
            UnderlineColor = underlined && !style.UnderlineColor.IsDefault ? ColorHex(style.UnderlineColor, lightBase) : null,
            Link = style.Hyperlink.IsEmpty ? null : style.Hyperlink.Uri,
        };
    }

    /// <summary>
    /// <c>#RRGGBB</c> for concrete colors, <see langword="null"/> for the terminal-default color
    /// (the viewer supplies its own default fg/bg). Palette entries resolve through the standard
    /// xterm-256 palette; alpha is dropped — cells arrive composited.
    /// </summary>
    private static string? ColorHex(in Color color, bool lightBase) => color.Kind switch
    {
        ColorKind.Default => null,
        ColorKind.Palette => XtermPalette.ToHex(color.PaletteIndex, lightBase),
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

/// <summary>
/// The standard xterm-256 palette, for resolving indexed colors to RGB for the IDE panel. The
/// ANSI 0–15 block is theme-paired the way real terminal emulators pair palettes: the classic
/// xterm values on a dark base, the VS&#160;Code Light+ values on a light base (xterm's
/// near-white "white" #e5e5e5 vanishes on light backgrounds). The 6×6×6 cube and the grayscale
/// ramp are absolute and identical on both bases.
/// </summary>
internal static class XtermPalette
{
    private static readonly uint[] Base16 =
    [
        0x000000, 0xcd0000, 0x00cd00, 0xcdcd00, 0x0000ee, 0xcd00cd, 0x00cdcd, 0xe5e5e5,
        0x7f7f7f, 0xff0000, 0x00ff00, 0xffff00, 0x5c5cff, 0xff00ff, 0x00ffff, 0xffffff,
    ];

    private static readonly uint[] Base16Light =
    [
        0x000000, 0xcd3131, 0x00bc00, 0x949800, 0x0451a5, 0xbc05bc, 0x0598bc, 0x555555,
        0x666666, 0xcd3131, 0x14ce14, 0xb5ba00, 0x0451a5, 0xbc05bc, 0x0598bc, 0xa5a5a5,
    ];

    public static string ToHex(byte index, bool lightBase = false)
    {
        uint rgb;
        if (index < 16)
        {
            rgb = (lightBase ? Base16Light : Base16)[index];
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
