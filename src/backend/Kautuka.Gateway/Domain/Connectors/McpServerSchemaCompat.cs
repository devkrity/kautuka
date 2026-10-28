using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Domain.Connectors;

/// <summary>
/// Additive mcp_servers upgrades that cannot live under Infrastructure/Database
/// (conformance forbids ALTER TABLE there).
/// </summary>
internal static class McpServerSchemaCompat
{
    /// <summary>
    /// Protocol the seeded MarkItDown image negotiates. Existing rows keep their saved value.
    /// </summary>
    internal const string MarkItDownInitialProtocolVersion = "2024-11-05";

    public static void EnsureProtocolVersionColumn(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (!TableExists(connection, "mcp_servers") || HasColumn(connection, "mcp_servers", "mcp_protocol_version"))
        {
            return;
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE mcp_servers ADD COLUMN mcp_protocol_version TEXT NULL";
        alter.ExecuteNonQuery();

        using var backfill = connection.CreateCommand();
        backfill.CommandText =
            """
            UPDATE mcp_servers
            SET mcp_protocol_version = $protocolVersion
            WHERE id = 'markitdown'
              AND (mcp_protocol_version IS NULL OR mcp_protocol_version = '')
            """;
        backfill.Parameters.AddWithValue("$protocolVersion", MarkItDownInitialProtocolVersion);
        backfill.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COUNT(*)
            FROM sqlite_schema
            WHERE type = 'table'
              AND name = $name
            """;
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Table name is a closed identifier from EnsureProtocolVersionColumn.")]
    private static bool HasColumn(SqliteConnection connection, string tableName, string columnName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({tableName})";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
