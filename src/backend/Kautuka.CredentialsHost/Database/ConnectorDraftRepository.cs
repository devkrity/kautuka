using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

using Grpc.Core;

using Kautuka.CredentialsHost.Security;

using Microsoft.Data.Sqlite;

namespace Kautuka.CredentialsHost.Database;

[JsonSerializable(typeof(string[]))]
internal sealed partial class ConnectorDraftJsonContext : JsonSerializerContext;

internal sealed record ConnectorDraftRecord(string Id, string Actor, string ConnectorId, string Scope, string Candidate, string BaseUpdatedAt, string EffectiveEndpoint, string BaseEndpoint, bool UseDurable, string CredentialNamespace, string[] Declared, string[] Removed, long ExpiresAt, string SourceAssociationId = "");

// CredentialsHost stores both the opaque non-secret draft metadata and encrypted temporary values.
internal sealed class ConnectorDraftRepository(CredentialsDatabase database, CredentialsVaultService vault, TimeProvider timeProvider)
{
    private long Now => timeProvider.GetUtcNow().ToUnixTimeSeconds();
    private static string Json(string[] value) => JsonSerializer.Serialize(value, ConnectorDraftJsonContext.Default.StringArray);
    private static string[] ReadJson(string value) => JsonSerializer.Deserialize(value, ConnectorDraftJsonContext.Default.StringArray) ?? [];

