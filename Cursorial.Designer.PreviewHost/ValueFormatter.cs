using System.Globalization;
using System.Runtime.CompilerServices;

using Cursorial.Drawing.Media;
using Cursorial.Output;
using Cursorial.UI;

namespace Cursorial.Designer.PreviewHost;

/// <summary>
/// Display formatting for inspected values, ported from the framework's InspectorDemo
/// (<c>InspectorDemo.FormatValue</c>): friendlier than <c>ToString</c> for the types a designer
/// meets constantly (elements, brushes, pens, colors, properties), invariant-culture for the
/// rest. Composite types (Pen, gradients) render flat for now — hierarchical value trees are a
/// future protocol extension.
/// </summary>
internal static class ValueFormatter
{
    public static string? Format(object? value) => value switch
    {
        null => null,
        UIElement element => $"{{{element.GetType().Name}}} ({RuntimeHelpers.GetHashCode(element):x8})",
        Array array => $"[{string.Join(", ", array.Cast<object?>().Select(Format))}]",
        System.Collections.IList list => $"[{string.Join(", ", list.Cast<object?>().Select(Format))}]",
        UIProperty property => $"{property.OwnerType.Name}.{property.Name}",
        TimeSpan span => span.Hours > 0 ? $"{span.Hours:0.##}h" : span.Minutes > 0 ? $"{span.Minutes:0.##}m" : $"{span.Seconds:0.##}s",
        Color color => color.Kind == ColorKind.Rgb ? $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}{color.Alpha:X2}" : color.ToString(),
        Pen pen => $"Pen {{ Brush={Format(pen.Brush)}, Weight={pen.Weight} }}",
        SolidColorBrush solid => $"{Format(solid.Color)} Opacity={solid.Opacity:0.##}",
        LinearGradientBrush linear =>
            $"linear:({linear.StartPoint.X},{linear.StartPoint.Y}) -> ({linear.EndPoint.X},{linear.EndPoint.Y}): " +
            string.Join(", ", linear.Stops.Select(stop => Format(stop.Color))),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => HasToStringOverride(value) ? value.ToString() : $"{{{value.GetType().Name}}}",
    };

    private static bool HasToStringOverride(object value)
        => value.GetType().GetMethod(nameof(ToString), Type.EmptyTypes)?.DeclaringType != typeof(object);

    /// <summary>
    /// An inline swatch hex for color-like values (<c>#RRGGBB</c>, or <c>#RRGGBBAA</c> when
    /// translucent); null for everything else.
    /// </summary>
    public static string? SwatchHex(object? value) => value switch
    {
        Color { Kind: ColorKind.Rgb } color => color.Alpha == 255
            ? $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}"
            : $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}{color.Alpha:X2}",
        SolidColorBrush solid => SwatchHex(solid.Color),
        _ => null,
    };
}
