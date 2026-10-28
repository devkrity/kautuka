using Microsoft.Data.Sqlite;

namespace Kautuka.IntegrationTests;

public sealed class SqliteUsageSchemaBootstrapperTests
{
    [Fact]
    public void FreshAndExistingCurrentUsageDatabaseAreAccepted()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        SqliteUsageSchemaBootstrapper.EnsureCurrent(connection);
        using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.CommandText =
                """
                INSERT INTO runtime_run_usage
                    (id, usage_fact_version, run_id, target_type, target_id, run_status,
                     run_started_at, run_ended_at, total_duration_ms, usage_date, created_at)
                VALUES ('usage-1', 1, 'run-1', 'agent', 'agent-1', 'completed',
                        'now', 'now', 1, '2026-07-15', 'now');
                """;
            insert.ExecuteNonQuery();
        }

        SqliteUsageSchemaBootstrapper.EnsureCurrent(connection);

        using SqliteCommand count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM runtime_run_usage WHERE id = 'usage-1'";
        Assert.Equal(1L, count.ExecuteScalar());
    }
}
