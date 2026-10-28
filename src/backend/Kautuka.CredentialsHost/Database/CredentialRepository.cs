using System.Globalization;

using Grpc.Core;

using Kautuka.CredentialsHost.Security;

using Microsoft.Data.Sqlite;

namespace Kautuka.CredentialsHost.Database;

internal readonly record struct StoredCredential(string Key, string EncryptedValue);

internal sealed record CredentialMutation(
    string OperationId,
    string ActorId,
    string Scope,
    string Subject,
    IReadOnlyDictionary<string, string>? Entries,
    string? DeleteKey,
    CredentialAuditEvent AuditEvent);

internal sealed class CredentialRepository(
    CredentialsDatabase database,
    CredentialsVaultService vault,
    CredentialAuditStore audit)
{
    public async Task<(IReadOnlyList<StoredCredential> User, IReadOnlyList<StoredCredential> System)> ResolveAsync(
        string userId,
        string keysJson,
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<StoredCredential> user = await ReadUserStoredAsync(connection, userId, keysJson, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<StoredCredential> system = await ReadSystemStoredAsync(connection, keysJson, cancellationToken).ConfigureAwait(false);
        return (user, system);
    }

    public async Task<StoredCredential?> GetSystemAsync(string key, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT key, encrypted_value FROM system_credentials WHERE key = $key";
        command.Parameters.AddWithValue("$key", key);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new StoredCredential(reader.GetString(0), reader.GetString(1))
            : null;
    }

    private static async Task<IReadOnlyList<StoredCredential>> ReadUserStoredAsync(
        SqliteConnection connection,
        string userId,
        string keysJson,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT key, encrypted_value FROM user_credentials WHERE user_id = $userId AND key IN (SELECT value FROM json_each($keysJson))";
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$keysJson", keysJson);
        return await ReadRowsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<StoredCredential>> ReadSystemStoredAsync(
        SqliteConnection connection,
        string keysJson,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT key, encrypted_value FROM system_credentials WHERE key IN (SELECT value FROM json_each($keysJson))";
        command.Parameters.AddWithValue("$keysJson", keysJson);
        return await ReadRowsAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<StoredCredential>> ReadRowsAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<StoredCredential>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(new StoredCredential(reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    public async Task<IReadOnlyList<string>> ListAsync(string scope, string subject, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        if (scope == "user")
        {
            command.CommandText = "SELECT key FROM user_credentials WHERE user_id = $userId ORDER BY key";
            command.Parameters.AddWithValue("$userId", subject);
        }
        else
        {
            command.CommandText = "SELECT key FROM system_credentials ORDER BY key";
        }
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var keys = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) keys.Add(reader.GetString(0));
        return keys;
    }

    public async Task<int> MutateAsync(CredentialMutation mutation, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (SqliteCommand replayCommand = connection.CreateCommand())
        {
            replayCommand.Transaction = transaction;
            replayCommand.CommandText = "SELECT result_count FROM credential_mutation_receipts WHERE operation_id = $operationId";
            replayCommand.Parameters.AddWithValue("$operationId", mutation.OperationId);
            object? replay = await replayCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (replay is not null && replay is not DBNull) return Convert.ToInt32(replay, CultureInfo.InvariantCulture);
        }

        int result;
        if (mutation.Entries is not null)
        {
            string now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            foreach ((string key, string value) in mutation.Entries)
            {
                string encrypted = vault.Encrypt(value, new CredentialEnvelopeIdentity(mutation.Scope, mutation.Subject, key));
                string sql = mutation.Scope == "user"
                    ? "INSERT INTO user_credentials(user_id,key,encrypted_value,created_at,updated_at) VALUES($subject,$key,$encrypted,$now,$now) ON CONFLICT(user_id,key) DO UPDATE SET encrypted_value=excluded.encrypted_value,updated_at=excluded.updated_at"
                    : "INSERT INTO system_credentials(key,encrypted_value,created_at,updated_at) VALUES($key,$encrypted,$now,$now) ON CONFLICT(key) DO UPDATE SET encrypted_value=excluded.encrypted_value,updated_at=excluded.updated_at";
                await using SqliteCommand writeCommand = connection.CreateCommand();
                writeCommand.Transaction = transaction;
                writeCommand.CommandText = sql;
                if (mutation.Scope == "user") writeCommand.Parameters.AddWithValue("$subject", mutation.Subject);
                writeCommand.Parameters.AddWithValue("$key", key);
                writeCommand.Parameters.AddWithValue("$encrypted", encrypted);
                writeCommand.Parameters.AddWithValue("$now", now);
                await writeCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            result = mutation.Entries.Count;
            await EnforceStorageQuotaAsync(connection, transaction, mutation.Scope, mutation.Subject, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            string sql = mutation.Scope == "user"
                ? "DELETE FROM user_credentials WHERE user_id=$subject AND key=$key"
                : "DELETE FROM system_credentials WHERE key=$key";
            await using SqliteCommand deleteCommand = connection.CreateCommand();
            deleteCommand.Transaction = transaction;
            deleteCommand.CommandText = sql;
            if (mutation.Scope == "user") deleteCommand.Parameters.AddWithValue("$subject", mutation.Subject);
            deleteCommand.Parameters.AddWithValue("$key", mutation.DeleteKey!);
            result = await deleteCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await EnforceMutationRateAsync(connection, transaction, mutation.ActorId, cancellationToken).ConfigureAwait(false);
        string createdAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        await using (SqliteCommand receiptCommand = connection.CreateCommand())
        {
            receiptCommand.Transaction = transaction;
            receiptCommand.CommandText = "INSERT INTO credential_mutation_receipts(operation_id,actor_id,result_count,created_at) VALUES($operationId,$actorId,$result,$createdAt)";
            receiptCommand.Parameters.AddWithValue("$operationId", mutation.OperationId);
            receiptCommand.Parameters.AddWithValue("$actorId", mutation.ActorId);
            receiptCommand.Parameters.AddWithValue("$result", result);
            receiptCommand.Parameters.AddWithValue("$createdAt", createdAt);
            await receiptCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await audit.WriteAsync(mutation.AuditEvent, cancellationToken, connection, transaction).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static async Task EnforceMutationRateAsync(SqliteConnection connection, SqliteTransaction transaction, string actorId, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM credential_mutation_receipts WHERE actor_id=$actorId AND created_at>=$cutoff";
        command.Parameters.AddWithValue("$actorId", actorId);
        command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O", CultureInfo.InvariantCulture));
        int count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (count >= 60) throw new RpcException(new Status(StatusCode.ResourceExhausted, "Credential mutation rate exceeded."));
    }

    private static async Task EnforceStorageQuotaAsync(SqliteConnection connection, SqliteTransaction transaction, string scope, string subject, CancellationToken cancellationToken)
    {
        string sql = scope == "user"
            ? "SELECT COUNT(*) FROM user_credentials WHERE user_id=$subject"
            : "SELECT COUNT(*) FROM system_credentials";
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        if (scope == "user") command.Parameters.AddWithValue("$subject", subject);
        int count = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        int maximum = scope == "user" ? 256 : 1024;
        if (count > maximum) throw new RpcException(new Status(StatusCode.ResourceExhausted, "Credential storage quota exceeded."));
    }
}
