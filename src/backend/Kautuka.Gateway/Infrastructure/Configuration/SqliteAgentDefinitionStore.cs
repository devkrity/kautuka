using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Memory;
using Kautuka.Gateway.Domain.Runtime;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Infrastructure.Configuration;

internal sealed partial class SqliteAgentDefinitionStore : IAtomicAgentDefinitionStore
{
    private const string agentDatabaseFileName = "agent.db";
    private const string runtimeMemoryEmbeddingsTableName = "runtime_memory_embeddings";
    private const string runtimeMemoryEmbeddingVectorTablePrefix = "runtime_memory_embeddings_vec_";
    private const string attachedAgentSchema = "agent_store";

    private readonly IConfiguration configuration;
    private readonly KautukaDatabase? database;

    public SqliteAgentDefinitionStore(IConfiguration configuration)
        : this(configuration, null)
    {
    }

    public SqliteAgentDefinitionStore(IConfiguration configuration, KautukaDatabase? database)
    {
        this.configuration = configuration;
        this.database = database;
    }

    public bool SupportsAtomicRegistryTransactions => database is not null;

    public string GetStorageLocatorKey(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        return agentId;
    }

    public long SaveDefinition(string storageLocatorKey, AgentDefinition definition, AgentDefinitionSnapshotMetadata? snapshotMetadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageLocatorKey);
        ArgumentNullException.ThrowIfNull(definition);

        using var connection = OpenConnection(storageLocatorKey);

        string now = DateTimeOffset.UtcNow.ToString("O");
        AgentDefinitionSerializer.SerializedAgentDefinition serialized = AgentDefinitionSerializer.Serialize(definition);

        using SqliteTransaction transaction = connection.BeginTransaction();
        long nextVersion = GetNextVersion(connection, transaction);

        using (SqliteCommand insertVersion = connection.CreateCommand())
        {
            insertVersion.Transaction = transaction;
            insertVersion.CommandText = """
                INSERT INTO agent_definition_versions
                    (version, agent_id, scope, enabled, name, model_id, system_prompt, super_exec_mode,
                     session_json, sandbox_json, mcp_bindings_json, skill_attachments_json, persona_json,
                     approval_config_json, tool_exposure_json, created_at)
                VALUES
                    ($version, $agentId, $scope, $enabled, $name, $modelId, $systemPrompt, $superExecMode,
                     $sessionJson, $sandboxJson, $mcpBindingsJson, $skillAttachmentsJson, $personaJson,
                     $approvalConfigJson, $toolExposureJson, $createdAt);
                """;
            insertVersion.Parameters.AddWithValue("$version", nextVersion);
            insertVersion.Parameters.AddWithValue("$agentId", serialized.Id);
            insertVersion.Parameters.AddWithValue("$scope", serialized.Scope);
            insertVersion.Parameters.AddWithValue("$enabled", serialized.Enabled);
            insertVersion.Parameters.AddWithValue("$name", serialized.Name);
            insertVersion.Parameters.AddWithValue("$modelId", serialized.ModelId);
            insertVersion.Parameters.AddWithValue("$systemPrompt", serialized.SystemPrompt);
            insertVersion.Parameters.AddWithValue("$superExecMode", serialized.SuperExecMode);
            insertVersion.Parameters.AddWithValue("$sessionJson", serialized.SessionJson);
            insertVersion.Parameters.AddWithValue("$sandboxJson", serialized.SandboxJson);
            insertVersion.Parameters.AddWithValue("$mcpBindingsJson", serialized.McpBindingsJson);
            insertVersion.Parameters.AddWithValue("$skillAttachmentsJson", serialized.SkillAttachmentsJson);
            insertVersion.Parameters.AddWithValue("$personaJson", serialized.PersonaJson);
            insertVersion.Parameters.AddWithValue("$approvalConfigJson", serialized.ApprovalConfigJson);
            insertVersion.Parameters.AddWithValue("$toolExposureJson", serialized.ToolExposureJson);
            insertVersion.Parameters.AddWithValue("$createdAt", now);
            insertVersion.ExecuteNonQuery();
        }

        foreach (AgentSettingSnapshot setting in BuildSettingSnapshots(definition, snapshotMetadata))
        {
            using SqliteCommand insertSetting = connection.CreateCommand();
            insertSetting.Transaction = transaction;
            insertSetting.CommandText = """
                INSERT INTO agent_definition_settings
                    (version, setting_id, setting_name, value_json)
                VALUES
                    ($version, $settingId, $settingName, $valueJson);
                """;
            insertSetting.Parameters.AddWithValue("$version", nextVersion);
            insertSetting.Parameters.AddWithValue("$settingId", setting.Id);
            insertSetting.Parameters.AddWithValue("$settingName", setting.Name);
            insertSetting.Parameters.AddWithValue("$valueJson", setting.ValueJson);
            insertSetting.ExecuteNonQuery();
        }

        using (SqliteCommand setCurrent = connection.CreateCommand())
        {
            setCurrent.Transaction = transaction;
            setCurrent.CommandText = """
                INSERT INTO agent_definition_current (singleton_id, version, activated_at)
                VALUES (1, $version, $activatedAt)
                ON CONFLICT(singleton_id) DO UPDATE SET
                    version = excluded.version,
                    activated_at = excluded.activated_at;
                """;
            setCurrent.Parameters.AddWithValue("$version", nextVersion);
            setCurrent.Parameters.AddWithValue("$activatedAt", now);
            setCurrent.ExecuteNonQuery();
        }

