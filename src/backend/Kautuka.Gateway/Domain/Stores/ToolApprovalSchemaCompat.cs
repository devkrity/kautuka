using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Domain.Stores;

/// <summary>
/// Additive <c>tool_approval_requests</c> upgrades that cannot live under
/// <c>Infrastructure/Database</c> (conformance forbids <c>ALTER TABLE</c> there).
/// Same pattern as channel outbox column ensures.
/// </summary>
internal static class ToolApprovalSchemaCompat
{
    public static void EnsureAgentDefinitionRevisionColumn(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (!TableExists(connection, "tool_approval_requests"))
        {
            return;
        }

        if (HasColumn(connection, "tool_approval_requests", "agent_definition_revision"))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "ALTER TABLE tool_approval_requests ADD COLUMN agent_definition_revision INTEGER";
        command.ExecuteNonQuery();
    }

    public static void EnsureWorkflowReviewColumns(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (!TableExists(connection, "tool_approval_requests"))
        {
            return;
        }

        EnsureColumn(connection, "kind", "TEXT NOT NULL DEFAULT 'tool'");
        EnsureColumn(connection, "workflow_id", "TEXT");
        EnsureColumn(connection, "instance_id", "TEXT");
        EnsureColumn(connection, "node_id", "TEXT");
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Column name and SQL type are closed identifiers from Ensure* callers.")]
    private static void EnsureColumn(SqliteConnection connection, string columnName, string columnSql)
    {
        if (HasColumn(connection, "tool_approval_requests", columnName))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE tool_approval_requests ADD COLUMN {columnName} {columnSql}";
        command.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1L;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Table name is a closed identifier from Ensure* callers.")]
    private static bool HasColumn(SqliteConnection connection, string tableName, string columnName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName})";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
