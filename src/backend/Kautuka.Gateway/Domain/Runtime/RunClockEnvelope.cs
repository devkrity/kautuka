using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kautuka.Gateway.Domain.Runtime;

internal readonly record struct PackedToolResult(string ResultJson, bool JournaledStart);

/// <summary>
/// Canonical max-run checkpoint for a parked run. Stored beside effective-behavior JSON on the
/// wait-state row that already owns the HITL pause — one clock for outer runs and AAAT children,
/// restored after process death by <c>StreamChatWithApprovalAsync</c>.
/// </summary>
internal static class RunClockEnvelope
{
    private const string elapsedProperty = "kautukaActiveElapsedMs";
    private const string behaviorProperty = "effectiveBehavior";
    private const string packedApprovalRequestIdsProperty = "kautukaPackedApprovalRequestIds";
    private const string packedToolResultsProperty = "kautukaPackedToolResults";

    public static string Wrap(
        string behaviorJson,
        TimeSpan activeElapsed,
        IReadOnlyList<string>? packedApprovalRequestIds = null,
        IReadOnlyDictionary<string, PackedToolResult>? packedToolResults = null)
    {
        long milliseconds = (long)Math.Max(0, activeElapsed.TotalMilliseconds);
        JsonNode behaviorNode;
        try
        {
            behaviorNode = JsonNode.Parse(string.IsNullOrWhiteSpace(behaviorJson) ? "{}" : behaviorJson)
                ?? new JsonObject();
        }
        catch (JsonException)
        {
            behaviorNode = new JsonObject();
        }

        if (behaviorNode is JsonObject existing
            && existing.ContainsKey(elapsedProperty)
            && existing.ContainsKey(behaviorProperty))
        {
            existing[elapsedProperty] = milliseconds;
            WritePackedIds(existing, packedApprovalRequestIds, preserveExisting: packedApprovalRequestIds is null);
            WritePackedResults(existing, packedToolResults, preserveExisting: packedToolResults is null);
            return existing.ToJsonString();
        }

        var envelope = new JsonObject
        {
            [behaviorProperty] = behaviorNode,
            [elapsedProperty] = milliseconds,
        };
        WritePackedIds(envelope, packedApprovalRequestIds, preserveExisting: false);
        WritePackedResults(envelope, packedToolResults, preserveExisting: false);
        return envelope.ToJsonString();
    }

    public static string PutPackedToolResult(
        string? stored,
        string requestId,
        string resultJson,
        bool journaledStart)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(resultJson);
        Dictionary<string, PackedToolResult> results = [];
        if (TryReadPackedToolResults(stored, out IReadOnlyDictionary<string, PackedToolResult> existing))
        {
            foreach ((string id, PackedToolResult result) in existing)
            {
                results[id] = result;
            }
        }

        results[requestId] = new PackedToolResult(resultJson, journaledStart);
        if (TryRead(stored, out _, out TimeSpan elapsed))
        {
            TryReadPackedApprovalRequestIds(stored, out IReadOnlyList<string> packedIds);
            // Rewrap the full envelope so packed ids stay on the root object even when
            // the inner effective-behavior JSON is passed around as the clock payload.
            return Wrap(
                stored!,
                elapsed,
                packedIds.Count > 0 ? packedIds : null,
                results);
        }

        return Wrap("{}", TimeSpan.Zero, packedApprovalRequestIds: null, results);
    }

    public static (string BehaviorJson, TimeSpan Elapsed) Unwrap(string? stored)
    {
        if (TryRead(stored, out string behaviorJson, out TimeSpan elapsed))
        {
            return (behaviorJson, elapsed);
        }

        return (string.IsNullOrWhiteSpace(stored) ? "{}" : stored, TimeSpan.Zero);
    }

    /// <summary>
    /// True when <paramref name="stored"/> is an envelope, including an explicit zero elapsed.
    /// Raw effective-behavior JSON (no checkpoint) returns false so callers can keep in-memory elapsed.
    /// </summary>
    public static bool TryReadElapsed(string? stored, out TimeSpan elapsed)
        => TryRead(stored, out _, out elapsed);

    public static bool TryReadPackedApprovalRequestIds(string? stored, out IReadOnlyList<string> packedRequestIds)
    {
        packedRequestIds = [];
        if (string.IsNullOrWhiteSpace(stored))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(stored);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(packedApprovalRequestIdsProperty, out JsonElement packed)
                || packed.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var ids = new List<string>();
            foreach (JsonElement item in packed.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String
                    && item.GetString() is { Length: > 0 } id)
                {
                    ids.Add(id);
                }
            }

            packedRequestIds = ids;
            return ids.Count > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryReadPackedToolResults(
        string? stored,
        out IReadOnlyDictionary<string, PackedToolResult> packedResults)
    {
        packedResults = new Dictionary<string, PackedToolResult>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(stored))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(stored);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(packedToolResultsProperty, out JsonElement packed)
                || packed.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var results = new Dictionary<string, PackedToolResult>(StringComparer.Ordinal);
            foreach (JsonProperty property in packed.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object
                    || !property.Value.TryGetProperty("resultJson", out JsonElement resultJson)
                    || resultJson.ValueKind != JsonValueKind.String
                    || resultJson.GetString() is not { Length: > 0 } json)
                {
                    continue;
                }

                bool journaledStart = property.Value.TryGetProperty("journaledStart", out JsonElement journaled)
                    && journaled.ValueKind is JsonValueKind.True;
                results[property.Name] = new PackedToolResult(json, journaledStart);
            }

            packedResults = results;
            return results.Count > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryRead(string? stored, out string behaviorJson, out TimeSpan elapsed)
    {
        behaviorJson = "{}";
        elapsed = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(stored))
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(stored);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(elapsedProperty, out JsonElement elapsedElement)
                || !root.TryGetProperty(behaviorProperty, out JsonElement behavior))
            {
                return false;
            }

            long milliseconds = elapsedElement.TryGetInt64(out long value) ? Math.Max(0, value) : 0;
            behaviorJson = behavior.GetRawText();
            elapsed = TimeSpan.FromMilliseconds(milliseconds);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void WritePackedIds(
        JsonObject envelope,
        IReadOnlyList<string>? packedApprovalRequestIds,
        bool preserveExisting)
    {
        if (packedApprovalRequestIds is { Count: > 0 })
        {
            var array = new JsonArray();
            foreach (string requestId in packedApprovalRequestIds)
            {
                if (!string.IsNullOrWhiteSpace(requestId))
                {
                    array.Add((JsonNode?)JsonValue.Create(requestId));
                }
            }

            envelope[packedApprovalRequestIdsProperty] = array;
            return;
        }

        if (!preserveExisting)
        {
            envelope.Remove(packedApprovalRequestIdsProperty);
        }
    }

    private static void WritePackedResults(
        JsonObject envelope,
        IReadOnlyDictionary<string, PackedToolResult>? packedToolResults,
        bool preserveExisting)
    {
        if (packedToolResults is { Count: > 0 })
        {
            var results = new JsonObject();
            foreach ((string requestId, PackedToolResult result) in packedToolResults)
            {
                if (string.IsNullOrWhiteSpace(requestId) || string.IsNullOrWhiteSpace(result.ResultJson))
                {
                    continue;
                }

                JsonNode resultNode = new JsonObject
                {
                    ["resultJson"] = result.ResultJson,
                    ["journaledStart"] = result.JournaledStart,
                };
                results[requestId] = resultNode;
            }

            envelope[packedToolResultsProperty] = results;
            return;
        }

        if (!preserveExisting)
        {
            envelope.Remove(packedToolResultsProperty);
        }
    }
}
