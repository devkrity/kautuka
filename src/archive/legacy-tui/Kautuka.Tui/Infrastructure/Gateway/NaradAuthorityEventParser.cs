using System.Text.Json;

namespace Kautuka.Tui.Infrastructure.Gateway;

public static class NaradAuthorityEventParser
{
    private static readonly HashSet<string> terminalRunEventTypes = new(StringComparer.Ordinal)
    {
        "run.finished",
        "run.error",
        "run.cancelled",
    };

    public static bool IsTerminalRunEvent(string eventType) => terminalRunEventTypes.Contains(eventType);

    public static bool IsInterruptBoundaryEvent(string eventType)
        => string.Equals(eventType, "interrupt.requested", StringComparison.Ordinal);

    public static bool ShouldSurfaceToChat(string eventType)
        => eventType is "run.started"
            or "reasoning.started"
            or "reasoning.delta"
            or "reasoning.completed"
            or "message.text.started"
            or "message.text.delta"
            or "message.text.completed"
            or "tool.call.started"
            or "tool.call.finished"
            or "interrupt.requested"
            or "run.error"
            or "run.finished"
            or "run.cancelled"
            or "progress.recorded";

    public static GatewayNaradAuthorityEvent? ParseSseData(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(payload);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("type", out JsonElement typeElement))
        {
            return null;
        }

        string eventType = typeElement.GetString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(eventType))
        {
            return null;
        }

        if (root.TryGetProperty("eventId", out JsonElement eventIdElement)
            && eventIdElement.ValueKind == JsonValueKind.String)
        {
            return ParseAuthority(root, eventType);
        }

        return null;
    }

    private static GatewayNaradAuthorityEvent ParseAuthority(JsonElement root, string eventType)
        => new(
            eventType,
            ReadOptionalString(root, "eventId"),
            ReadOptionalInt64(root, "sessionSequence"),
            ReadOptionalString(root, "rootRunId"),
            ReadOptionalString(root, "runId"),
            ReadOptionalString(root, "delta"),
            ReadOptionalString(root, "content"),
            ReadOptionalString(root, "message"),
            ReadOptionalString(root, "messageId"),
            ReadOptionalString(root, "toolCallId"),
            ReadOptionalString(root, "toolName"),
            ReadOptionalString(root, "outcome"),
            ReadOptionalString(root, "operation"),
            ReadOptionalString(root, "status"),
            ReadOptionalString(root, "kind"),
            ReadOptionalString(root, "title") ?? ReadOptionalString(root, "label"));

    private static string? ReadOptionalString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;

    private static long? ReadOptionalInt64(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out long value)
                ? value
                : null;
}
