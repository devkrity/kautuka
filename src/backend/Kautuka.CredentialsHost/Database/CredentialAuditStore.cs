using System.Globalization;

using Microsoft.Data.Sqlite;

namespace Kautuka.CredentialsHost.Database;

internal readonly record struct CredentialAuditEvent(
    string CorrelationId,
    string ActorId,
    string Action,
    string Scope,
    string Subject,
    string KeyId,
    string Outcome);

internal sealed class CredentialAuditStore(CredentialsDatabase database)
{
    public async Task WriteAsync(
        CredentialAuditEvent auditEvent,
        CancellationToken cancellationToken,
        SqliteConnection? connection = null,
        SqliteTransaction? transaction = null)
    {
        bool ownsConnection = connection is null;
        connection ??= await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO credential_audit(timestamp, correlation_id, actor_id, action, scope, subject, key_id, outcome) VALUES ($timestamp, $correlationId, $actorId, $action, $scope, $subject, $keyId, $outcome)";
            command.Parameters.AddWithValue("$timestamp", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$correlationId", auditEvent.CorrelationId);
            command.Parameters.AddWithValue("$actorId", auditEvent.ActorId);
            command.Parameters.AddWithValue("$action", auditEvent.Action);
            command.Parameters.AddWithValue("$scope", auditEvent.Scope);
            command.Parameters.AddWithValue("$subject", auditEvent.Subject);
            command.Parameters.AddWithValue("$keyId", auditEvent.KeyId);
            command.Parameters.AddWithValue("$outcome", auditEvent.Outcome);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (ownsConnection) await connection.DisposeAsync().ConfigureAwait(false);
        }
    }
}
