using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Execution;

internal static class NaradStreamObservation
{
    public static bool IsTerminalOrPaused(string eventType)
        => eventType is NaradEventTypes.RunFinished
            or NaradEventTypes.RunError
            or NaradEventTypes.RunCancelled
            or NaradEventTypes.RunPaused;

    public static bool IsRootTerminal(string eventType)
        => NaradEventTypes.IsRootRunTerminalEventType(eventType);

    public static string ValidateEventJson(NaradPersistedEvent persistedEvent)
        => NaradEventJsonGuard.ValidatePersistedEventJson(persistedEvent.EventJson, $"narad event '{persistedEvent.EventId}'");

    public static JsonElement ParseEnvelope(NaradPersistedEvent persistedEvent)
    {
        ValidateEventJson(persistedEvent);
        using JsonDocument document = JsonDocument.Parse(persistedEvent.EventJson);
        return document.RootElement.Clone();
    }

    public static string? ReadString(NaradPersistedEvent persistedEvent, string propertyName)
        => NaradPersistedEventReader.String(persistedEvent, propertyName);

    public static string? ReadBodyString(NaradPersistedEvent persistedEvent, string propertyName)
    {
        JsonElement body = NaradPersistedEventReader.ParseBody(persistedEvent);
        return NaradPersistedEventReader.String(body, propertyName);
    }

    public static T ReadBodyPayload<T>(NaradPersistedEvent persistedEvent, JsonTypeInfo<T> typeInfo)
    {
        JsonElement body = NaradPersistedEventReader.ParseBody(persistedEvent);
        return JsonSerializer.Deserialize(body, typeInfo)
            ?? throw new InvalidOperationException($"Failed to deserialize Narad body for '{persistedEvent.EventType}'.");
    }

    public static GatewayChatStreamDone BuildTerminalDone(
        NaradPersistedEvent persistedEvent,
        string targetType,
        string targetId,
        string sessionId)
    {
        JsonElement body = NaradPersistedEventReader.ParseBody(persistedEvent);
        // Prefer terminal error message, then channel-facing response stamps (top-level or nested).
        string message = NaradPersistedEventReader.String(body, "message")
            ?? NaradPersistedEventReader.String(body, "response")
            ?? string.Empty;
        if (string.IsNullOrWhiteSpace(message)
            && body.TryGetProperty("result", out JsonElement resultElement)
            && resultElement.TryGetProperty("response", out JsonElement responseElement)
            && responseElement.ValueKind == JsonValueKind.String)
        {
            message = responseElement.GetString() ?? string.Empty;
        }
        string? code = NaradPersistedEventReader.String(body, "code");
        GatewayRunState? runState = persistedEvent.EventType switch
        {
            NaradEventTypes.RunPaused => new GatewayRunState(GatewayRunPhases.AwaitingToolApproval),
            NaradEventTypes.RunCancelled => new GatewayRunState(GatewayRunPhases.Stopped, ErrorCode: code, ErrorMessage: message),
            NaradEventTypes.RunError => new GatewayRunState(GatewayRunPhases.Failed, ErrorCode: code, ErrorMessage: message),
            _ => new GatewayRunState(GatewayRunPhases.Completed),
        };
        long promptTokens = 0;
        long completionTokens = 0;
        if (NaradPersistedEventReader.TryGetUsage(persistedEvent, out long prompt, out long completion))
        {
            promptTokens = prompt;
            completionTokens = completion;
        }

        return new GatewayChatStreamDone(new GatewayChatResult(
            targetType,
            targetId,
            sessionId,
            message,
            persistedEvent.EventType == NaradEventTypes.RunFinished,
            new GatewayMetrics((int)promptTokens, (int)completionTokens, 0),
            Run: runState));
    }

    public static string? TryReadRunErrorMessage(NaradPersistedEvent persistedEvent)
    {
        if (!string.Equals(persistedEvent.EventType, NaradEventTypes.RunError, StringComparison.Ordinal))
        {
            return null;
        }

        return ReadBodyString(persistedEvent, "message");
    }

    public static NaradPersistedEvent CreateSynthetic(
        string eventId,
        string eventType,
        string sessionId,
        string runId,
        string rootRunId,
        JsonElement body,
        long sessionSequence = 1,
        long? sequence = 1)
    {
        var envelope = new System.Text.Json.Nodes.JsonObject
        {
            ["type"] = eventType,
            ["protocolVersion"] = NaradProtocolVersions.V1,
            ["eventId"] = eventId,
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
            ["sessionId"] = sessionId,
            ["sessionSequence"] = sessionSequence,
            ["rootRunId"] = rootRunId,
            ["runId"] = runId,
            ["sequence"] = sequence,
        };
        if (body.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in body.EnumerateObject())
            {
                if (!envelope.ContainsKey(property.Name))
                {
                    envelope[property.Name] = System.Text.Json.Nodes.JsonNode.Parse(property.Value.GetRawText());
                }
            }
        }

        string eventJson = NaradEventJsonGuard.ValidatePersistedEventJson(envelope.ToJsonString(), "synthetic narad event");
        return new NaradPersistedEvent(
            eventId,
            NaradProtocolVersions.V1,
            eventType,
            sessionId,
            sessionSequence,
            NaradRecordClasses.Authority,
            eventJson,
            DateTimeOffset.UtcNow.ToString("O"),
            rootRunId,
            runId,
            sequence);
    }
}
