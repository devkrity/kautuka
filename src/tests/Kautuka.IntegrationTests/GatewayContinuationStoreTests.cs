using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Stores;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Kautuka.IntegrationTests;

public sealed class GatewayContinuationStoreTests
{
    [Fact]
    public void StorePersistsCheckpointsInAgentDatabase()
    {
        using TemporaryDatabase temp = TemporaryDatabase.Create();

        temp.EnsureReady();
        temp.InsertAgent("agent-alpha", "agent-alpha");

        var database = new KautukaDatabase(temp.Configuration);
        var checkpointRepository = new SqliteAgentContinuationCheckpointRepository(new SqliteAgentStoreDatabase(temp.Configuration));
        var store = new GatewayContinuationStore(database, checkpointRepository);

        var checkpoint = new GatewayContinuationCheckpoint(
            "checkpoint-1",
            "continuation-1",
            1,
            "session-1",
            "agent-alpha",
            "user-1",
            "run-1",
            ContinuationCheckpointBoundaryTypes.ToolResult,
            "{}",
            "{}",
            "[]",
            "resume-hash-1",
            "2026-05-12T00:00:00.0000000+00:00");

        store.CreateCheckpoint(checkpoint);

        GatewayContinuationCheckpoint? byId = store.GetById("checkpoint-1");
        GatewayContinuationCheckpoint? byHash = store.GetByResumeTokenHash("resume-hash-1");
        int nextCheckpointIndex = store.GetNextCheckpointIndex("continuation-1");

        Assert.NotNull(byId);
        Assert.NotNull(byHash);
        Assert.Equal("agent-alpha", byId.AgentId);
        Assert.Equal(2, nextCheckpointIndex);
        Assert.False(temp.SharedTableExists("continuation_checkpoints"));
        Assert.True(temp.AnyAgentDatabaseContainsTable("continuation_checkpoints"));

        store.DeleteBySessions(["session-1"]);

        Assert.Null(store.GetById("checkpoint-1"));
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string directoryPath;

        private TemporaryDatabase(string directoryPath, IConfiguration configuration)
        {
            this.directoryPath = directoryPath;
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        public string ConnectionString => new KautukaDatabase(Configuration).ConnectionString;

        public static TemporaryDatabase Create()
        {
            string directoryPath = Path.Combine(Path.GetTempPath(), "kautuka-continuation-store-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Kautuka:ContainerStateRoot"] = directoryPath,
                })
                .Build();

            return new TemporaryDatabase(directoryPath, configuration);
        }

        public void EnsureReady()
        {
            var database = new KautukaDatabase(Configuration);
            database.EnsureReady();
        }

        public void InsertAgent(string agentId, string storageLocatorKey)
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO agents (id, scope, enabled, owner_user_id, storage_locator_key, created_at, updated_at)
                VALUES ($id, 'user', 1, 'user-1', $storageLocatorKey, $createdAt, $updatedAt);
                """;
            command.Parameters.AddWithValue("$id", agentId);
            command.Parameters.AddWithValue("$storageLocatorKey", storageLocatorKey);
            command.Parameters.AddWithValue("$createdAt", "2026-05-12T00:00:00.0000000+00:00");
            command.Parameters.AddWithValue("$updatedAt", "2026-05-12T00:00:00.0000000+00:00");
            command.ExecuteNonQuery();
        }

        public bool SharedTableExists(string tableName)
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            return TableExists(connection, tableName);
        }

        public bool AnyAgentDatabaseContainsTable(string tableName)
        {
            foreach (string agentDatabasePath in Directory.GetFiles(directoryPath, "agent.db", SearchOption.AllDirectories))
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = agentDatabasePath }.ToString());
                connection.Open();
                if (TableExists(connection, tableName))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TableExists(SqliteConnection connection, string tableName)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = $name";
            command.Parameters.AddWithValue("$name", tableName);
            return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > 0;
        }

        public void Dispose()
        {
            SqliteTestPoolCleanup.ClearPoolsUnderDirectory(directoryPath);
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }
}
