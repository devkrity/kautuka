using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Infrastructure.Configuration;

internal sealed class SqliteProviderTypeCatalog(KautukaDatabase database) : IProviderTypeCatalog
{
    private readonly object syncLock = new();

    public IReadOnlyList<ProviderTypeInfo> ListProviderTypes()
    {
        return [.. ListCatalogEntries()
            .Select(static entry => new ProviderTypeInfo(
                entry.Type,
                entry.FriendlyName,
                entry.RequiresApiKey,
                entry.RequiresBaseUrl,
                entry.SupportsHttpTimeoutSeconds))];
    }

    public IReadOnlyList<ProviderCatalogEntry> ListCatalogEntries()
    {
        return [.. LoadEntries().OrderBy(static entry => entry.FriendlyName, StringComparer.OrdinalIgnoreCase)];
    }

    public bool TryGet(string providerType, out AiProviderCatalog.ProviderMetadata? metadata)
    {
        ProviderCatalogEntry? entry = LoadEntries()
            .FirstOrDefault(entry => string.Equals(entry.Type, providerType, StringComparison.OrdinalIgnoreCase));
        bool found = entry is not null;
        metadata = found
            ? new AiProviderCatalog.ProviderMetadata(
                entry!.FriendlyName,
                entry.DefaultBaseUrl,
                entry.DefaultHttpTimeoutSeconds,
                entry.RequiresApiKey,
                entry.RequiresBaseUrl,
                entry.SupportsHttpTimeoutSeconds)
            : null;
        return found;
    }

    public void SaveCatalogEntries(IReadOnlyList<ProviderCatalogEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        lock (syncLock)
        {
            using var connection = new SqliteConnection(database.ConnectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();

            foreach (ProviderCatalogEntry entry in entries)
            {
                using SqliteCommand update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText =
                    """
                    UPDATE provider_type_catalog
                    SET friendly_name = $friendly_name,
                        default_base_url = $default_base_url,
                        default_http_timeout_seconds = $default_http_timeout_seconds,
                        updated_at = $updated_at
                    WHERE type = $type AND is_builtin = 1
                    """;
                update.Parameters.AddWithValue("$type", entry.Type);
                update.Parameters.AddWithValue("$friendly_name", entry.FriendlyName);
                update.Parameters.AddWithValue("$default_base_url", (object?)entry.DefaultBaseUrl ?? DBNull.Value);
                update.Parameters.AddWithValue(
                    "$default_http_timeout_seconds",
                    entry.DefaultHttpTimeoutSeconds is int defaultHttpTimeoutSeconds ? defaultHttpTimeoutSeconds : DBNull.Value);
                update.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
                _ = update.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    private List<ProviderCatalogEntry> LoadEntries()
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT type, friendly_name, default_base_url, default_http_timeout_seconds, requires_api_key, requires_base_url, supports_http_timeout_seconds, is_builtin, seed_version FROM provider_type_catalog ORDER BY rowid";

        using SqliteDataReader reader = command.ExecuteReader();
        var entries = new List<ProviderCatalogEntry>();
        while (reader.Read())
        {
            entries.Add(new ProviderCatalogEntry(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.GetInt32(4) != 0,
                reader.GetInt32(5) != 0,
                reader.GetInt32(6) != 0,
                reader.GetInt32(7) != 0,
                reader.GetInt32(8)));
        }

        return entries;
    }
}