    public ConnectorDraftRecord Create(string id, string actor, string connectorId, string scope, string candidate, string baseUpdatedAt, string effectiveEndpoint, string baseEndpoint, bool useDurable, string credentialNamespace, string[] declared, IReadOnlyDictionary<string, string> entries, string[] removed, string sourceAssociationId = "")
    {
        using SqliteConnection connection = database.OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        Cleanup(connection, transaction, Now);
        using (SqliteCommand quota = connection.CreateCommand())
        {
            quota.Transaction = transaction;
            quota.CommandText = "SELECT COUNT(*) FROM connector_credential_drafts WHERE actor_id=$actor AND completed=0 AND expires_at>$now";
            quota.Parameters.AddWithValue("$actor", actor); quota.Parameters.AddWithValue("$now", Now);
            if (Convert.ToInt64(quota.ExecuteScalar(), CultureInfo.InvariantCulture) >= 5)
                throw new RpcException(new Status(StatusCode.ResourceExhausted, "At most five connector drafts are allowed."));
        }
        using (SqliteCommand rate = connection.CreateCommand())
        {
            rate.Transaction = transaction;
            rate.CommandText = "SELECT COUNT(*) FROM credential_mutation_receipts WHERE actor_id=$actor AND created_at >= $cutoff";
            rate.Parameters.AddWithValue("$actor", actor);
            rate.Parameters.AddWithValue("$cutoff", timeProvider.GetUtcNow().AddMinutes(-1).ToString("O", CultureInfo.InvariantCulture));
            if (Convert.ToInt64(rate.ExecuteScalar(), CultureInfo.InvariantCulture) >= 60)
                throw new RpcException(new Status(StatusCode.ResourceExhausted, "Credential mutation rate exceeded."));
        }
        long expires = Now + 900;
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO connector_credential_drafts(id,actor_id,connector_id,credential_scope,candidate_json,base_updated_at,effective_endpoint,base_endpoint,use_durable,credential_namespace,declared_keys_json,remove_keys_json,expires_at) VALUES($id,$actor,$connector,$scope,$candidate,$base,$endpoint,$baseEndpoint,$useDurable,$namespace,$declared,$removed,$expires)";
            command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$actor", actor); command.Parameters.AddWithValue("$connector", connectorId);
            command.Parameters.AddWithValue("$scope", scope); command.Parameters.AddWithValue("$candidate", candidate); command.Parameters.AddWithValue("$base", baseUpdatedAt);
            command.Parameters.AddWithValue("$endpoint", effectiveEndpoint); command.Parameters.AddWithValue("$baseEndpoint", baseEndpoint); command.Parameters.AddWithValue("$useDurable", useDurable ? 1 : 0); command.Parameters.AddWithValue("$namespace", credentialNamespace); command.Parameters.AddWithValue("$declared", Json(declared)); command.Parameters.AddWithValue("$removed", Json(removed)); command.Parameters.AddWithValue("$expires", expires);
            command.ExecuteNonQuery();
        }
        foreach ((string key, string value) in entries)
        {
            using SqliteCommand command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "INSERT INTO connector_credential_draft_entries(draft_id,key,encrypted_value) VALUES($id,$key,$value)";
            command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", vault.Encrypt(value, new CredentialEnvelopeIdentity("draft", id, key))); command.ExecuteNonQuery();
        }
        using (SqliteCommand source = connection.CreateCommand())
        {
            source.Transaction = transaction; source.CommandText = "INSERT INTO connector_draft_sources(draft_id,association_id) VALUES($id,$source)";
            source.Parameters.AddWithValue("$id", id); source.Parameters.AddWithValue("$source", sourceAssociationId); source.ExecuteNonQuery();
        }
        using (SqliteCommand receipt = connection.CreateCommand())
        {
            receipt.Transaction = transaction;
            receipt.CommandText = "INSERT INTO credential_mutation_receipts(operation_id,actor_id,result_count,created_at) VALUES($id,$actor,$count,$created)";
            receipt.Parameters.AddWithValue("$id", "draft-create:" + id); receipt.Parameters.AddWithValue("$actor", actor);
            receipt.Parameters.AddWithValue("$count", entries.Count); receipt.Parameters.AddWithValue("$created", timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)); receipt.ExecuteNonQuery();
        }
        WriteAudit(connection, transaction, id, actor, "draft-create", scope, connectorId);
        transaction.Commit();
        return new(id, actor, connectorId, scope, candidate, baseUpdatedAt, effectiveEndpoint, baseEndpoint, useDurable, credentialNamespace, declared, removed, expires, sourceAssociationId);
    }

    public IReadOnlyList<ConnectorDraftRecord> ListPending(string actor)
    {
        using SqliteConnection connection = database.OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        long now = Now;
        var ids = new List<string>();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT id FROM connector_credential_drafts WHERE actor_id=$actor AND completed=0 AND expires_at>$now ORDER BY expires_at,id";
            command.Parameters.AddWithValue("$actor", actor); command.Parameters.AddWithValue("$now", now);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read()) ids.Add(reader.GetString(0));
        }
        return [.. ids.Select(id => Read(connection, transaction, id, actor, now))];
    }

    public ConnectorDraftRecord Get(string id, string actor)
    {
        using SqliteConnection connection = database.OpenConnection();
        return Read(connection, null, id, actor, Now);
    }

    internal static ConnectorDraftRecord Read(SqliteConnection connection, SqliteTransaction? transaction, string id, string actor, long now)
    {
        using SqliteCommand command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT id,actor_id,connector_id,credential_scope,candidate_json,base_updated_at,effective_endpoint,base_endpoint,use_durable,credential_namespace,declared_keys_json,remove_keys_json,expires_at,COALESCE((SELECT association_id FROM connector_draft_sources WHERE draft_id=connector_credential_drafts.id),'') FROM connector_credential_drafts WHERE id=$id AND actor_id=$actor AND expires_at>$now";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$actor", actor); command.Parameters.AddWithValue("$now", now);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) throw new RpcException(new Status(StatusCode.NotFound, "Connector draft was not found or expired."));
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetInt32(8) != 0, reader.GetString(9), ReadJson(reader.GetString(10)), ReadJson(reader.GetString(11)), reader.GetInt64(12), reader.GetString(13));
    }

    public Dictionary<string, string> Resolve(string id, string actor, string[] required, bool includeSystem)
    {
        using SqliteConnection connection = database.OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        ConnectorDraftRecord draft = Read(connection, transaction, id, actor, Now);
        if (required.Any(key => !draft.Declared.Contains(key, StringComparer.Ordinal))) throw new RpcException(new Status(StatusCode.PermissionDenied, "Credential is not declared by the draft."));
        if (draft.UseDurable && draft.SourceAssociationId.Length != 0)
        {
            using SqliteCommand source = connection.CreateCommand(); source.Transaction = transaction;
            source.CommandText = "SELECT COUNT(*) FROM connector_credential_associations WHERE id=$id AND connector_id=$connector AND credential_namespace=$namespace";
            source.Parameters.AddWithValue("$id", draft.SourceAssociationId); source.Parameters.AddWithValue("$connector", draft.ConnectorId); source.Parameters.AddWithValue("$namespace", draft.CredentialNamespace);
            if (Convert.ToInt64(source.ExecuteScalar(), CultureInfo.InvariantCulture) != 1) throw new RpcException(new Status(StatusCode.FailedPrecondition, "Credential association is unavailable."));
        }
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string key in required.Distinct(StringComparer.Ordinal))
        {
            string? user = draft.UseDurable && draft.SourceAssociationId.Length != 0 ? ReadAssociationValue(connection, transaction, draft.SourceAssociationId, "user", actor, key) : null;
            string? system = draft.UseDurable && includeSystem && draft.SourceAssociationId.Length != 0 ? ReadAssociationValue(connection, transaction, draft.SourceAssociationId, "system", string.Empty, key) : null;
            string? temporary = ReadValue(connection, transaction, "SELECT encrypted_value FROM connector_credential_draft_entries WHERE draft_id=$actor AND key=$key", id, key);
            bool removed = draft.Removed.Contains(key, StringComparer.Ordinal);
            if (draft.Scope == "user") { if (temporary is not null) user = temporary; else if (removed) user = null; }
            else { if (temporary is not null) system = temporary; else if (removed) system = null; }
            string? selected = user ?? system;
            if (selected is null) continue;
            bool temp = temporary is not null && ((draft.Scope == "user" && user is not null) || (draft.Scope == "system" && user is null && system is not null));
            string scope = temp ? "draft" : user is not null ? "user" : "system";
            string subject = temp ? id : user is not null ? actor : string.Empty;
            CredentialEnvelopeIdentity identity = temp ? new CredentialEnvelopeIdentity(scope, subject, key) : ConnectorAssociationRepository.Identity(draft.SourceAssociationId, scope, subject, key);
            result[key] = vault.Decrypt(selected, identity);
        }
        return result;
    }

    private static string? ReadAssociationValue(SqliteConnection connection, SqliteTransaction transaction, string association, string scope, string subject, string key)
    {
        using SqliteCommand command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT encrypted_value FROM connector_credential_association_entries WHERE association_id=$id AND scope=$scope AND subject=$subject AND key=$key";
        command.Parameters.AddWithValue("$id", association); command.Parameters.AddWithValue("$scope", scope); command.Parameters.AddWithValue("$subject", subject); command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Callers select only fixed SQL statements; values are parameterized.")]
    private static string? ReadValue(SqliteConnection connection, SqliteTransaction transaction, string sql, string subject, string key)
    {
        using SqliteCommand command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        if (sql.Contains("$actor", StringComparison.Ordinal)) command.Parameters.AddWithValue("$actor", subject);
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public bool Cancel(string id, string actor)
    {
        using SqliteConnection connection = database.OpenConnection(); using SqliteTransaction transaction = connection.BeginTransaction();
        string? connector = null; string? scope = null;
        using (SqliteCommand lookup = connection.CreateCommand())
        {
            lookup.Transaction = transaction;
            lookup.CommandText = "SELECT connector_id,credential_scope FROM connector_credential_drafts WHERE id=$id AND actor_id=$actor UNION ALL SELECT connector_id,credential_scope FROM connector_association_draft_metadata WHERE draft_id=$id AND actor_id=$actor LIMIT 1";
            lookup.Parameters.AddWithValue("$id", id); lookup.Parameters.AddWithValue("$actor", actor);
            using SqliteDataReader reader = lookup.ExecuteReader();
            if (reader.Read()) { connector = reader.GetString(0); scope = reader.GetString(1); }
        }
        if (connector is null || scope is null) return false;
        using SqliteCommand command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "DELETE FROM connector_association_draft_metadata WHERE draft_id=$id AND actor_id=$actor; DELETE FROM connector_credential_draft_entries WHERE draft_id=$id AND EXISTS (SELECT 1 FROM connector_credential_drafts WHERE id=$id AND actor_id=$actor); DELETE FROM connector_draft_sources WHERE draft_id=$id AND EXISTS (SELECT 1 FROM connector_credential_drafts WHERE id=$id AND actor_id=$actor); DELETE FROM connector_credential_drafts WHERE id=$id AND actor_id=$actor";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$actor", actor); bool deleted = command.ExecuteNonQuery() > 0;
        WriteAudit(connection, transaction, id, actor, "draft-cancel", scope, connector);
        transaction.Commit(); return deleted;
    }

    public void DiscardInput(string id, string actor)
    {
        using SqliteConnection connection = database.OpenConnection(); using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "DELETE FROM connector_credential_draft_entries WHERE draft_id=$id AND EXISTS (SELECT 1 FROM connector_credential_drafts WHERE id=$id AND actor_id=$actor); DELETE FROM connector_draft_sources WHERE draft_id=$id AND EXISTS (SELECT 1 FROM connector_credential_drafts WHERE id=$id AND actor_id=$actor); DELETE FROM connector_credential_drafts WHERE id=$id AND actor_id=$actor";
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$actor", actor); command.ExecuteNonQuery(); transaction.Commit();
    }
    private void WriteAudit(SqliteConnection connection, SqliteTransaction transaction, string id, string actor, string action, string scope, string connectorId)
    {
        using SqliteCommand command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO credential_audit(timestamp,correlation_id,actor_id,action,scope,subject,key_id,outcome) VALUES($time,$id,$actor,$action,$scope,$subject,$key,'success')";
        command.Parameters.AddWithValue("$time", timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)); command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$actor", actor); command.Parameters.AddWithValue("$action", action); command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$subject", connectorId); command.Parameters.AddWithValue("$key", "batch"); command.ExecuteNonQuery();
    }
    public void CleanupExpired() { using SqliteConnection connection = database.OpenConnection(); using SqliteTransaction transaction = connection.BeginTransaction(); Cleanup(connection, transaction, Now); transaction.Commit(); }
    private static void Cleanup(SqliteConnection connection, SqliteTransaction transaction, long now)
    {
        using SqliteCommand command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "DELETE FROM connector_credential_draft_entries WHERE draft_id IN (SELECT id FROM connector_credential_drafts WHERE expires_at <= $now); DELETE FROM connector_credential_drafts WHERE expires_at <= $now";
        command.Parameters.AddWithValue("$now", now); command.ExecuteNonQuery();
    }
}
