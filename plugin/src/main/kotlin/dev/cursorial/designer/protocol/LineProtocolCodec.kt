package dev.cursorial.designer.protocol

import com.google.gson.Gson
import com.google.gson.GsonBuilder
import com.google.gson.JsonParser
import com.google.gson.JsonSyntaxException

/**
 * Encodes commands to and decodes events from the newline-delimited JSON wire format.
 *
 * Uses Gson because the IntelliJ Platform bundles it; no additional runtime dependency.
 * Gson's default (non-pretty) output never emits raw newlines — newlines inside string
 * values are escaped — so every encoded command is guaranteed to be a single line.
 */
object LineProtocolCodec {

    private val gson: Gson = GsonBuilder()
        .disableHtmlEscaping()
        .create()

    private val eventTypes: Map<String, Class<out PreviewerEvent>> = mapOf(
        "ready" to ReadyEvent::class.java,
        "frame" to FrameEvent::class.java,
        "diagnostics" to DiagnosticsEvent::class.java,
        "hitTestResult" to HitTestResultEvent::class.java,
        "children" to ChildrenEvent::class.java,
        "cellSamples" to CellSamplesEvent::class.java,
        "properties" to PropertiesEvent::class.java,
        "error" to ErrorEvent::class.java,
        "log" to LogEvent::class.java,
    )

    /** Serializes [command] as a single JSON line (without the trailing newline). */
    fun encodeCommand(command: PreviewerCommand): String = gson.toJson(command)

    /**
     * Parses one line of host output into a [PreviewerEvent].
     *
     * Returns [UnknownEvent] for well-formed JSON with an unrecognized `type`,
     * and `null` for blank or malformed lines (the caller should log those).
     */
    @Throws(MalformedEventException::class)
    fun decodeEvent(line: String): PreviewerEvent? {
        val trimmed = line.trim()
        if (trimmed.isEmpty()) return null

        val json = try {
            JsonParser.parseString(trimmed)
        } catch (e: JsonSyntaxException) {
            throw MalformedEventException("Not valid JSON: $trimmed", e)
        }
        if (!json.isJsonObject) {
            throw MalformedEventException("Expected a JSON object, got: $trimmed")
        }

        val obj = json.asJsonObject
        val type = obj.get("type")?.takeIf { it.isJsonPrimitive }?.asString
            ?: throw MalformedEventException("Event without a \"type\" property: $trimmed")

        val eventClass = eventTypes[type] ?: return UnknownEvent(type, trimmed)
        return try {
            gson.fromJson(obj, eventClass)
        } catch (e: JsonSyntaxException) {
            throw MalformedEventException("Malformed \"$type\" event: $trimmed", e)
        }
    }
}

class MalformedEventException(message: String, cause: Throwable? = null) : Exception(message, cause)
