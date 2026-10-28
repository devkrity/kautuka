using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Execution;

using Narad.Contracts;

namespace Kautuka.Gateway.RuntimePlane.Projection;

/// <summary>
/// Redacts operator-only and sensitive Narad authority events for public session streams.
/// </summary>
internal static class NaradPublicAuthorityFilter
{
    private const int maxEventJsonLength = 512 * 1024;

    private static readonly HashSet<string> alwaysSuppressedEventTypes = new(StringComparer.Ordinal)
    {
        NaradEventTypes.TraceEvent,
        NaradEventTypes.SessionRevision,
    };

    public static bool TryBuildPublicPayload(NaradPersistedEvent persistedEvent, out JsonObject payload)
    {
        payload = new JsonObject();
        if (alwaysSuppressedEventTypes.Contains(persistedEvent.EventType))
        {
            return false;
        }

        if (string.Equals(persistedEvent.EventType, NaradEventTypes.ProgressRecorded, StringComparison.Ordinal)
            && !IsPublicProgress(persistedEvent))
        {
            return false;
        }

        string validated = NaradStreamObservation.ValidateEventJson(persistedEvent);
        if (validated.Length > maxEventJsonLength)
        {
            throw new NaradProjectionException($"Narad event '{persistedEvent.EventId}' exceeds maximum payload size.");
        }

        payload = JsonNode.Parse(validated)!.AsObject();
        // Supply the omitted null for legacy unsuccessful completions; preserve identity and never synthesize successful results.
        if (persistedEvent.EventType == NaradEventTypes.ToolCallFinished && !payload.ContainsKey("response")
            && payload["outcome"]?.GetValue<string>() is "cancelled" or "error") payload["response"] = null;
        RedactProtectedFields(payload);
        return true;
    }

    private static bool IsPublicProgress(NaradPersistedEvent persistedEvent)
    {
        string privacy = NaradEventJsonParser.String(persistedEvent, "privacy") ?? "public";
        return string.Equals(privacy, "public", StringComparison.Ordinal);
    }

    private static void RedactProtectedFields(JsonObject payload)
    {
        payload.Remove("actorId");
        payload.Remove("userId");
        payload.Remove("ownerUserId");
        payload.Remove("createdById");

        if (payload["progress"] is JsonObject progress)
        {
            progress.Remove("internal");
            progress.Remove("trace");
            progress.Remove("debug");
        }
    }
}
