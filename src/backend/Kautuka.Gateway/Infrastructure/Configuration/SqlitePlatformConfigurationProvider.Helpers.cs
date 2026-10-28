using System.Globalization;
using System.Text.Json;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Infrastructure.Configuration;

internal partial class SqlitePlatformConfigurationProvider
{
    private static string? LoadSetting(SqliteConnection connection, string key)
    {
        string? raw = QueryFirstOrDefault(
            static conn => new SqliteCommand("SELECT value_json FROM system_settings WHERE key = $key", conn),
            connection,
            command => command.Parameters.AddWithValue("$key", key),
            reader => reader.IsDBNull(0) ? null : reader.GetString(0));
        if (raw is null) return null;
        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
            return raw[1..^1];
        return raw;
    }

    private static int LoadInt(SqliteConnection connection, string key, int fallback)
    {
        string? raw = LoadSetting(connection, key);
        return raw is not null && int.TryParse(raw, out int v) ? v : fallback;
    }

    private static Dictionary<string, string> LoadJsonDict(SqliteConnection connection, string key)
    {
        string? raw = QueryFirstOrDefault(
            static conn => new SqliteCommand("SELECT value_json FROM system_settings WHERE key = $key", conn),
            connection,
            command => command.Parameters.AddWithValue("$key", key),
            reader => reader.IsDBNull(0) ? null : reader.GetString(0));
        if (raw is null) return new Dictionary<string, string>();
        try
        {
            return JsonSerializer.Deserialize(raw, jsonContext.DictionaryStringString) ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
        catch (NotSupportedException)
        {
            return new Dictionary<string, string>();
        }
    }

    private static double LoadDouble(SqliteConnection connection, string key, double fallback)
    {
        string? raw = LoadSetting(connection, key);
        return raw is not null && double.TryParse(raw,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : fallback;
    }

    private static List<T> QueryRows<T>(Func<SqliteConnection, SqliteCommand> commandFactory, SqliteConnection connection, Func<SqliteDataReader, T> map, Action<SqliteCommand>? configure = null)
    {
        using var command = commandFactory(connection);
        configure?.Invoke(command);

        using SqliteDataReader reader = command.ExecuteReader();
        var results = new List<T>();
        while (reader.Read())
        {
            results.Add(map(reader));
        }

        return results;
    }

    private static T? QueryFirstOrDefault<T>(Func<SqliteConnection, SqliteCommand> commandFactory, SqliteConnection connection, Action<SqliteCommand>? configure, Func<SqliteDataReader, T?> map)
    {
        using var command = commandFactory(connection);
        configure?.Invoke(command);

        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? map(reader) : default;
    }

    private static long ExecuteScalarInt64(Func<SqliteConnection, SqliteCommand> commandFactory, SqliteConnection connection)
    {
        using var command = commandFactory(connection);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }
}
