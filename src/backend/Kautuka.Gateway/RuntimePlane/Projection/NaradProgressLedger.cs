using System.Text.Json;
using System.Text.Json.Nodes;

using Narad.Contracts;

namespace Kautuka.Gateway.RuntimePlane.Projection;

internal sealed record NaradProgressEntry(
    string ProgressId,
    string Category,
    string Label,
    string Status,
    string Privacy,
    string? Detail,
    long SessionSequence,
    long RunSequence,
    string RootRunId,
    string RunId);

internal static class NaradProgressLedger
{
    public static IReadOnlyList<NaradProgressEntry> Fold(IEnumerable<NaradPersistedEvent> sessionHistory)
    {
        var entries = new Dictionary<string, NaradProgressEntry>(StringComparer.Ordinal);
        foreach (NaradPersistedEvent persistedEvent in sessionHistory.OrderBy(static item => item.SessionSequence))
        {
            if (!string.Equals(persistedEvent.EventType, NaradEventTypes.ProgressRecorded, StringComparison.Ordinal))
            {
                continue;
            }

            JsonElement body = NaradEventJsonParser.ParseBody(persistedEvent);
            string? progressId = NaradEventJsonParser.String(body, "progressId");
            if (string.IsNullOrWhiteSpace(progressId))
            {
                continue;
            }

            entries[progressId] = new NaradProgressEntry(
                progressId,
                NaradEventJsonParser.String(body, "category") ?? string.Empty,
                NaradEventJsonParser.String(body, "label") ?? string.Empty,
                NaradEventJsonParser.String(body, "status") ?? "active",
                NaradEventJsonParser.String(body, "privacy") ?? "public",
                NaradEventJsonParser.String(body, "detail"),
                persistedEvent.SessionSequence,
                persistedEvent.Sequence ?? 0,
                persistedEvent.RootRunId ?? string.Empty,
                persistedEvent.RunId ?? string.Empty);
        }

        return entries.Values
            .OrderBy(static entry => entry.SessionSequence)
            .ToArray();
    }

    public static JsonArray ToJsonArray(IReadOnlyList<NaradProgressEntry> entries)
        => new([.. entries.Select(static entry => new JsonObject
        {
            ["progressId"] = entry.ProgressId,
            ["category"] = entry.Category,
            ["label"] = entry.Label,
            ["status"] = entry.Status,
            ["privacy"] = entry.Privacy,
            ["detail"] = entry.Detail,
            ["sessionSequence"] = entry.SessionSequence,
            ["runSequence"] = entry.RunSequence,
            ["rootRunId"] = entry.RootRunId,
            ["runId"] = entry.RunId,
        })]);
}
