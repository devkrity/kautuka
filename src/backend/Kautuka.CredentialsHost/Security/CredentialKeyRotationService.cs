using System.Globalization;

using Kautuka.CredentialsHost.Database;

using Microsoft.Data.Sqlite;

namespace Kautuka.CredentialsHost.Security;

internal readonly record struct CredentialKeyRotationProgress(
    string TargetKeyId,
    string Status,
    string LastScope,
    string LastSubject,
    string LastKey,
    long ProcessedCount);

internal sealed class CredentialKeyRotationService(
    CredentialsDatabase database,
    CredentialKeyRing keyRing,
    CredentialsVaultService vault,
    CredentialAuditStore audit)
{
    private const int batchSize = 64;

    public async Task<CredentialKeyRotationProgress> BeginOrResumeAsync(CancellationToken cancellationToken)
    {
        CredentialKeyRotationProgress? existing = await ReadProgressAsync(cancellationToken).ConfigureAwait(false);
        if (existing.HasValue) return existing.Value;

        string targetKeyId = keyRing.Rotate();
        string now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO credential_key_rotation(singleton_id,target_key_id,status,last_scope,last_subject,last_key,processed_count,started_at,updated_at) VALUES(1,$target,'running','','','',0,$now,$now)";
        command.Parameters.AddWithValue("$target", targetKeyId);
        command.Parameters.AddWithValue("$now", now);
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return new CredentialKeyRotationProgress(targetKeyId, "running", string.Empty, string.Empty, string.Empty, 0);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
        {
            return (await ReadProgressAsync(cancellationToken).ConfigureAwait(false))
                ?? throw new InvalidOperationException("Credential rotation checkpoint could not be created.", exception);
        }
    }

    public async Task<bool> ProcessNextBatchAsync(CancellationToken cancellationToken)
    {
        CredentialKeyRotationProgress progress = await BeginOrResumeAsync(cancellationToken).ConfigureAwait(false);
        if (progress.Status == "completed") return false;

        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        CredentialKeyRotationProgress current = await ReadProgressAsync(connection, transaction, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Credential rotation checkpoint disappeared.");
        if (current.Status == "completed") return false;

        List<RotationRow> rows = await ReadBatchAsync(connection, transaction, current, cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            await VerifyAllRowsUseTargetAsync(connection, transaction, current.TargetKeyId, cancellationToken).ConfigureAwait(false);
            await SetCompletedAsync(connection, transaction, current.TargetKeyId, cancellationToken).ConfigureAwait(false);
            await audit.WriteAsync(new CredentialAuditEvent(
                current.TargetKeyId, "appliance", "rotate", "vault", string.Empty, current.TargetKeyId, "success"),
                cancellationToken, connection, transaction).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            keyRing.RetireAllExcept(current.TargetKeyId);
            return false;
        }

        foreach (RotationRow row in rows)
        {
            var identity = new CredentialEnvelopeIdentity(row.Scope, row.Subject, row.Key);
            string plaintext = vault.Decrypt(row.EncryptedValue, identity);
            string replacement = vault.Encrypt(plaintext, identity);
            try
            {
                await UpdateRowAsync(connection, transaction, row, replacement, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // Strings cannot be cleared; do not retain plaintext beyond this batch iteration.
                plaintext = string.Empty;
            }
        }

        RotationRow last = rows[^1];
        await using (SqliteCommand checkpoint = connection.CreateCommand())
        {
            checkpoint.Transaction = transaction;
            checkpoint.CommandText =
                "UPDATE credential_key_rotation SET last_scope=$scope,last_subject=$subject,last_key=$key,processed_count=processed_count+$count,updated_at=$now WHERE singleton_id=1 AND target_key_id=$target AND status='running'";
            checkpoint.Parameters.AddWithValue("$scope", last.Scope);
            checkpoint.Parameters.AddWithValue("$subject", last.Subject);
            checkpoint.Parameters.AddWithValue("$key", last.Key);
            checkpoint.Parameters.AddWithValue("$count", rows.Count);
            checkpoint.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            checkpoint.Parameters.AddWithValue("$target", current.TargetKeyId);
            if (await checkpoint.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
                throw new InvalidOperationException("Credential rotation checkpoint update lost ownership.");
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<CredentialKeyRotationProgress> RunToCompletionAsync(CancellationToken cancellationToken)
    {
        CredentialKeyRotationProgress? initial = await ReadProgressAsync(cancellationToken).ConfigureAwait(false);
        if (initial is { Status: "completed" })
        {
            bool retirementWasPending = keyRing.KeyCount > 1;
            keyRing.RetireAllExcept(initial.Value.TargetKeyId);
            if (retirementWasPending) return initial.Value;
            await DeleteCompletedCheckpointAsync(initial.Value.TargetKeyId, cancellationToken).ConfigureAwait(false);
        }
        while (await ProcessNextBatchAsync(cancellationToken).ConfigureAwait(false)) { }
        CredentialKeyRotationProgress progress = (await ReadProgressAsync(cancellationToken).ConfigureAwait(false))
            ?? throw new InvalidOperationException("Credential rotation completed without a durable checkpoint.");
        if (progress.Status != "completed") throw new InvalidOperationException("Credential rotation did not reach its terminal checkpoint.");
        keyRing.RetireAllExcept(progress.TargetKeyId);
        return progress;
    }

    private async Task DeleteCompletedCheckpointAsync(string targetKeyId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM credential_key_rotation WHERE singleton_id=1 AND target_key_id=$target AND status='completed'";
        command.Parameters.AddWithValue("$target", targetKeyId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<CredentialKeyRotationProgress?> ReadProgressAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadProgressAsync(connection, null, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CredentialKeyRotationProgress?> ReadProgressAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT target_key_id,status,last_scope,last_subject,last_key,processed_count FROM credential_key_rotation WHERE singleton_id=1";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new CredentialKeyRotationProgress(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetInt64(5))
            : null;
    }

    private static async Task<List<RotationRow>> ReadBatchAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CredentialKeyRotationProgress progress,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT scope, subject, key, encrypted_value
            FROM (
                SELECT 'system' AS scope, '' AS subject, key, encrypted_value FROM system_credentials
                UNION ALL
                SELECT 'user' AS scope, user_id AS subject, key, encrypted_value FROM user_credentials
            )
            WHERE scope > $scope
               OR (scope = $scope AND subject > $subject)
               OR (scope = $scope AND subject = $subject AND key > $key)
            ORDER BY scope, subject, key
            LIMIT 64
            """;
        command.Parameters.AddWithValue("$scope", progress.LastScope);
        command.Parameters.AddWithValue("$subject", progress.LastSubject);
        command.Parameters.AddWithValue("$key", progress.LastKey);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<RotationRow>(batchSize);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(new RotationRow(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        return rows;
    }

    private static async Task UpdateRowAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RotationRow row,
        string replacement,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        if (row.Scope == "user")
        {
            command.CommandText = "UPDATE user_credentials SET encrypted_value=$value,updated_at=$now WHERE user_id=$subject AND key=$key";
            command.Parameters.AddWithValue("$subject", row.Subject);
        }
        else
        {
            command.CommandText = "UPDATE system_credentials SET encrypted_value=$value,updated_at=$now WHERE key=$key";
        }
        command.Parameters.AddWithValue("$value", replacement);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$key", row.Key);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Credential disappeared during key rotation.");
    }

    private static async Task VerifyAllRowsUseTargetAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string targetKeyId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT encrypted_value FROM system_credentials UNION ALL SELECT encrypted_value FROM user_credentials";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!AesGcmEnvelope.TryDeserialize(reader.GetString(0), out SecretEnvelope? envelope)
                || envelope is null
                || !string.Equals(envelope.KeyId, targetKeyId, StringComparison.Ordinal))
                throw new CredentialIntegrityException("Credential rotation verification found a non-target envelope.");
        }
    }

    private static async Task SetCompletedAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string targetKeyId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE credential_key_rotation SET status='completed',updated_at=$now WHERE singleton_id=1 AND target_key_id=$target AND status='running'";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$target", targetKeyId);
        if (await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1)
            throw new InvalidOperationException("Credential rotation terminal checkpoint lost ownership.");
    }

    private readonly record struct RotationRow(string Scope, string Subject, string Key, string EncryptedValue);
}
