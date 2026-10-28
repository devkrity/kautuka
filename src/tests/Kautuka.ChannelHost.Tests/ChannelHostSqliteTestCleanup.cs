using Microsoft.Data.Sqlite;

namespace Kautuka.ChannelHost.Tests;

internal static class ChannelHostSqliteTestCleanup
{
    public static void ReleasePool(ChannelHostDatabase database)
        => ReleasePool(database.ConnectionString);

    public static void ReleasePoolForStateRoot(string stateRoot)
    {
        string databasePath = Path.Combine(stateRoot, "channelHost", "channelhost.db");
        if (!File.Exists(databasePath))
        {
            return;
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Cache = SqliteCacheMode.Shared,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        };
        ReleasePool(builder.ToString());
    }

    public static void ReleaseAndDelete(string stateRoot)
    {
        ReleasePoolForStateRoot(stateRoot);
        if (Directory.Exists(stateRoot))
        {
            Directory.Delete(stateRoot, recursive: true);
        }
    }

    private static void ReleasePool(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        SqliteConnection.ClearPool(connection);
    }
}
