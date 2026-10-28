using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Narad.Contracts;

namespace Kautuka.Gateway.RuntimePlane.Projection;

internal sealed record NaradTranscriptMessage(
    string MessageId,
    string Role,
    string Content,
    long SessionSequence,
    bool Completed,
    string? MessageScope = null,
    string? RootRunId = null,
    string? RunId = null,
    string? Timestamp = null,
    IReadOnlyList<string>? AttachmentIds = null);

/// <summary>
/// Folds public conversation authority into a hydrate <c>messages.snapshot</c>.
/// Stamps run lineage so reloaded clients can bind tools/activity to turns the
/// same way live stream events do (authority events carry rootRunId/runId).
/// </summary>
internal static class NaradMessagesSnapshotBuilder
{
    public static JsonObject Build(
        string sessionId,
        IReadOnlyList<NaradPersistedEvent> sessionHistory,
        DateTimeOffset timestamp)
    {
        var messages = new List<NaradTranscriptMessage>();
        var messageBuffers = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        var messageRoles = new Dictionary<string, string>(StringComparer.Ordinal);
        var messageScopes = new Dictionary<string, string>(StringComparer.Ordinal);
        var messageRootRuns = new Dictionary<string, string>(StringComparer.Ordinal);
        var messageRuns = new Dictionary<string, string>(StringComparer.Ordinal);
        var messageTimestamps = new Dictionary<string, string>(StringComparer.Ordinal);
        var messageStartSequence = new Dictionary<string, long>(StringComparer.Ordinal);
        long conversationCoversThrough = 0;

        foreach (NaradPersistedEvent persistedEvent in sessionHistory.OrderBy(static item => item.SessionSequence))
        {
            JsonElement body = NaradEventJsonParser.ParseBody(persistedEvent);
            switch (persistedEvent.EventType)
            {
                case NaradEventTypes.MessageUserRecorded:
                    {
                        conversationCoversThrough = Math.Max(conversationCoversThrough, persistedEvent.SessionSequence);
                        string? messageId = NaradEventJsonParser.String(body, "messageId");
                        string? content = NaradEventJsonParser.String(body, "content");
                        if (!string.IsNullOrWhiteSpace(messageId) && content is not null)
                        {
                            messages.Add(new NaradTranscriptMessage(
                                messageId,
                                "user",
                                content,
                                persistedEvent.SessionSequence,
                                Completed: true,
                                NaradEventJsonParser.String(body, "messageScope"),
                                persistedEvent.RootRunId,
                                persistedEvent.RunId,
                                persistedEvent.OccurredAt,
                                NaradEventJsonParser.StringList(body, "attachmentIds")));
                        }

                        break;
                    }
                case NaradEventTypes.MessageTextStarted:
                    {
                        string? messageId = NaradEventJsonParser.String(body, "messageId");
                        if (string.IsNullOrWhiteSpace(messageId))
                        {
                            break;
                        }

                        conversationCoversThrough = Math.Max(conversationCoversThrough, persistedEvent.SessionSequence);
                        messageBuffers[messageId] = new StringBuilder();
                        messageRoles[messageId] = NaradEventJsonParser.String(body, "role") ?? "assistant";
                        messageScopes[messageId] = NaradEventJsonParser.String(body, "messageScope") ?? "run";
                        messageStartSequence[messageId] = persistedEvent.SessionSequence;
                        if (!string.IsNullOrWhiteSpace(persistedEvent.RootRunId))
                        {
                            messageRootRuns[messageId] = persistedEvent.RootRunId!;
                        }

                        if (!string.IsNullOrWhiteSpace(persistedEvent.RunId))
                        {
                            messageRuns[messageId] = persistedEvent.RunId!;
                        }

                        if (!string.IsNullOrWhiteSpace(persistedEvent.OccurredAt))
                        {
                            messageTimestamps[messageId] = persistedEvent.OccurredAt;
                        }

                        break;
                    }
                case NaradEventTypes.MessageTextDelta:
                    {
                        string? messageId = NaradEventJsonParser.String(body, "messageId");
                        string? delta = NaradEventJsonParser.String(body, "delta");
                        if (!string.IsNullOrWhiteSpace(messageId)
                            && delta is not null
                            && messageBuffers.TryGetValue(messageId, out StringBuilder? buffer)
                            && buffer is not null)
                        {
                            conversationCoversThrough = Math.Max(conversationCoversThrough, persistedEvent.SessionSequence);
                            buffer.Append(delta);
                        }

                        break;
                    }
                case NaradEventTypes.MessageTextCompleted:
                    {
                        string? messageId = NaradEventJsonParser.String(body, "messageId");
                        if (string.IsNullOrWhiteSpace(messageId)
                            || !messageBuffers.Remove(messageId, out StringBuilder? buffer)
                            || buffer is null)
                        {
                            break;
                        }

                        conversationCoversThrough = Math.Max(conversationCoversThrough, persistedEvent.SessionSequence);
                        messages.Add(new NaradTranscriptMessage(
                            messageId,
                            messageRoles.GetValueOrDefault(messageId, "assistant"),
                            buffer.ToString(),
                            messageStartSequence.GetValueOrDefault(messageId, persistedEvent.SessionSequence),
                            Completed: true,
                            messageScopes.GetValueOrDefault(messageId, "run"),
                            messageRootRuns.GetValueOrDefault(messageId) ?? persistedEvent.RootRunId,
                            messageRuns.GetValueOrDefault(messageId) ?? persistedEvent.RunId,
                            messageTimestamps.GetValueOrDefault(messageId) ?? persistedEvent.OccurredAt));
                        messageRoles.Remove(messageId);
                        messageScopes.Remove(messageId);
                        messageRootRuns.Remove(messageId);
                        messageRuns.Remove(messageId);
                        messageTimestamps.Remove(messageId);
                        messageStartSequence.Remove(messageId);
                        break;
                    }
            }
        }

        foreach (KeyValuePair<string, StringBuilder> inProgress in messageBuffers)
        {
            messages.Add(new NaradTranscriptMessage(
                inProgress.Key,
                messageRoles.GetValueOrDefault(inProgress.Key, "assistant"),
                inProgress.Value.ToString(),
                messageStartSequence.GetValueOrDefault(inProgress.Key, conversationCoversThrough),
                Completed: false,
                messageScopes.GetValueOrDefault(inProgress.Key, "run"),
                messageRootRuns.GetValueOrDefault(inProgress.Key),
                messageRuns.GetValueOrDefault(inProgress.Key),
                messageTimestamps.GetValueOrDefault(inProgress.Key)));
        }

        return new JsonObject
        {
            ["type"] = "messages.snapshot",
            ["protocolVersion"] = NaradProtocolVersions.V1,
            ["timestamp"] = timestamp.ToString("O"),
            ["snapshotId"] = $"messages:{sessionId}:{conversationCoversThrough}",
            ["sessionId"] = sessionId,
            ["coversThroughSessionSequence"] = conversationCoversThrough,
            ["messages"] = new JsonArray([.. messages
                .OrderBy(static message => message.SessionSequence)
                .ThenBy(static message => message.MessageId, StringComparer.Ordinal)
                .Select(static message => ToSnapshotRecord(message))]),
        };
    }

    private static JsonObject ToSnapshotRecord(NaradTranscriptMessage message)
    {
        var record = new JsonObject
        {
            ["messageId"] = message.MessageId,
            ["role"] = message.Role,
            ["content"] = message.Content,
            ["messageScope"] = message.MessageScope,
            ["sessionSequence"] = message.SessionSequence,
            ["completed"] = message.Completed,
        };
        if (!string.IsNullOrWhiteSpace(message.RootRunId))
        {
            record["rootRunId"] = message.RootRunId;
        }

        if (!string.IsNullOrWhiteSpace(message.RunId))
        {
            record["runId"] = message.RunId;
        }

        if (!string.IsNullOrWhiteSpace(message.Timestamp))
        {
            record["timestamp"] = message.Timestamp;
        }

        if (message.AttachmentIds is { Count: > 0 })
        {
            record["attachmentIds"] = new JsonArray([.. message.AttachmentIds.Select(static id => (JsonNode?)id)]);
        }

        return record;
    }
}
