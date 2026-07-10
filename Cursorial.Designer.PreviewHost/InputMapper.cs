using System.Globalization;

using Cursorial.Input;

namespace Cursorial.Designer.PreviewHost;

/// <summary>Maps the protocol's wire strings onto Cursorial's input enums.</summary>
internal static class InputMapper
{
    /// <summary>
    /// Maps a wire key name to a <see cref="Key"/> plus the text it types. Named keys first;
    /// otherwise any single grapheme cluster (including non-BMP characters) is a printable
    /// character (<see cref="Key.Character"/>). Returns <see langword="false"/> for names this
    /// protocol version does not know.
    /// </summary>
    public static bool TryMapKey(string key, out Key mapped, out string? text)
    {
        text = null;

        switch (key.ToLowerInvariant())
        {
            case "enter": mapped = Key.Enter; return true;
            case "tab": mapped = Key.Tab; return true;
            case "escape" or "esc": mapped = Key.Escape; return true;
            case "space": mapped = Key.Space; text = " "; return true;
            case "up": mapped = Key.UpArrow; return true;
            case "down": mapped = Key.DownArrow; return true;
            case "left": mapped = Key.LeftArrow; return true;
            case "right": mapped = Key.RightArrow; return true;
            case "backspace": mapped = Key.Backspace; return true;
            case "delete": mapped = Key.Delete; return true;
            case "insert": mapped = Key.Insert; return true;
            case "home": mapped = Key.Home; return true;
            case "end": mapped = Key.End; return true;
            case "pageup": mapped = Key.PageUp; return true;
            case "pagedown": mapped = Key.PageDown; return true;
        }

        if (key is ['f' or 'F', .. var digits] && int.TryParse(digits, out var f) && f is >= 1 and <= 24)
        {
            mapped = Key.F1 + (f - 1);
            return true;
        }

        if (key.Length > 0 && new StringInfo(key).LengthInTextElements == 1)
        {
            mapped = Key.Character;
            text = key;
            return true;
        }

        mapped = Key.None;
        return false;
    }

    /// <summary>
    /// Maps wire modifier names; fails on the first unknown name (reported via
    /// <paramref name="unknown"/>) rather than silently dropping it.
    /// </summary>
    public static bool TryMapModifiers(IReadOnlyList<string>? modifiers, out KeyModifiers mapped, out string? unknown)
    {
        mapped = KeyModifiers.None;
        unknown = null;
        if (modifiers is null)
            return true;

        foreach (var modifier in modifiers)
        {
            switch (modifier.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    mapped |= KeyModifiers.Control;
                    break;
                case "alt":
                    mapped |= KeyModifiers.Alt;
                    break;
                case "shift":
                    mapped |= KeyModifiers.Shift;
                    break;
                case "super":
                    mapped |= KeyModifiers.Super;
                    break;
                case "meta":
                    mapped |= KeyModifiers.Meta;
                    break;
                default:
                    unknown = modifier;
                    return false;
            }
        }

        return true;
    }

    public static bool TryMapButton(string? button, out MouseButton mapped)
    {
        (var known, mapped) = button?.ToLowerInvariant() switch
        {
            null or "left" => (true, MouseButton.Left),
            "right" => (true, MouseButton.Right),
            "middle" => (true, MouseButton.Middle),
            _ => (false, MouseButton.Left),
        };
        return known;
    }
}
