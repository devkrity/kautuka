using System.Globalization;
using System.Text.Json;

using Grpc.Core;

using Kautuka.CredentialsHost.Security;

using Microsoft.Data.Sqlite;

namespace Kautuka.CredentialsHost.Database;

// Staging never changes the active credential records. The Gateway commits the returned
// association ID with its configuration in a separate, single activation transaction.
internal sealed class ConnectorAssociationRepository(CredentialsDatabase database, CredentialsVaultService vault, TimeProvider clock)
{
    private sealed record Entry(string Scope, string Subject, string Key, string Value);

    internal static CredentialEnvelopeIdentity Identity(string association, string scope, string subject, string key)
        => new("association-" + scope, association + ":" + subject, key);

    public string StageMutation(string operation, string actor, string connector, string credentialNamespace, string sourceId, string scope, IReadOnlyDictionary<string, string> changes, IReadOnlyList<string> removals)
    {
        using SqliteConnection connection = database.OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using (SqliteCommand existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT a.id,a.actor_id,a.connector_id,a.credential_namespace,a.source_id,a.credential_scope,d.id FROM connector_credential_associations a LEFT JOIN connector_credential_drafts d ON d.staged_association_id=a.id WHERE a.operation_id=$operation";
            existing.Parameters.AddWithValue("$operation", operation);
            using SqliteDataReader reader = existing.ExecuteReader();
            if (reader.Read())
            {
                if (reader.GetString(1) != actor || reader.GetString(2) != connector || reader.GetString(3) != credentialNamespace || reader.GetString(4) != sourceId || reader.GetString(5) != scope || !reader.IsDBNull(6))
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, "Association operation does not match its original request."));
                return reader.GetString(0);
            }
        }
        string prefix = "__connector_remote__:" + credentialNamespace + ":";
        string overrideKey = "__connector_http_url__:" + credentialNamespace + ":" + connector;
        if (scope is not ("user" or "system") || changes.Keys.Concat(removals).Any(key => !key.StartsWith(prefix, StringComparison.Ordinal) && !(scope == "user" && key == overrideKey)))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid connector credential mutation."));
        var entries = new List<Entry>();
        if (sourceId.Length != 0)
        {
            ValidateAssociation(connection, sourceId, connector, credentialNamespace, transaction);
            using SqliteCommand read = connection.CreateCommand(); read.Transaction = transaction;
            read.CommandText = "SELECT scope,subject,key,encrypted_value FROM connector_credential_association_entries WHERE association_id=$source";
            read.Parameters.AddWithValue("$source", sourceId);
            using SqliteDataReader reader = read.ExecuteReader();
            while (reader.Read()) entries.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }
        string association = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO connector_credential_associations(id,operation_id,actor_id,connector_id,credential_namespace,source_id,credential_scope) VALUES($id,$operation,$actor,$connector,$namespace,$source,$scope)";
            insert.Parameters.AddWithValue("$id", association); insert.Parameters.AddWithValue("$operation", operation); insert.Parameters.AddWithValue("$actor", actor);
            insert.Parameters.AddWithValue("$connector", connector); insert.Parameters.AddWithValue("$namespace", credentialNamespace); insert.Parameters.AddWithValue("$source", sourceId); insert.Parameters.AddWithValue("$scope", scope); insert.ExecuteNonQuery();
        }
        string subject = scope == "user" ? actor : string.Empty;
        foreach (Entry entry in entries)
        {
            if (entry.Scope == scope && entry.Subject == subject && removals.Contains(entry.Key, StringComparer.Ordinal)) continue;
            Write(connection, transaction, association, entry.Scope, entry.Subject, entry.Key, vault.Decrypt(entry.Value, Identity(sourceId, entry.Scope, entry.Subject, entry.Key)));
        }
        foreach ((string key, string value) in changes) Write(connection, transaction, association, scope, subject, key, value);
        using (SqliteCommand audit = connection.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText = "INSERT INTO credential_audit(timestamp,correlation_id,actor_id,action,scope,subject,key_id,outcome) VALUES($timestamp,$id,$actor,'association-stage',$scope,$subject,'batch','success')";
            audit.Parameters.AddWithValue("$timestamp", clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)); audit.Parameters.AddWithValue("$id", operation);
            audit.Parameters.AddWithValue("$actor", actor); audit.Parameters.AddWithValue("$scope", scope); audit.Parameters.AddWithValue("$subject", connector); audit.ExecuteNonQuery();
        }
        transaction.Commit();
        return association;
    }

    public string Stage(string draftId, string actor, string sourceId)
    {
        using SqliteConnection connection = database.OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using (SqliteCommand existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT a.id,a.actor_id,a.source_id,d.id FROM connector_credential_associations a LEFT JOIN connector_credential_drafts d ON d.staged_association_id=a.id WHERE a.operation_id=$operation";
            existing.Parameters.AddWithValue("$operation", draftId);
            using SqliteDataReader reader = existing.ExecuteReader();
            if (reader.Read())
            {
                if (!string.Equals(reader.GetString(1), actor, StringComparison.Ordinal) || !string.Equals(reader.GetString(2), sourceId, StringComparison.Ordinal) || reader.IsDBNull(3) || reader.GetString(3) != draftId)
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, "Association operation does not match its original request."));
                return reader.GetString(0);
            }
        }
        ConnectorDraftRecord draft = ConnectorDraftRepository.Read(connection, transaction, draftId, actor, clock.GetUtcNow().ToUnixTimeSeconds());
        if (!string.Equals(draft.SourceAssociationId, sourceId, StringComparison.Ordinal))
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "Draft source association changed."));
        string association = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        string prefix = "__connector_remote__:" + draft.CredentialNamespace + ":";
        string overrideKey = "__connector_http_url__:" + draft.CredentialNamespace + ":" + draft.ConnectorId;
        var entries = new List<Entry>();
        if (sourceId.Length != 0)
        {
            using SqliteCommand read = connection.CreateCommand();
            read.Transaction = transaction;
            using SqliteCommand source = connection.CreateCommand(); source.Transaction = transaction;
            source.CommandText = "SELECT COUNT(*) FROM connector_credential_associations WHERE id=$id AND connector_id=$connector AND credential_namespace=$namespace";
            source.Parameters.AddWithValue("$id", sourceId); source.Parameters.AddWithValue("$connector", draft.ConnectorId); source.Parameters.AddWithValue("$namespace", draft.CredentialNamespace);
            if (Convert.ToInt64(source.ExecuteScalar(), CultureInfo.InvariantCulture) != 1) throw new RpcException(new Status(StatusCode.FailedPrecondition, "Source association does not match the connector."));
            read.CommandText = "SELECT scope,subject,key,encrypted_value FROM connector_credential_association_entries WHERE association_id=$source";
            read.Parameters.AddWithValue("$source", sourceId);
            using SqliteDataReader reader = read.ExecuteReader();
            while (reader.Read()) entries.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }
        using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO connector_credential_associations(id,operation_id,actor_id,connector_id,credential_namespace,source_id,credential_scope) VALUES($id,$operation,$actor,$connector,$namespace,$source,$scope)";
            insert.Parameters.AddWithValue("$id", association); insert.Parameters.AddWithValue("$operation", draftId); insert.Parameters.AddWithValue("$actor", actor);
            insert.Parameters.AddWithValue("$connector", draft.ConnectorId); insert.Parameters.AddWithValue("$namespace", draft.CredentialNamespace); insert.Parameters.AddWithValue("$source", sourceId); insert.Parameters.AddWithValue("$scope", draft.Scope); insert.ExecuteNonQuery();
        }
        using (SqliteCommand metadata = connection.CreateCommand())
        {
            metadata.Transaction = transaction;
            metadata.CommandText = "UPDATE connector_credential_drafts SET staged_association_id=$association WHERE id=$draft AND actor_id=$actor AND staged_association_id IS NULL";
            metadata.Parameters.AddWithValue("$association", association); metadata.Parameters.AddWithValue("$draft", draftId); metadata.Parameters.AddWithValue("$actor", actor);
            if (metadata.ExecuteNonQuery() != 1) throw new InvalidOperationException("Staged draft metadata disappeared.");
        }
        string changedSubject = draft.Scope == "user" ? actor : string.Empty;
        var mutations = new List<(string Key, string Action, string Outcome)>();
        foreach (string key in draft.Removed)
        {
            bool existed = entries.Any(entry => entry.Scope == draft.Scope && entry.Subject == changedSubject && entry.Key == key);
            mutations.Add((key, "credential-remove", existed ? "success" : "unchanged"));
        }
        foreach (Entry entry in entries)
        {
            if (entry.Scope == draft.Scope && entry.Subject == changedSubject && draft.Removed.Contains(entry.Key, StringComparer.Ordinal)) continue;
            CredentialEnvelopeIdentity oldIdentity = Identity(sourceId, entry.Scope, entry.Subject, entry.Key);
            Write(connection, transaction, association, entry.Scope, entry.Subject, entry.Key, vault.Decrypt(entry.Value, oldIdentity));
        }
        using (SqliteCommand read = connection.CreateCommand())
        {
            read.Transaction = transaction; read.CommandText = "SELECT key,encrypted_value FROM connector_credential_draft_entries WHERE draft_id=$id"; read.Parameters.AddWithValue("$id", draftId);
            using SqliteDataReader reader = read.ExecuteReader();
            while (reader.Read())
            {
                string key = reader.GetString(0);
                if (!draft.Declared.Contains(key, StringComparer.Ordinal) || !(key.StartsWith(prefix, StringComparison.Ordinal) || (draft.Scope == "user" && key == overrideKey)))
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, "Draft credential does not match its connector."));
                bool existed = entries.Any(entry => entry.Scope == draft.Scope && entry.Subject == changedSubject && entry.Key == key);
                mutations.Add((key, existed ? "credential-replace" : "credential-add", "success"));
                Write(connection, transaction, association, draft.Scope, changedSubject, key, vault.Decrypt(reader.GetString(1), new CredentialEnvelopeIdentity("draft", draftId, key)));
            }
        }
        using (SqliteCommand audit = connection.CreateCommand())
        {
            audit.Transaction = transaction;
            audit.CommandText = "INSERT INTO credential_audit(timestamp,correlation_id,actor_id,action,scope,subject,key_id,outcome) VALUES($timestamp,$id,$actor,'association-stage',$scope,$subject,'batch','success')";
            audit.Parameters.AddWithValue("$timestamp", clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
            audit.Parameters.AddWithValue("$id", draftId); audit.Parameters.AddWithValue("$actor", actor);
            audit.Parameters.AddWithValue("$scope", draft.Scope); audit.Parameters.AddWithValue("$subject", draft.ConnectorId); audit.ExecuteNonQuery();
        }
        foreach ((string key, string action, string outcome) in mutations)
        {
            using SqliteCommand audit = connection.CreateCommand(); audit.Transaction = transaction;
            audit.CommandText = "INSERT INTO credential_audit(timestamp,correlation_id,actor_id,action,scope,subject,key_id,outcome) VALUES($timestamp,$id,$actor,$action,$scope,$subject,$key,$outcome)";
            audit.Parameters.AddWithValue("$timestamp", clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)); audit.Parameters.AddWithValue("$id", draftId);
            audit.Parameters.AddWithValue("$actor", actor); audit.Parameters.AddWithValue("$action", action); audit.Parameters.AddWithValue("$scope", draft.Scope);
            audit.Parameters.AddWithValue("$subject", draft.ConnectorId); audit.Parameters.AddWithValue("$key", key); audit.Parameters.AddWithValue("$outcome", outcome); audit.ExecuteNonQuery();
        }
        transaction.Commit();
        return association;
    }

    public ConnectorDraftRecord GetStagedDraft(string draftId, string actor)
    {
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT d.id,d.actor_id,d.connector_id,d.credential_scope,d.candidate_json,d.base_updated_at,d.effective_endpoint,d.base_endpoint,d.use_durable,d.credential_namespace,d.declared_keys_json,d.remove_keys_json,d.expires_at,d.source_association_id FROM connector_credential_drafts d JOIN connector_credential_associations a ON a.id=d.staged_association_id AND a.operation_id=d.id AND a.actor_id=d.actor_id WHERE d.id=$id AND d.actor_id=$actor";
        command.Parameters.AddWithValue("$id", draftId); command.Parameters.AddWithValue("$actor", actor);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) throw new RpcException(new Status(StatusCode.NotFound, "Staged connector draft was not found."));
        string[] declared = JsonSerializer.Deserialize(reader.GetString(10), ConnectorDraftJsonContext.Default.StringArray) ?? [];
        string[] removed = JsonSerializer.Deserialize(reader.GetString(11), ConnectorDraftJsonContext.Default.StringArray) ?? [];
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetInt32(8) != 0, reader.GetString(9), declared, removed, reader.GetInt64(12), reader.GetString(13));
    }

    private void Write(SqliteConnection connection, SqliteTransaction transaction, string association, string scope, string subject, string key, string value)
    {
        using SqliteCommand write = connection.CreateCommand(); write.Transaction = transaction;
        write.CommandText = "INSERT INTO connector_credential_association_entries(association_id,scope,subject,key,encrypted_value) VALUES($id,$scope,$subject,$key,$value) ON CONFLICT(association_id,scope,subject,key) DO UPDATE SET encrypted_value=excluded.encrypted_value";
        write.Parameters.AddWithValue("$id", association); write.Parameters.AddWithValue("$scope", scope); write.Parameters.AddWithValue("$subject", subject); write.Parameters.AddWithValue("$key", key);
        write.Parameters.AddWithValue("$value", vault.Encrypt(value, Identity(association, scope, subject, key))); write.ExecuteNonQuery();
    }

    public Dictionary<string, string> Resolve(string association, string connector, string credentialNamespace, string actor, IEnumerable<string> keys, bool includeSystem)
    {
        using SqliteConnection connection = database.OpenConnection();
        ValidateAssociation(connection, association, connector, credentialNamespace);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string key in keys.Distinct(StringComparer.Ordinal))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT scope,subject,encrypted_value FROM connector_credential_association_entries WHERE association_id=$id AND key=$key AND ((scope='user' AND subject=$actor) OR (scope='system' AND $system=1)) ORDER BY CASE scope WHEN 'user' THEN 0 ELSE 1 END LIMIT 1";
            command.Parameters.AddWithValue("$id", association); command.Parameters.AddWithValue("$key", key); command.Parameters.AddWithValue("$actor", actor); command.Parameters.AddWithValue("$system", includeSystem ? 1 : 0);
            using SqliteDataReader reader = command.ExecuteReader();
            if (reader.Read()) result.Add(key, vault.Decrypt(reader.GetString(2), Identity(association, reader.GetString(0), reader.GetString(1), key)));
        }
        return result;
    }

    public (List<string> UserKeys, List<string> SystemKeys) GetPresence(string association, string connector, string credentialNamespace, string actor, IEnumerable<string> keys, bool includeSystem)
    {
        using SqliteConnection connection = database.OpenConnection();
        ValidateAssociation(connection, association, connector, credentialNamespace);
        var user = new List<string>(); var system = new List<string>();
        foreach (string key in keys.Distinct(StringComparer.Ordinal))
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT scope FROM connector_credential_association_entries WHERE association_id=$id AND key=$key AND ((scope='user' AND subject=$actor) OR (scope='system' AND $system=1))";
            command.Parameters.AddWithValue("$id", association); command.Parameters.AddWithValue("$key", key); command.Parameters.AddWithValue("$actor", actor); command.Parameters.AddWithValue("$system", includeSystem ? 1 : 0);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read()) { if (reader.GetString(0) == "user") user.Add(key); else system.Add(key); }
        }
        return (user, system);
    }

    private static void ValidateAssociation(SqliteConnection connection, string association, string connector, string credentialNamespace, SqliteTransaction? transaction = null)
    {
        using (SqliteCommand exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT COUNT(*) FROM connector_credential_associations WHERE id=$id AND connector_id=$connector AND credential_namespace=$namespace";
            exists.Parameters.AddWithValue("$id", association);
            exists.Parameters.AddWithValue("$connector", connector); exists.Parameters.AddWithValue("$namespace", credentialNamespace);
            if (Convert.ToInt64(exists.ExecuteScalar(), CultureInfo.InvariantCulture) != 1)
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "Credential association is unavailable."));
        }
    }
}
