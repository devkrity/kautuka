using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Infrastructure.Database;

internal static class SqliteUsageSchemaBootstrapper
{
    private const string schemaId = "usage-2026-07-15.1";

    public static void EnsureCurrent(SqliteConnection connection)
    {
        Dictionary<(string Type, string Name), string> actual = ReadSchema(connection);
        if (actual.Count == 0)
        {
            using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Versioned embedded SQL; never contains external input.
            command.CommandText = SqliteEmbeddedSql.BaselineUsageV1;
#pragma warning restore CA2100
            command.ExecuteNonQuery();
            actual = ReadSchema(connection);
        }

        using var expectedConnection = new SqliteConnection("Data Source=:memory:");
        expectedConnection.Open();
        using (SqliteCommand command = expectedConnection.CreateCommand())
        {
#pragma warning disable CA2100 // Versioned embedded SQL; never contains external input.
            command.CommandText = SqliteEmbeddedSql.BaselineUsageV1;
#pragma warning restore CA2100
            command.ExecuteNonQuery();
        }

        Dictionary<(string Type, string Name), string> expected = ReadSchema(expectedConnection);
        bool current = expected.Count == actual.Count
            && expected.All(entry => actual.TryGetValue(entry.Key, out string? sql)
                && string.Equals(Normalize(entry.Value), Normalize(sql), StringComparison.Ordinal));
        if (!current)
        {
            throw new InvalidOperationException(
                $"The usage schema is incompatible with this build ({schemaId}). Automatic migration and " +
                "destructive repair are disabled. Explicitly reset or reprovision the usage database.");
        }
    }

    private static Dictionary<(string Type, string Name), string> ReadSchema(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT type, name, sql FROM sqlite_schema WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' ORDER BY type, name";
        var result = new Dictionary<(string Type, string Name), string>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add((reader.GetString(0), reader.GetString(1)), reader.GetString(2));
        }

        return result;
    }

    private static string Normalize(string sql)
        => string.Concat(sql.Where(character => !char.IsWhiteSpace(character))).ToUpperInvariant();
}
