using Microsoft.Data.Sqlite;

namespace Kautuka.IntegrationTests;

public sealed class SqliteTestPoolCleanupTests
{
    [Fact]
    public void ClearingOneFixturePoolsDoesNotInvalidateAnotherFixturesConnection()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-sqlite-pool-tests", Guid.NewGuid().ToString("N"));
        string firstRoot = Path.Combine(root, "first");
        string secondRoot = Path.Combine(root, "second");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(secondRoot);

        try
        {
            string firstConnectionString = CreateGatewayConnectionString(Path.Combine(firstRoot, "kautuka.db"));
            using (var firstConnection = new SqliteConnection(firstConnectionString))
            {
                firstConnection.Open();
            }

            string secondConnectionString = CreateGatewayConnectionString(Path.Combine(secondRoot, "kautuka.db"));
            using var secondConnection = new SqliteConnection(secondConnectionString);
            secondConnection.Open();
            using (SqliteCommand create = secondConnection.CreateCommand())
            {
                create.CommandText = "CREATE TABLE fixture_state (value INTEGER NOT NULL); INSERT INTO fixture_state VALUES (42);";
                create.ExecuteNonQuery();
            }

            SqliteTestPoolCleanup.ClearPoolsUnderDirectory(firstRoot);

            using SqliteCommand query = secondConnection.CreateCommand();
            query.CommandText = "SELECT value FROM fixture_state;";
            Assert.Equal(42L, query.ExecuteScalar());
        }
        finally
        {
            SqliteTestPoolCleanup.ClearPoolsUnderDirectory(root);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static string CreateGatewayConnectionString(string path)
        => new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
}
