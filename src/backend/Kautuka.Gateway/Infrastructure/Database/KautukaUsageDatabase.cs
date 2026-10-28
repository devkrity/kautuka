using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Infrastructure.Database;

internal sealed partial class KautukaUsageDatabase
{
    private readonly string databasePath;

    public string ConnectionString { get; }
    public string DataDirectory { get; }
    public bool IsAvailable { get; private set; }

    public KautukaUsageDatabase(IConfiguration configuration)
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

    public void TryEnsureReady(ILogger logger)
    {
        try
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();

            using var pragmaCmd = connection.CreateCommand();
            pragmaCmd.CommandText =
                "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=1000; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
            pragmaCmd.ExecuteNonQuery();

            SqliteUsageSchemaBootstrapper.EnsureCurrent(connection);
            SetupPossession.RestrictToOwner(databasePath);
            if (File.Exists(databasePath + "-wal"))
            {
                SetupPossession.RestrictToOwner(databasePath + "-wal");
            }

            if (File.Exists(databasePath + "-shm"))
            {
                SetupPossession.RestrictToOwner(databasePath + "-shm");
            }

            IsAvailable = true;
        }
        catch (SqliteException ex)
        {
            IsAvailable = false;
            LogUsageDatabaseInitializationFailed(logger, ex);
        }
        catch (InvalidOperationException ex)
        {
            IsAvailable = false;
            LogUsageDatabaseInitializationFailed(logger, ex);
        }
    }

    private static string ResolvePath(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return KautukaHostPathPolicy.ResolveSharedFilePath(configuration, "kautuka-usage.db");
    }

    [LoggerMessage(EventId = 0, Level = LogLevel.Warning, Message = "Usage database initialization failed. Usage capture will stay disabled.")]
    private static partial void LogUsageDatabaseInitializationFailed(ILogger logger, Exception exception);
}
