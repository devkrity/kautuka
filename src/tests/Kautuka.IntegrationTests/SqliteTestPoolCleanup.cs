using Microsoft.Data.Sqlite;

namespace Kautuka.IntegrationTests;

internal static class SqliteTestPoolCleanup
{
    public static void ClearPoolsUnderDirectory(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return;
        }

        foreach (string databasePath in Directory.EnumerateFiles(directoryPath, "*.db", SearchOption.AllDirectories))
        {
            ClearPool(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
            }.ToString());

            ClearPool(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                Pooling = true,
            }.ToString());

            ClearPool(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                DefaultTimeout = 5,
            }.ToString());

            ClearPool(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Shared,
                Pooling = true,
            }.ToString());
        }
    }

    private static void ClearPool(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        SqliteConnection.ClearPool(connection);
    }
}
