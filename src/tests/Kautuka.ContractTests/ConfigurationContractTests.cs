using Kautuka.Gateway.Infrastructure.Configuration;
using Kautuka.Gateway.Infrastructure.Database;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Kautuka.ContractTests;

public sealed class ConfigurationContractTests
{
    [Fact]
    public void ProviderLoadsConfiguredAgentsFromSqlite()
    {
        using var temp = new TempDirectory();
        string dbPath = Path.Combine(temp.Path, "kautuka.db");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kautuka:ContainerStateRoot"] = temp.Path,
            })
            .Build();

        var database = new KautukaDatabase(configuration);
        database.EnsureReady();
        var agentDefinitionStore = new SqliteAgentDefinitionStore(configuration);
        SeedTopology(database.ConnectionString, agentDefinitionStore);

        var provider = new SqlitePlatformConfigurationProvider(database, agentDefinitionStore);
        var snapshot = provider.GetCurrent();

        Assert.Contains(snapshot.Agents, agent => agent.Id == "fasterBrain");
        Assert.Contains(snapshot.Agents, agent => agent.Id == "simpleBrain");
        Assert.Contains(snapshot.Providers, providerDefinition => providerDefinition.Id == "local");
        Assert.Contains(snapshot.Models, model => model.Id == "phi");
    }

    private static void SeedTopology(string connectionString, SqliteAgentDefinitionStore agentDefinitionStore)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using var tx = connection.BeginTransaction();

        ExecuteProvidersInsert(connection, tx);
        ExecuteModelsInsert(connection, tx);
        ExecuteFasterBrainInsert(connection, tx);
        ExecuteSimpleBrainInsert(connection, tx);

        tx.Commit();

        agentDefinitionStore.SaveDefinition("fasterBrain", new AgentDefinition
        {
            Id = "fasterBrain",
            Scope = "user",
            Enabled = true,
            Name = "Faster Brain",
            ModelId = "phi",
            SystemPrompt = "Be fast",
            SuperExecMode = "trusted",
        });

        agentDefinitionStore.SaveDefinition("simpleBrain", new AgentDefinition
        {
            Id = "simpleBrain",
            Scope = "user",
            Enabled = true,
            Name = "Simple Brain",
            ModelId = "phi",
            SystemPrompt = "Be simple",
            SuperExecMode = "trusted",
        });
    }

    private static void ExecuteProvidersInsert(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "INSERT INTO providers (id, name, type, settings_json, created_at, updated_at) VALUES ('local', 'Local Provider', 'openai', '{}', $now, $now)";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        _ = cmd.ExecuteNonQuery();
    }

    private static void ExecuteModelsInsert(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "INSERT INTO models (id, provider_id, model_name, settings_json, created_at, updated_at) VALUES ('phi', 'local', 'phi', '{}', $now, $now)";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        _ = cmd.ExecuteNonQuery();
    }

    private static void ExecuteFasterBrainInsert(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "INSERT INTO agents (id, scope, enabled, owner_user_id, storage_locator_key, created_at, updated_at) VALUES ('fasterBrain', 'user', 1, '', 'fasterBrain', $now, $now)";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        _ = cmd.ExecuteNonQuery();
    }

    private static void ExecuteSimpleBrainInsert(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "INSERT INTO agents (id, scope, enabled, owner_user_id, storage_locator_key, created_at, updated_at) VALUES ('simpleBrain', 'user', 1, '', 'simpleBrain', $now, $now)";
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        _ = cmd.ExecuteNonQuery();
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kautuka-contract-" + Guid.NewGuid().ToString("N"));

        public TempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
