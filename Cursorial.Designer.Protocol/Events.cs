using System.Text.Json.Serialization;

namespace Cursorial.Designer.Protocol;

/// <summary>
/// A message sent from the preview host to the IDE plugin, one JSON object per line on stdout.
/// Events answering a specific command echo its id via <see cref="ReplyTo"/>; unsolicited events
/// (frames pushed after input or animation, logs) leave it null.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ReadyEvent), "ready")]
[JsonDerivedType(typeof(FrameEvent), "frame")]
[JsonDerivedType(typeof(DiagnosticsEvent), "diagnostics")]
[JsonDerivedType(typeof(HitTestResultEvent), "hitTestResult")]
[JsonDerivedType(typeof(PropertiesEvent), "properties")]
[JsonDerivedType(typeof(ErrorEvent), "error")]
[JsonDerivedType(typeof(LogEvent), "log")]
public abstract class PreviewEvent
{
    /// <summary>The id of the command this event answers, when it answers one.</summary>
    public long? ReplyTo { get; init; }
}

/// <summary>Emitted once at startup, before any command is processed.</summary>
public sealed class ReadyEvent : PreviewEvent
{
    public required int ProtocolVersion { get; init; }

    public required int Pid { get; init; }
}

/// <summary>
/// A full composited frame. Cell content is run-length encoded per row: each row is a list of
/// <see cref="TextRun"/>s whose <c>s</c> indexes into <see cref="Styles"/> (deduplicated per
/// frame). Run widths (<c>w</c>) always sum to <see cref="Columns"/> for every row.
/// </summary>
public sealed class FrameEvent : PreviewEvent
{
    public required int Columns { get; init; }

    public required int Rows { get; init; }

    public required CursorInfo Cursor { get; init; }

    /// <summary>The frame's deduplicated style table, referenced by <see cref="TextRun.StyleIndex"/>.</summary>
    public required IReadOnlyList<StyleInfo> Styles { get; init; }

    /// <summary>One entry per row, top to bottom; each row is its runs, left to right.</summary>
    public required IReadOnlyList<IReadOnlyList<TextRun>> Lines { get; init; }
}

/// <summary>Parse/load diagnostics for the most recent <c>loadXaml</c>. Always emitted, even when empty.</summary>
public sealed class DiagnosticsEvent : PreviewEvent
{
    public string? SourceUri { get; init; }

    public required IReadOnlyList<DiagnosticInfo> Items { get; init; }
}

/// <summary>Answer to <c>hitTest</c>: the element chain at the position, innermost first. Empty when nothing hit.</summary>
public sealed class HitTestResultEvent : PreviewEvent
{
    public required IReadOnlyList<ElementRef> Elements { get; init; }
}

/// <summary>Answer to <c>getProperties</c>: the element's non-default property values with provenance.</summary>
public sealed class PropertiesEvent : PreviewEvent
{
    public required int ElementId { get; init; }

    public required IReadOnlyList<PropertyEntry> Items { get; init; }
}

/// <summary>A command failed. The session stays alive; the previous content keeps rendering.</summary>
public sealed class ErrorEvent : PreviewEvent
{
    public required string Message { get; init; }

    /// <summary>Optional detail (typically an exception ToString) for the IDE's log, not for end-user display.</summary>
    public string? Detail { get; init; }
}

/// <summary>Host-side log line surfaced to the IDE's log. Levels: <c>debug</c>, <c>info</c>, <c>warn</c>, <c>error</c>.</summary>
public sealed class LogEvent : PreviewEvent
{
    public required string Level { get; init; }

    public required string Message { get; init; }
}
