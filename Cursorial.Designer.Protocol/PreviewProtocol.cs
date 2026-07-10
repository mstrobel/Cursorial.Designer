using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cursorial.Designer.Protocol;

/// <summary>
/// The wire protocol between the IDE plugin and the preview host: newline-delimited JSON, UTF-8,
/// one object per line. Commands flow host-ward on stdin; events flow IDE-ward on stdout; stderr
/// is free-form logging. Serialization is source-generated and never emits raw newlines, so a
/// serialized message is always exactly one line.
/// </summary>
public static class PreviewProtocol
{
    public const int Version = 1;

    /// <summary>Serialize a command to its single-line wire form (no trailing newline).</summary>
    public static string Serialize(PreviewCommand command)
        => JsonSerializer.Serialize(command, PreviewProtocolContext.Default.PreviewCommand);

    /// <summary>Serialize an event to its single-line wire form (no trailing newline).</summary>
    public static string Serialize(PreviewEvent @event)
        => JsonSerializer.Serialize(@event, PreviewProtocolContext.Default.PreviewEvent);

    /// <summary>
    /// Parse one wire line as a command. Throws <see cref="JsonException"/> on malformed input or
    /// an unknown <c>type</c> discriminator — callers own the decision to answer with an error
    /// event rather than crash the session.
    /// </summary>
    public static PreviewCommand DeserializeCommand(string line)
        => JsonSerializer.Deserialize(line, PreviewProtocolContext.Default.PreviewCommand)
           ?? throw new JsonException("Command line deserialized to null.");

    /// <summary>Parse one wire line as an event (the IDE-side direction; used in tests).</summary>
    public static PreviewEvent DeserializeEvent(string line)
        => JsonSerializer.Deserialize(line, PreviewProtocolContext.Default.PreviewEvent)
           ?? throw new JsonException("Event line deserialized to null.");
}

/// <summary>
/// Source-generated serializer context for every wire shape. Camel-case properties, nulls
/// omitted, out-of-order <c>type</c> discriminators tolerated (the Kotlin side does not
/// guarantee member order).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(PreviewCommand))]
[JsonSerializable(typeof(PreviewEvent))]
public sealed partial class PreviewProtocolContext : JsonSerializerContext;
