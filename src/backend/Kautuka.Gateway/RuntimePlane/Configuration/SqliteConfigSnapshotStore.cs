using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.RuntimePlane.Configuration;

internal sealed class SqliteConfigSnapshotStore(KautukaDatabase database) : IConfigSnapshotStore
{
    public async Task<ConfigSnapshot> EnsurePersistedAsync(ConfigSnapshot snapshot, CancellationToken ct)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        long? existingRevision = await TryGetRevisionByHashAsync(connection, transaction, snapshot.ContentHash, ct).ConfigureAwait(false);
        if (existingRevision is long revision)
        {
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            ConfigSnapshot? existing = await GetAsync(revision, ct).ConfigureAwait(false);
            return existing ?? snapshot with { Revision = revision };
        }

        long newRevision = await InsertAsync(connection, transaction, snapshot, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return snapshot with { Revision = newRevision };
    }

    public async Task<ConfigSnapshot?> GetAsync(long revision, CancellationToken ct)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT revision, content_hash, payload_json, captured_at
            FROM config_snapshots
            WHERE revision = $revision
            """;
        cmd.Parameters.AddWithValue("$revision", revision);
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return ReadRow(reader);
    }

    public async Task<bool> ExistsAsync(long revision, string contentHash, CancellationToken ct)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText =
            """
            SELECT 1 FROM config_snapshots
            WHERE revision = $revision AND content_hash = $hash
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$revision", revision);
        cmd.Parameters.AddWithValue("$hash", contentHash);
        object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is not null;
    }

    private static async Task<long?> TryGetRevisionByHashAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string contentHash,
        CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            """
            SELECT revision FROM config_snapshots
            WHERE content_hash = $hash
            ORDER BY revision DESC
            LIMIT 1
            """;
        cmd.Parameters.AddWithValue("$hash", contentHash);
        object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is null or DBNull ? null : Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static async Task<long> InsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ConfigSnapshot snapshot,
        CancellationToken ct)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            """
            INSERT INTO config_snapshots (content_hash, payload_json, captured_at)
            VALUES ($hash, $payload, $capturedAt);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$hash", snapshot.ContentHash);
        cmd.Parameters.AddWithValue("$payload", snapshot.PayloadJson);
        cmd.Parameters.AddWithValue("$capturedAt", snapshot.CapturedAt.ToString("O"));
        object? result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private static ConfigSnapshot ReadRow(SqliteDataReader reader)
    {
        long revision = reader.GetInt64(0);
        string hash = reader.GetString(1);
        string payload = reader.GetString(2);
        DateTimeOffset capturedAt = DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture);
        return new ConfigSnapshot(revision, hash, payload, capturedAt);
    }
}

internal sealed class GatewayConfigSnapshotCapture(
    GatewayState gatewayState,
    ConfigCaptureGate captureGate) : IConfigSnapshotCapture
{
    public async Task<ConfigSnapshot> CaptureCurrentAsync(CancellationToken ct)
    {
        await captureGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string payloadJson = SerializeControlPlane(gatewayState);
            string contentHash = ComputeHash(payloadJson);
            return new ConfigSnapshot(
                Revision: 0,
                ContentHash: contentHash,
                PayloadJson: payloadJson,
                CapturedAt: DateTimeOffset.UtcNow);
        }
        finally
        {
            captureGate.Release();
        }
    }

    private static string SerializeControlPlane(GatewayState state)
    {
        StringBuilder builder = new();
        builder.Append("{\"capturedAt\":\"").Append(DateTimeOffset.UtcNow.ToString("O")).Append("\",\"agents\":[");
        bool first = true;
        foreach (GatewayAgent agent in state.Agents.Values)
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            builder.Append("{\"id\":\"").Append(EscapeJson(agent.Id))
                .Append("\",\"name\":\"").Append(EscapeJson(agent.Name))
                .Append("\",\"modelId\":\"").Append(EscapeJson(agent.ModelId))
                .Append("\",\"enabled\":").Append(agent.Enabled ? "true" : "false")
                .Append('}');
        }

        builder.Append("]}");
        return builder.ToString();
    }

    private static string EscapeJson(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string ComputeHash(string payloadJson)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(payloadJson));
        return Convert.ToHexString(hash);
    }
}

/// <summary>Serializes control-plane config capture vs settings writes.</summary>
internal sealed class ConfigCaptureGate : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public Task WaitAsync(CancellationToken ct) => gate.WaitAsync(ct);

    public void Release() => gate.Release();

    public void Dispose() => gate.Dispose();
}