        transaction.Commit();
        return nextVersion;
    }

    public long SaveDefinitionAndRegistryAtomically(AgentRegistryEntry registryEntry, AgentDefinition definition, AgentDefinitionSnapshotMetadata? snapshotMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(registryEntry);
        ArgumentNullException.ThrowIfNull(definition);

        KautukaDatabase resolvedDatabase = database
            ?? throw new InvalidOperationException("Atomic agent persistence requires a configured kautuka.db instance.");

        string storageLocatorKey = registryEntry.StorageLocatorKey;
        string databasePath = GetDatabasePath(storageLocatorKey);
        bool storeExisted = File.Exists(databasePath);

        try
        {
            // Schema activation is a distinct precondition. The cross-database transaction below owns
            // only the definition snapshot and registry aggregate changes.
            using SqliteConnection agentConnection = OpenConnection(storageLocatorKey);
        }
        catch
        {
            if (!storeExisted)
            {
                _ = DeleteStore(storageLocatorKey);
            }

            throw;
        }

        using SqliteConnection connection = OpenRegistryConnection(resolvedDatabase);
        bool attached = false;
        long nextVersion;

        try
        {
            AttachAgentDatabase(connection, databasePath);
            attached = true;

            using SqliteTransaction transaction = connection.BeginTransaction();
            nextVersion = SaveDefinitionToAttachedSchema(connection, transaction, attachedAgentSchema, definition, snapshotMetadata);
            UpsertAgentRegistry(connection, transaction, registryEntry);
            transaction.Commit();
        }
        catch
        {
            if (!storeExisted)
            {
                _ = DeleteStore(storageLocatorKey);
            }

            throw;
        }
        finally
        {
            if (attached)
            {
                DetachAgentDatabase(connection);
            }
        }

        return nextVersion;
    }

    public bool DeleteStoreAndRegistryAtomically(string agentId, string storageLocatorKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(storageLocatorKey);

        KautukaDatabase resolvedDatabase = database
            ?? throw new InvalidOperationException("Atomic agent persistence requires a configured kautuka.db instance.");

        string directoryPath = GetDatabaseDirectory(storageLocatorKey);
        string databasePath = GetDatabasePath(storageLocatorKey);
        string? pendingDeletePath = null;
        SqliteAgentStoreSchema.Forget(databasePath);
        SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(directoryPath))
            {
                pendingDeletePath = MoveStoreToPendingDelete(directoryPath, storageLocatorKey);
            }

            using SqliteConnection connection = OpenRegistryConnection(resolvedDatabase);
            using SqliteTransaction transaction = connection.BeginTransaction();
            DeleteAgentRegistry(connection, transaction, agentId);
            transaction.Commit();
        }
        catch
        {
            RestorePendingDelete(directoryPath, pendingDeletePath);
            throw;
        }

        if (!string.IsNullOrWhiteSpace(pendingDeletePath))
        {
            try
            {
                Directory.Delete(pendingDeletePath, recursive: true);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        SqliteAgentStoreSchema.Forget(databasePath);
        return true;
    }

    public AgentDefinition? TryLoadLatestDefinition(string storageLocatorKey)
        => TryLoadLatestDefinition(storageLocatorKey, out _);

    public AgentDefinition? TryLoadLatestDefinition(string storageLocatorKey, out long revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageLocatorKey);
        revision = 0;

        string databasePath = GetDatabasePath(storageLocatorKey);
        if (!File.Exists(databasePath))
        {
            return null;
        }

        using var connection = OpenConnection(storageLocatorKey);

        long? currentVersion = GetCurrentVersion(connection);
        if (!currentVersion.HasValue)
        {
            return null;
        }

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT agent_id, scope, enabled, name, model_id, system_prompt, super_exec_mode,
                     session_json, sandbox_json, mcp_bindings_json, skill_attachments_json, persona_json,
                     approval_config_json, tool_exposure_json
            FROM agent_definition_versions
            WHERE version = $version
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$version", currentVersion.Value);

        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        revision = currentVersion.Value;
        return AgentDefinitionSerializer.Deserialize(new AgentDefinitionSerializer.SerializedAgentDefinition(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.GetString(12),
            reader.GetString(13)));
    }

    public IReadOnlyList<AgentDefinitionRevision> ListDefinitionRevisions(string storageLocatorKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageLocatorKey);
        if (!File.Exists(GetDatabasePath(storageLocatorKey)))
        {
            return [];
        }

        using var connection = OpenConnection(storageLocatorKey);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT version, created_at
            FROM agent_definition_versions
            ORDER BY version DESC;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var revisions = new List<AgentDefinitionRevision>();
        while (reader.Read())
        {
            revisions.Add(new AgentDefinitionRevision(reader.GetInt64(0), reader.GetString(1)));
        }

        return revisions;
    }

    public IReadOnlyList<AgentDefinitionSettingSnapshot> ListDefinitionSettings(string storageLocatorKey, long revision)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageLocatorKey);
        if (!File.Exists(GetDatabasePath(storageLocatorKey)))
        {
            return [];
        }

        using var connection = OpenConnection(storageLocatorKey);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT version, setting_id, setting_name, value_json
            FROM agent_definition_settings
            WHERE version = $version
            ORDER BY setting_id;
            """;
        command.Parameters.AddWithValue("$version", revision);
        return ReadSettingSnapshots(command);
    }

    public IReadOnlyList<AgentDefinitionSettingSnapshot> ListAllDefinitionSettings(string storageLocatorKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageLocatorKey);
        if (!File.Exists(GetDatabasePath(storageLocatorKey)))
        {
            return [];
        }

        using var connection = OpenConnection(storageLocatorKey);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT version, setting_id, setting_name, value_json
            FROM agent_definition_settings
            ORDER BY version, setting_id;
            """;
        return ReadSettingSnapshots(command);
    }

    private static List<AgentDefinitionSettingSnapshot> ReadSettingSnapshots(SqliteCommand command)
    {
        using SqliteDataReader reader = command.ExecuteReader();
        var settings = new List<AgentDefinitionSettingSnapshot>();
        while (reader.Read())
        {
            settings.Add(new AgentDefinitionSettingSnapshot(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3)));
        }

        return settings;
    }








    public bool DeleteStore(string storageLocatorKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageLocatorKey);

        string directoryPath = GetDatabaseDirectory(storageLocatorKey);
        if (!Directory.Exists(directoryPath))
        {
            SqliteAgentStoreSchema.Forget(GetDatabasePath(storageLocatorKey));
            return true;
        }

        try
        {
            Directory.Delete(directoryPath, recursive: true);
            SqliteAgentStoreSchema.Forget(GetDatabasePath(storageLocatorKey));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private SqliteConnection OpenConnection(string storageLocatorKey)
    {
        string directoryPath = GetDatabaseDirectory(storageLocatorKey);
        Directory.CreateDirectory(directoryPath);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = GetDatabasePath(storageLocatorKey),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        };

#pragma warning disable CA2000
        var connection = new SqliteConnection(builder.ToString());
#pragma warning restore CA2000
        try
        {
            connection.Open();

            ApplyConnectionPragmas(connection);
            SqliteAgentStoreSchema.EnsureCurrent(connection);
            BootstrapSqliteVec(connection);

            return connection;
        }
        catch
        {
            SqliteAgentStoreSchema.Forget(connection.DataSource);
            connection.Dispose();
            throw;
        }
    }


    public void UpsertMemoryEmbeddings(string storageLocatorKey, IReadOnlyList<AgentMemoryEmbeddingRecord> embeddings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageLocatorKey);
        if (embeddings.Count == 0)
        {
            return;
        }

        using var connection = OpenConnection(storageLocatorKey);
        using SqliteTransaction transaction = connection.BeginTransaction();

        foreach (AgentMemoryEmbeddingRecord embedding in embeddings)
        {
            ExistingEmbeddingEntry? existing = TryGetExistingEmbeddingEntry(connection, transaction, embedding.FactId);
            long embeddingId;
            if (existing is null)
            {
                embeddingId = InsertEmbeddingMetadata(connection, transaction, embedding);
            }
            else
            {
                DeleteEmbeddingVectorRow(connection, transaction, existing.EmbeddingId, existing.Dimensions);
                UpdateEmbeddingMetadata(connection, transaction, existing.EmbeddingId, embedding);
                embeddingId = existing.EmbeddingId;
            }

            EnsureEmbeddingVectorTable(connection, transaction, embedding.Dimensions);
            InsertEmbeddingVectorRow(connection, transaction, embeddingId, embedding.ModelKey, embedding.Dimensions, embedding.Vector);
        }

        transaction.Commit();
    }

    public void DeleteMemoryEmbedding(string storageLocatorKey, string factId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageLocatorKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(factId);

        string databasePath = GetDatabasePath(storageLocatorKey);
        if (!File.Exists(databasePath))
        {
            return;
        }

        using var connection = OpenConnection(storageLocatorKey);
        using SqliteTransaction transaction = connection.BeginTransaction();
        ExistingEmbeddingEntry? existing = TryGetExistingEmbeddingEntry(connection, transaction, factId);
        if (existing is not null)
        {
            DeleteEmbeddingVectorRow(connection, transaction, existing.EmbeddingId, existing.Dimensions);

            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM runtime_memory_embeddings WHERE fact_id = $factId;";
            command.Parameters.AddWithValue("$factId", factId);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IReadOnlyDictionary<string, AgentMemoryEmbeddingState> ListMemoryEmbeddingStates(string storageLocatorKey)
    {
        using SqliteConnection connection = OpenConnection(storageLocatorKey);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT fact_id,model_key,updated_at FROM {runtimeMemoryEmbeddingsTableName} ORDER BY fact_id";
        using SqliteDataReader reader = command.ExecuteReader();
        var result = new Dictionary<string, AgentMemoryEmbeddingState>(StringComparer.Ordinal);
        while (reader.Read())
        {
            result[reader.GetString(0)] = new AgentMemoryEmbeddingState(reader.GetString(1), reader.GetString(2));
        }
        return result;
    }

    public IReadOnlyList<AgentMemorySearchHit> SearchMemoryEmbeddings(string storageLocatorKey, string modelKey, ReadOnlySpan<float> queryVector, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageLocatorKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelKey);
        if (queryVector.Length == 0 || limit <= 0)
        {
            return [];
        }

        string databasePath = GetDatabasePath(storageLocatorKey);
        if (!File.Exists(databasePath))
        {
            return [];
        }

        using var connection = OpenConnection(storageLocatorKey);

        int dimensions = queryVector.Length;
        string vectorTableName = GetEmbeddingVectorTableName(dimensions);
        if (!TableExists(connection, vectorTableName))
        {
            return [];
        }

        string queryVectorJson = JsonSerializer.Serialize(queryVector.ToArray(), KautukaJsonContext.Default.SingleArray);
        using SqliteCommand command = connection.CreateCommand();
#pragma warning disable CA2100
        command.CommandText = $"""
            SELECT metadata.fact_id, 1.0 - vectors.distance AS score, metadata.updated_at
            FROM {vectorTableName} AS vectors
            INNER JOIN runtime_memory_embeddings AS metadata
                ON metadata.embedding_id = vectors.rowid
            WHERE vectors.model_key = $modelKey
              AND vectors.embedding MATCH $queryVector
            ORDER BY vectors.distance ASC, metadata.updated_at DESC
            LIMIT $limit;
            """;
#pragma warning restore CA2100
        command.Parameters.AddWithValue("$modelKey", modelKey);
        command.Parameters.AddWithValue("$queryVector", queryVectorJson);
        command.Parameters.AddWithValue("$limit", limit);

        var hits = new List<AgentMemorySearchHit>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            double score = reader.GetDouble(1);
            if (double.IsNaN(score) || double.IsInfinity(score))
            {
                continue;
            }

            hits.Add(new AgentMemorySearchHit(reader.GetString(0), score, reader.GetString(2)));
        }

        return hits;
    }


    private static ExistingEmbeddingEntry? TryGetExistingEmbeddingEntry(SqliteConnection connection, SqliteTransaction transaction, string factId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT embedding_id, dimensions
            FROM {runtimeMemoryEmbeddingsTableName}
            WHERE fact_id = $factId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$factId", factId);

        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new ExistingEmbeddingEntry(reader.GetInt64(0), reader.GetInt32(1));
    }

    private static long InsertEmbeddingMetadata(SqliteConnection connection, SqliteTransaction transaction, AgentMemoryEmbeddingRecord embedding)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT INTO {runtimeMemoryEmbeddingsTableName}
                (fact_id, target_type, target_id, model_key, dimensions, source_text, created_at, updated_at)
            VALUES
                ($factId, $targetType, $targetId, $modelKey, $dimensions, $sourceText, $createdAt, $updatedAt)
            RETURNING embedding_id;
            """;
        command.Parameters.AddWithValue("$factId", embedding.FactId);
        command.Parameters.AddWithValue("$targetType", embedding.TargetType);
        command.Parameters.AddWithValue("$targetId", embedding.TargetId);
        command.Parameters.AddWithValue("$modelKey", embedding.ModelKey);
        command.Parameters.AddWithValue("$dimensions", embedding.Dimensions);
        command.Parameters.AddWithValue("$sourceText", embedding.SourceText);
        command.Parameters.AddWithValue("$createdAt", embedding.CreatedAt);
        command.Parameters.AddWithValue("$updatedAt", embedding.UpdatedAt);

        object? scalar = command.ExecuteScalar();
        return Convert.ToInt64(scalar, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void UpdateEmbeddingMetadata(SqliteConnection connection, SqliteTransaction transaction, long embeddingId, AgentMemoryEmbeddingRecord embedding)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE {runtimeMemoryEmbeddingsTableName}
            SET target_type = $targetType,
                target_id = $targetId,
                model_key = $modelKey,
                dimensions = $dimensions,
                source_text = $sourceText,
                created_at = $createdAt,
                updated_at = $updatedAt
            WHERE embedding_id = $embeddingId;
            """;
        command.Parameters.AddWithValue("$embeddingId", embeddingId);
        command.Parameters.AddWithValue("$targetType", embedding.TargetType);
        command.Parameters.AddWithValue("$targetId", embedding.TargetId);
        command.Parameters.AddWithValue("$modelKey", embedding.ModelKey);
        command.Parameters.AddWithValue("$dimensions", embedding.Dimensions);
        command.Parameters.AddWithValue("$sourceText", embedding.SourceText);
        command.Parameters.AddWithValue("$createdAt", embedding.CreatedAt);
        command.Parameters.AddWithValue("$updatedAt", embedding.UpdatedAt);
        command.ExecuteNonQuery();
    }

    private static void EnsureEmbeddingVectorTable(SqliteConnection connection, SqliteTransaction transaction, int dimensions)
    {
        string vectorTableName = GetEmbeddingVectorTableName(dimensions);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
#pragma warning disable CA2100
        command.CommandText = $"""
            CREATE VIRTUAL TABLE IF NOT EXISTS {vectorTableName} USING vec0(
                model_key TEXT PARTITION KEY,
                embedding FLOAT[{dimensions}]
            );
            """;
#pragma warning restore CA2100
        command.ExecuteNonQuery();
    }

    private static void InsertEmbeddingVectorRow(SqliteConnection connection, SqliteTransaction transaction, long embeddingId, string modelKey, int dimensions, float[] vector)
    {
        string vectorJson = JsonSerializer.Serialize(vector, KautukaJsonContext.Default.SingleArray);
        InsertEmbeddingVectorRow(connection, transaction, embeddingId, modelKey, dimensions, vectorJson);
    }

    private static void InsertEmbeddingVectorRow(SqliteConnection connection, SqliteTransaction transaction, long embeddingId, string modelKey, int dimensions, string vectorJson)
    {
        string vectorTableName = GetEmbeddingVectorTableName(dimensions);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
#pragma warning disable CA2100
        command.CommandText = $"""
            INSERT INTO {vectorTableName}(rowid, model_key, embedding)
            VALUES ($rowId, $modelKey, vec_f32($vectorJson));
            """;
#pragma warning restore CA2100
        command.Parameters.AddWithValue("$rowId", embeddingId);
        command.Parameters.AddWithValue("$modelKey", modelKey);
        command.Parameters.AddWithValue("$vectorJson", vectorJson);
        command.ExecuteNonQuery();
    }

    private static void DeleteEmbeddingVectorRow(SqliteConnection connection, SqliteTransaction transaction, long embeddingId, int dimensions)
    {
        string vectorTableName = GetEmbeddingVectorTableName(dimensions);
        if (!TableExists(connection, vectorTableName))
        {
            return;
        }

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
#pragma warning disable CA2100
        command.CommandText = $"DELETE FROM {vectorTableName} WHERE rowid = $rowId;";
#pragma warning restore CA2100
        command.Parameters.AddWithValue("$rowId", embeddingId);
        command.ExecuteNonQuery();
    }


    private static string GetEmbeddingVectorTableName(int dimensions)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimensions);
        return string.Create(
            runtimeMemoryEmbeddingVectorTablePrefix.Length + dimensions.ToString(System.Globalization.CultureInfo.InvariantCulture).Length,
            dimensions,
            static (span, value) =>
            {
                runtimeMemoryEmbeddingVectorTablePrefix.AsSpan().CopyTo(span);
                _ = value.TryFormat(span[runtimeMemoryEmbeddingVectorTablePrefix.Length..], out _, provider: System.Globalization.CultureInfo.InvariantCulture);
            });
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type IN ('table', 'virtual table') AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", tableName);
        return command.ExecuteScalar() is not null;
    }


    private void BootstrapSqliteVec(SqliteConnection connection)
    {
        (string extensionPath, string version) = ValidateSqliteVec(connection);
        using SqliteCommand metadata = connection.CreateCommand();
        metadata.CommandText = """
            INSERT INTO runtime_memory_vector_metadata (singleton_id, sqlite_vec_path, vec_version, validated_at)
            VALUES (1, $path, $version, $validatedAt)
            ON CONFLICT(singleton_id) DO UPDATE SET
                sqlite_vec_path = excluded.sqlite_vec_path,
                vec_version = excluded.vec_version,
                validated_at = excluded.validated_at;
            """;
        metadata.Parameters.AddWithValue("$path", extensionPath);
        metadata.Parameters.AddWithValue("$version", version);
        metadata.Parameters.AddWithValue("$validatedAt", DateTimeOffset.UtcNow.ToString("O"));
        metadata.ExecuteNonQuery();
    }

    private (string ExtensionPath, string Version) ValidateSqliteVec(SqliteConnection connection)
    {
        string extensionPath = ResolveSqliteVecExtensionPath();
        if (!File.Exists(extensionPath))
        {
            throw new InvalidOperationException($"sqlite-vec extension was not found at '{extensionPath}'. Configure Kautuka:SqliteVec:ExtensionPath with a valid shared library path.");
        }

        connection.EnableExtensions(true);
        connection.LoadExtension(extensionPath);
        using SqliteCommand smoke = connection.CreateCommand();
        smoke.CommandText = "SELECT vec_version();";
        string? version = smoke.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidOperationException($"sqlite-vec loaded from '{extensionPath}' but vec_version() did not return a valid result.");
        }

        return (extensionPath, version);
    }

    private static long GetNextVersion(SqliteConnection connection, SqliteTransaction transaction)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(version), 0) + 1 FROM agent_definition_versions;";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void ApplyConnectionPragmas(SqliteConnection connection)
    {
        using SqliteCommand pragma = connection.CreateCommand();
        pragma.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA foreign_keys=ON;
            PRAGMA busy_timeout=5000;
            """;
        pragma.ExecuteNonQuery();
    }

    private static SqliteConnection OpenRegistryConnection(KautukaDatabase database)
    {
        var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        ApplyConnectionPragmas(connection);
        return connection;
    }

    private static void UpsertAgentRegistry(SqliteConnection connection, SqliteTransaction transaction, AgentRegistryEntry registryEntry)
    {
        string now = DateTimeOffset.UtcNow.ToString("O");

        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO agents
                (id, scope, enabled, owner_user_id, storage_locator_key, created_at, updated_at)
            VALUES
                ($id, $scope, $enabled, $ownerUserId, $storageLocatorKey,
                 COALESCE((SELECT created_at FROM agents WHERE id = $id), $now),
                 $now)
            ON CONFLICT(id) DO UPDATE SET
                scope = excluded.scope,
                enabled = excluded.enabled,
                owner_user_id = excluded.owner_user_id,
                storage_locator_key = excluded.storage_locator_key,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", registryEntry.Id);
        command.Parameters.AddWithValue("$scope", registryEntry.Scope);
        command.Parameters.AddWithValue("$enabled", registryEntry.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("$ownerUserId", registryEntry.OwnerUserId);
        command.Parameters.AddWithValue("$storageLocatorKey", registryEntry.StorageLocatorKey);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
    }

    private static void DeleteAgentRegistry(SqliteConnection connection, SqliteTransaction transaction, string agentId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM agents WHERE id = $id;";
        command.Parameters.AddWithValue("$id", agentId);
        command.ExecuteNonQuery();
    }

    private void AttachAgentDatabase(SqliteConnection connection, string databasePath)
    {
        string? directoryPath = Path.GetDirectoryName(databasePath);
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new InvalidOperationException("Agent database path must have a parent directory.");
        }

        Directory.CreateDirectory(directoryPath);
        _ = ValidateSqliteVec(connection);

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"ATTACH DATABASE $path AS {attachedAgentSchema};";
        command.Parameters.AddWithValue("$path", databasePath);
        command.ExecuteNonQuery();
    }

    private static void DetachAgentDatabase(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"DETACH DATABASE {attachedAgentSchema};";
        command.ExecuteNonQuery();
    }


    private static long SaveDefinitionToAttachedSchema(SqliteConnection connection, SqliteTransaction transaction, string schemaName, AgentDefinition definition, AgentDefinitionSnapshotMetadata? snapshotMetadata)
    {
        string now = DateTimeOffset.UtcNow.ToString("O");
        AgentDefinitionSerializer.SerializedAgentDefinition serialized = AgentDefinitionSerializer.Serialize(definition);
        long nextVersion = GetNextAttachedVersion(connection, transaction, schemaName);

        using (SqliteCommand insertVersion = connection.CreateCommand())
        {
            insertVersion.Transaction = transaction;
#pragma warning disable CA2100
            insertVersion.CommandText = $"""
                INSERT INTO {schemaName}.agent_definition_versions
                    (version, agent_id, scope, enabled, name, model_id, system_prompt, super_exec_mode,
                     session_json, sandbox_json, mcp_bindings_json, skill_attachments_json, persona_json,
                     approval_config_json, tool_exposure_json, created_at)
                VALUES
                    ($version, $agentId, $scope, $enabled, $name, $modelId, $systemPrompt, $superExecMode,
                     $sessionJson, $sandboxJson, $mcpBindingsJson, $skillAttachmentsJson, $personaJson,
                     $approvalConfigJson, $toolExposureJson, $createdAt);
                """;
#pragma warning restore CA2100
            insertVersion.Parameters.AddWithValue("$version", nextVersion);
            insertVersion.Parameters.AddWithValue("$agentId", serialized.Id);
            insertVersion.Parameters.AddWithValue("$scope", serialized.Scope);
            insertVersion.Parameters.AddWithValue("$enabled", serialized.Enabled);
            insertVersion.Parameters.AddWithValue("$name", serialized.Name);
            insertVersion.Parameters.AddWithValue("$modelId", serialized.ModelId);
            insertVersion.Parameters.AddWithValue("$systemPrompt", serialized.SystemPrompt);
            insertVersion.Parameters.AddWithValue("$superExecMode", serialized.SuperExecMode);
            insertVersion.Parameters.AddWithValue("$sessionJson", serialized.SessionJson);
            insertVersion.Parameters.AddWithValue("$sandboxJson", serialized.SandboxJson);
            insertVersion.Parameters.AddWithValue("$mcpBindingsJson", serialized.McpBindingsJson);
            insertVersion.Parameters.AddWithValue("$skillAttachmentsJson", serialized.SkillAttachmentsJson);
            insertVersion.Parameters.AddWithValue("$personaJson", serialized.PersonaJson);
            insertVersion.Parameters.AddWithValue("$approvalConfigJson", serialized.ApprovalConfigJson);
            insertVersion.Parameters.AddWithValue("$toolExposureJson", serialized.ToolExposureJson);
            insertVersion.Parameters.AddWithValue("$createdAt", now);
            insertVersion.ExecuteNonQuery();
        }

        foreach (AgentSettingSnapshot setting in BuildSettingSnapshots(definition, snapshotMetadata))
        {
            using SqliteCommand insertSetting = connection.CreateCommand();
            insertSetting.Transaction = transaction;
#pragma warning disable CA2100
            insertSetting.CommandText = $"""
                INSERT INTO {schemaName}.agent_definition_settings
                    (version, setting_id, setting_name, value_json)
                VALUES
                    ($version, $settingId, $settingName, $valueJson);
                """;
#pragma warning restore CA2100
            insertSetting.Parameters.AddWithValue("$version", nextVersion);
            insertSetting.Parameters.AddWithValue("$settingId", setting.Id);
            insertSetting.Parameters.AddWithValue("$settingName", setting.Name);
            insertSetting.Parameters.AddWithValue("$valueJson", setting.ValueJson);
            insertSetting.ExecuteNonQuery();
        }

        using SqliteCommand setCurrent = connection.CreateCommand();
        setCurrent.Transaction = transaction;
#pragma warning disable CA2100
        setCurrent.CommandText = $"""
            INSERT INTO {schemaName}.agent_definition_current (singleton_id, version, activated_at)
            VALUES (1, $version, $activatedAt)
            ON CONFLICT(singleton_id) DO UPDATE SET
                version = excluded.version,
                activated_at = excluded.activated_at;
            """;
#pragma warning restore CA2100
        setCurrent.Parameters.AddWithValue("$version", nextVersion);
        setCurrent.Parameters.AddWithValue("$activatedAt", now);
        setCurrent.ExecuteNonQuery();
        return nextVersion;
    }

    private static long GetNextAttachedVersion(SqliteConnection connection, SqliteTransaction transaction, string schemaName)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
#pragma warning disable CA2100
        command.CommandText = $"SELECT COALESCE(MAX(version), 0) + 1 FROM {schemaName}.agent_definition_versions;";
#pragma warning restore CA2100
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private string MoveStoreToPendingDelete(string directoryPath, string storageLocatorKey)
    {
        string pendingRoot = Path.Combine(KautukaHostPathPolicy.ResolveSharedStateRoot(configuration), "agents", ".deleting");
        Directory.CreateDirectory(pendingRoot);
        string pendingPath = Path.Combine(pendingRoot, $"{SanitizePathSegment(storageLocatorKey)}-{Guid.NewGuid():N}");
        const int maxAttempts = 12;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(directoryPath, pendingPath);
                return pendingPath;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                SqliteAgentStoreSchema.Forget(GetDatabasePath(storageLocatorKey));
                SqliteConnection.ClearAllPools();
                Thread.Sleep(TimeSpan.FromMilliseconds(40 * attempt));
            }
        }
    }

    private static void RestorePendingDelete(string directoryPath, string? pendingDeletePath)
    {
        if (string.IsNullOrWhiteSpace(pendingDeletePath)
            || !Directory.Exists(pendingDeletePath)
            || Directory.Exists(directoryPath))
        {
            return;
        }

        Directory.Move(pendingDeletePath, directoryPath);
    }

    private static long? GetCurrentVersion(SqliteConnection connection)
    {
        using SqliteCommand currentCommand = connection.CreateCommand();
        currentCommand.CommandText = "SELECT version FROM agent_definition_current WHERE singleton_id = 1 LIMIT 1;";
        object? scalar = currentCommand.ExecuteScalar();
        if (scalar is long longValue)
        {
            return longValue;
        }

        if (scalar is int intValue)
        {
            return intValue;
        }

        using SqliteCommand fallbackCommand = connection.CreateCommand();
        fallbackCommand.CommandText = "SELECT MAX(version) FROM agent_definition_versions;";
        object? fallbackScalar = fallbackCommand.ExecuteScalar();

        return fallbackScalar switch
        {
            long fallbackLong => fallbackLong,
            int fallbackInt => fallbackInt,
            _ => null,
        };
    }

    private static IReadOnlyList<AgentSettingSnapshot> BuildSettingSnapshots(AgentDefinition definition, AgentDefinitionSnapshotMetadata? snapshotMetadata)
    {
        AgentDefinitionSerializer.SerializedAgentDefinition serialized = AgentDefinitionSerializer.Serialize(definition);

        return
        [
            Snapshot("scope", "Scope", SerializeStringScalar(definition.Scope)),
            Snapshot("enabled", "Enabled", SerializeBooleanScalar(definition.Enabled)),
            Snapshot("name", "Name", SerializeStringScalar(definition.Name)),
            Snapshot("model_id", "Model", SerializeModelReference(definition.ModelId, snapshotMetadata)),
            Snapshot("system_prompt", "System prompt", SerializeStringScalar(definition.SystemPrompt)),
            Snapshot("super_exec_mode", "SuperExec mode", SerializeStringScalar(definition.SuperExecMode)),
            Snapshot("session", "Session settings", serialized.SessionJson),
            Snapshot("sandbox", "Sandbox settings", serialized.SandboxJson),
            Snapshot("mcp_bindings", "MCP bindings", SerializeMcpBindings(definition.McpBindings, snapshotMetadata)),
            Snapshot("skill_attachments", "Skill attachments", SerializeSkillAttachments(definition.SkillAttachments, snapshotMetadata)),
            Snapshot("persona", "Persona", serialized.PersonaJson),
            Snapshot("approval_policy", "Approval policy", serialized.ApprovalConfigJson),
            Snapshot("tool_exposure", "Tool exposure", serialized.ToolExposureJson),
            Snapshot("runtime", "Runtime", SerializeRuntime(definition.Runtime)),
        ];
    }

    private static AgentSettingSnapshot Snapshot(string id, string name, string valueJson) => new(id, name, valueJson);

    private static string SerializeRuntime(AgentRuntimeDefinition? runtime)
    {
        AgentRuntimeDefinition value = runtime ?? new AgentRuntimeDefinition();
        var payload = new JsonObject();
        if (value.MaxRunDurationSeconds is int maxRun)
        {
            payload["maxRunDurationSeconds"] = maxRun;
        }

        if (value.HitlExpirySeconds is int hitlExpiry)
        {
            payload["hitlExpirySeconds"] = hitlExpiry;
        }

        if (!string.IsNullOrWhiteSpace(value.HitlExpiryAction))
        {
            payload["hitlExpiryAction"] = value.HitlExpiryAction;
        }

        if (value.AllowMultipleToolCalls is bool allowMultiple)
        {
            payload["allowMultipleToolCalls"] = allowMultiple;
        }

        if (value.AllowConcurrentInvocation is bool allowConcurrent)
        {
            payload["allowConcurrentInvocation"] = allowConcurrent;
        }

        if (value.MaxParallelTools is int maxParallel)
        {
            payload["maxParallelTools"] = RuntimeSettingKeys.ClampMaxParallelTools(maxParallel);
        }

        return payload.ToJsonString();
    }

    private static string SerializeBooleanScalar(bool value) => value ? "true" : "false";

    private static string SerializeStringScalar(string value)
    {
        string encoded = JsonEncodedText.Encode(value).ToString();
        return string.Create(encoded.Length + 2, encoded, static (span, state) =>
        {
            span[0] = '"';
            state.AsSpan().CopyTo(span[1..]);
            span[^1] = '"';
        });
    }

    private static string SerializeModelReference(string modelId, AgentDefinitionSnapshotMetadata? snapshotMetadata)
    {
        if (string.IsNullOrWhiteSpace(snapshotMetadata?.ModelName)
            && string.IsNullOrWhiteSpace(snapshotMetadata?.ModelProviderId))
        {
            return SerializeStringScalar(modelId);
        }

        var payload = new JsonObject
        {
            ["id"] = modelId,
            ["name"] = snapshotMetadata.ModelName,
            ["providerId"] = snapshotMetadata.ModelProviderId,
        };

        return payload.ToJsonString();
    }

    private static string SerializeMcpBindings(IReadOnlyList<AgentMcpBindingDefinition> bindings, AgentDefinitionSnapshotMetadata? snapshotMetadata)
    {
        var payload = new JsonArray();
        foreach (AgentMcpBindingDefinition binding in bindings)
        {
            string? connectorName = null;
            _ = snapshotMetadata?.ConnectorNames?.TryGetValue(binding.ConnectorId, out connectorName);
            payload.Add((JsonNode)new JsonObject
            {
                ["connectorId"] = binding.ConnectorId,
                ["connectorName"] = connectorName,
                ["context"] = binding.Context,
            });
        }

        return payload.ToJsonString();
    }

    private static string SerializeSkillAttachments(IReadOnlyList<AgentSkillAttachmentDefinition> attachments, AgentDefinitionSnapshotMetadata? snapshotMetadata)
    {
        var payload = new JsonArray();
        foreach (AgentSkillAttachmentDefinition attachment in attachments)
        {
            string? skillName = null;
            _ = snapshotMetadata?.SkillNames?.TryGetValue(attachment.SkillId, out skillName);
            payload.Add((JsonNode)new JsonObject
            {
                ["skillId"] = attachment.SkillId,
                ["skillName"] = skillName,
                ["mode"] = attachment.Mode.ToString(),
            });
        }

        return payload.ToJsonString();
    }

    private string GetDatabaseDirectory(string storageLocatorKey)
        => AgentStoragePath.RequireAgentDirectory(
            KautukaHostPathPolicy.ResolveSharedStateRoot(configuration),
            storageLocatorKey);

    private string GetDatabasePath(string storageLocatorKey)
        => Path.Combine(GetDatabaseDirectory(storageLocatorKey), agentDatabaseFileName);

    private List<string> ListRegisteredStorageLocatorKeys()
    {
        KautukaDatabase resolvedDatabase = database
            ?? throw new InvalidOperationException("Runtime root resolution requires the authoritative agent registry.");

        using SqliteConnection connection = OpenRegistryConnection(resolvedDatabase);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT id
            FROM agents
            ORDER BY 1;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var locators = new List<string>();
        while (reader.Read())
        {
            locators.Add(reader.GetString(0));
        }

        return locators;
    }

    private static string SanitizePathSegment(string value)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        Span<char> buffer = stackalloc char[value.Length];
        for (int index = 0; index < value.Length; index++)
        {
            buffer[index] = invalidChars.Contains(value[index]) ? '_' : value[index];
        }

        return new string(buffer);
    }

    private string ResolveSqliteVecExtensionPath()
    {
        string? configured = configuration["Kautuka:SqliteVec:ExtensionPath"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        foreach (string candidatePath in GetDefaultSqliteVecPaths())
        {
            if (File.Exists(candidatePath))
            {
                return candidatePath;
            }
        }

        throw new InvalidOperationException($"sqlite-vec is required for agent stores. Set Kautuka:SqliteVec:ExtensionPath or package one of the expected binaries alongside the Gateway app: {string.Join(", ", GetDefaultSqliteVecPaths())}.");
    }

    private static IReadOnlyList<string> GetDefaultSqliteVecPaths()
    {
        if (OperatingSystem.IsWindows())
        {
            return
            [
                Path.Combine(AppContext.BaseDirectory, "sqlite-vec", "win-x64", "vec0.dll"),
                Path.Combine(AppContext.BaseDirectory, "sqlite-vec", "linux-x64", "vec0.so")
            ];
        }

        return
        [
            Path.Combine(AppContext.BaseDirectory, "sqlite-vec", "linux-x64", "vec0.so"),
            Path.Combine(AppContext.BaseDirectory, "sqlite-vec", "win-x64", "vec0.dll")
        ];
    }

    private static void ExecuteNonQuery(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
#pragma warning disable CA2100
        command.CommandText = sql;
#pragma warning restore CA2100
        command.ExecuteNonQuery();
    }

    private sealed record ExistingEmbeddingEntry(long EmbeddingId, int Dimensions);
    private sealed record AgentSettingSnapshot(string Id, string Name, string ValueJson);
}
