using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Infrastructure.Database;

/// <summary>
/// Single source of truth for the kautuka.db SQLite registry.
/// Resolves the DB path, ensures WAL mode, and bootstraps or validates the canonical schema.
/// Designed for Native AOT: manual SQL only, no reflection.
/// </summary>
internal sealed class KautukaDatabase
{
    private readonly string databasePath;

    public string ConnectionString { get; }
    public string DataDirectory { get; }

    public KautukaDatabase(IConfiguration configuration)
    {
        databasePath = ResolvePath(configuration);
        string dbPath = databasePath;
        DataDirectory = Path.GetDirectoryName(dbPath)
            ?? Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "data"));
        Directory.CreateDirectory(DataDirectory);

        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
    }

    /// <summary>Creates a new canonical database or validates an existing one. Call once at startup.</summary>
    public void EnsureReady()
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        using var pragmaCmd = connection.CreateCommand();
        pragmaCmd.CommandText =
            "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
        pragmaCmd.ExecuteNonQuery();

        SqliteGatewaySchemaBootstrapper.EnsureCurrent(connection);
        RestrictDatabaseFiles();
    }

    private void RestrictDatabaseFiles()
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

    /// <summary>Returns true when the schema has been applied (DB is usable for reads/writes).</summary>
    public bool IsInitialized()
    {
        try
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();

            SqliteGatewaySchemaBootstrapper.ValidateExisting(connection);
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves the canonical kautuka.db path from the shared hosting layout.
    /// </summary>
    public static string ResolvePath(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return KautukaHostPathPolicy.ResolveSharedFilePath(configuration, "kautuka.db");
    }
}
