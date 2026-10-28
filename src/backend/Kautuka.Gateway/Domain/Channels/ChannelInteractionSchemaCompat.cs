using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Domain.Channels;

/// <summary>Registers the non-destructive schema upgrade for linked channel interactions.</summary>
internal static class ChannelInteractionSchemaCompat
{
    public static void EnsureInteractionGroupColumn(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!TableExists(connection)) return;

        using SqliteCommand columns = connection.CreateCommand();
        columns.CommandText = "PRAGMA table_info(runtime_channel_interactions)";
        using SqliteDataReader reader = columns.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), "interaction_group_id", StringComparison.OrdinalIgnoreCase))
                return;
        }
        reader.Close();

        using SqliteCommand alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE runtime_channel_interactions ADD COLUMN interaction_group_id TEXT NULL";
        alter.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'runtime_channel_interactions'";
        return command.ExecuteScalar() is not null;
    }
}
