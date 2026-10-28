using System.Buffers;
using System.Text.Json;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Infrastructure.Agents;

/// <summary>
/// CRUD service for the maf_sessions SQLite table.
/// Stores serialized AgentSession blobs keyed by (sessionId).
/// </summary>
internal sealed class MafSessionStore(KautukaDatabase db)
{
    // ME.AI JSON type discriminators for approval content items that must be
    // stripped before an approval resume.  FunctionInvokingChatClient strips
    // these from its in-memory message list when processing an approval response,
    // but it does NOT mutate agentSession.History (a separate object owned by
    // ChatClientAgent). As a result, stale approval requests accumulate in the
    // saved session JSON and trigger a validation error on the next resume:
    //   "ToolApprovalRequestContent found with ... that have no matching ToolApprovalResponseContent."
    // Stripping them here, before deserialization, keeps the session clean.
    private static readonly HashSet<string> approvalContentTypeDiscriminators =
        ["toolApprovalRequest", "toolApprovalResponse"];

    public async Task<JsonElement?> LoadAsync(string sessionId, CancellationToken ct)
    {
        using var connection = new SqliteConnection(db.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT stateJson FROM maf_sessions WHERE session_id = $sid";
        cmd.Parameters.AddWithValue("$sid", sessionId);
        object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (result is string json)
        {
            return JsonDocument.Parse(json).RootElement.Clone();
        }

        return null;
    }

    /// <summary>
    /// Loads the saved session and strips any <c>toolApprovalRequest</c> /
    /// <c>toolApprovalResponse</c> content items before returning the JSON element.
    /// Call this instead of <see cref="LoadAsync"/> when resuming an approval
    /// response so that stale pending-approval entries from previous approval
    /// cycles do not trigger FunctionInvokingChatClient's validation error.
    /// </summary>
    public async Task<JsonElement?> LoadCleanedForApprovalResumeAsync(string sessionId, CancellationToken ct)
    {
        JsonElement? stored = await LoadAsync(sessionId, ct).ConfigureAwait(false);
        if (!stored.HasValue)
            return null;

        return StripApprovalContentItems(stored.Value);
    }

    /// <summary>
    /// Removes approval content items from chat history messages only
    /// (<c>stateBag.InMemoryChatHistoryProvider.messages</c>). MAF bypass/binding
    /// state-bag entries that serialize as <c>toolApprovalRequest</c> must survive
    /// save/resume so packed non-HITL siblings can execute on the next FICC turn.
    /// </summary>
    internal static JsonElement StripApprovalContentItems(JsonElement element)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        WriteSessionPreservingNonHistoryApprovals(writer, element);
        writer.Flush();
        return JsonDocument.Parse(buffer.WrittenMemory).RootElement.Clone();
    }

    private static void WriteSessionPreservingNonHistoryApprovals(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            element.WriteTo(writer);
            return;
        }

        writer.WriteStartObject();
        foreach (JsonProperty property in element.EnumerateObject())
        {
            writer.WritePropertyName(property.Name);
            if (string.Equals(property.Name, "stateBag", StringComparison.Ordinal))
            {
                WriteStateBag(writer, property.Value);
            }
            else
            {
                property.Value.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
    }

    private static void WriteStateBag(Utf8JsonWriter writer, JsonElement stateBag)
    {
        if (stateBag.ValueKind != JsonValueKind.Object)
        {
            stateBag.WriteTo(writer);
            return;
        }

        writer.WriteStartObject();
        foreach (JsonProperty property in stateBag.EnumerateObject())
        {
            writer.WritePropertyName(property.Name);
            if (string.Equals(property.Name, "InMemoryChatHistoryProvider", StringComparison.Ordinal))
            {
                WriteHistoryProvider(writer, property.Value);
            }
            else
            {
                property.Value.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
    }

    private static void WriteHistoryProvider(Utf8JsonWriter writer, JsonElement historyProvider)
    {
        if (historyProvider.ValueKind != JsonValueKind.Object)
        {
            historyProvider.WriteTo(writer);
            return;
        }

        writer.WriteStartObject();
        foreach (JsonProperty property in historyProvider.EnumerateObject())
        {
            writer.WritePropertyName(property.Name);
            if (string.Equals(property.Name, "messages", StringComparison.Ordinal)
                && property.Value.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray();
                foreach (JsonElement message in property.Value.EnumerateArray())
                {
                    WriteHistoryMessage(writer, message);
                }

                writer.WriteEndArray();
            }
            else
            {
                property.Value.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
    }

    private static void WriteHistoryMessage(Utf8JsonWriter writer, JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            message.WriteTo(writer);
            return;
        }

        writer.WriteStartObject();
        foreach (JsonProperty property in message.EnumerateObject())
        {
            writer.WritePropertyName(property.Name);
            if ((string.Equals(property.Name, "contents", StringComparison.Ordinal)
                    || string.Equals(property.Name, "content", StringComparison.Ordinal))
                && property.Value.ValueKind == JsonValueKind.Array)
            {
                WriteStrippedContentArray(writer, property.Value);
            }
            else
            {
                property.Value.WriteTo(writer);
            }
        }

        writer.WriteEndObject();
    }

    private static void WriteStrippedContentArray(Utf8JsonWriter writer, JsonElement contents)
    {
        writer.WriteStartArray();
        foreach (JsonElement item in contents.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("$type", out JsonElement typeEl)
                && typeEl.ValueKind == JsonValueKind.String
                && approvalContentTypeDiscriminators.Contains(typeEl.GetString()!))
            {
                continue;
            }

            item.WriteTo(writer);
        }

        writer.WriteEndArray();
    }

    public async Task SaveAsync(
        string sessionId,
        string entityId,
        string entityType,
        JsonElement state,
        CancellationToken ct)
    {
        using var connection = new SqliteConnection(db.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO maf_sessions (session_id, entityId, entityType, stateJson, updated_at)
            VALUES ($sid, $eid, $etype, $state, $now)
            ON CONFLICT(session_id) DO UPDATE SET
                entityId   = excluded.entityId,
                entityType = excluded.entityType,
                stateJson  = excluded.stateJson,
                updated_at  = excluded.updated_at
            """;
        cmd.Parameters.AddWithValue("$sid", sessionId);
        cmd.Parameters.AddWithValue("$eid", entityId);
        cmd.Parameters.AddWithValue("$etype", entityType);
        cmd.Parameters.AddWithValue("$state", StripApprovalContentItems(state).GetRawText());
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string sessionId, CancellationToken ct)
    {
        using var connection = new SqliteConnection(db.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM maf_sessions WHERE session_id = $sid";
        cmd.Parameters.AddWithValue("$sid", sessionId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteManyAsync(IReadOnlyCollection<string> sessionIds, CancellationToken ct)
    {
        if (sessionIds.Count == 0)
        {
            return;
        }

        using var connection = new SqliteConnection(db.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
#pragma warning disable CA2007 // await using DbTransaction — no SynchronizationContext in ASP.NET Core
        await using System.Data.Common.DbTransaction transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
#pragma warning restore CA2007

        foreach (string sessionId in sessionIds)
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = (SqliteTransaction)transaction;
            cmd.CommandText = "DELETE FROM maf_sessions WHERE session_id = $sid";
            cmd.Parameters.AddWithValue("$sid", sessionId);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }
}
