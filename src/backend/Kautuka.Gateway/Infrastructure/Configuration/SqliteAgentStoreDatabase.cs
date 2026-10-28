using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Infrastructure.Configuration;

/// <summary>Owns agent-store location, connection policy, and schema activation for bounded repositories.</summary>
internal sealed class SqliteAgentStoreDatabase(IConfiguration configuration)
{
    private const string agentDatabaseFileName = "agent.db";

    public bool Exists(string storageLocatorKey)
        => File.Exists(GetDatabasePath(storageLocatorKey));

    public SqliteConnection OpenCurrentConnection(string storageLocatorKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageLocatorKey);
        string directory = Path.GetDirectoryName(GetDatabasePath(storageLocatorKey))
            ?? throw new InvalidOperationException("Agent database path has no parent directory.");
        Directory.CreateDirectory(directory);

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = GetDatabasePath(storageLocatorKey),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            using (SqliteCommand pragma = connection.CreateCommand())
            {
                pragma.CommandText =
                    "PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
                pragma.ExecuteNonQuery();
            }

            SqliteAgentStoreSchema.EnsureCurrent(connection);
            RestrictStoreFiles(GetDatabasePath(storageLocatorKey));
            return connection;
        }
        catch
        {
            SqliteAgentStoreSchema.Forget(connection.DataSource);
            connection.Dispose();
            throw;
        }
    }

    private static void RestrictStoreFiles(string databasePath)
    {
        SetupPossession.RestrictToOwner(databasePath);
        if (File.Exists(databasePath + "-wal"))
        {
            SetupPossession.RestrictToOwner(databasePath + "-wal");
        }

        if (File.Exists(databasePath + "-shm"))
        {
            SetupPossession.RestrictToOwner(databasePath + "-shm");
        }
    }

    private string GetDatabasePath(string storageLocatorKey)
        => Path.Combine(
            AgentStoragePath.RequireAgentDirectory(
                KautukaHostPathPolicy.ResolveSharedStateRoot(configuration),
                storageLocatorKey),
            agentDatabaseFileName);
}
