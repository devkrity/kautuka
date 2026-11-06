using Kautuka.Gateway.Domain.Connectors;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Domain.Stores;

internal sealed partial class GatewayCatalogStore
{
    public async Task<bool> SeedRemoteMcpPresetAsync(string serverId, GatewayConnectorUpsertRequest request, CancellationToken cancellationToken = default)
    {
        if (!RemoteMcpPresets.Contains(serverId)) throw new InvalidOperationException("Unknown remote MCP preset.");
        SemaphoreSlim mutex = ConnectorMutationLock(serverId);
        await mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (HasRemotePresetReceipt(serverId)) return false;
            bool created = !state.McpServers.ContainsKey(serverId);
            if (created)
                _ = await SaveMcpServerCoreAsync(serverId, request, GatewayOwnershipScopes.System, null, false, cancellationToken).ConfigureAwait(false);
            RecordRemotePresetReceipt(serverId);
            return created;
        }
        finally { mutex.Release(); }
    }

    private bool HasRemotePresetReceipt(string serverId)
    {
        using var connection = new SqliteConnection(state.Database.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value_json FROM system_settings WHERE key = $key";
        command.Parameters.AddWithValue("$key", "remoteMcp.preset.seeded:" + serverId);
        return command.ExecuteScalar() is string;
    }

    private void RecordRemotePresetReceipt(string serverId)
    {
        using var connection = new SqliteConnection(state.Database.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO system_settings(key, value_json, updated_at) VALUES ($key, 'true', $now) ON CONFLICT(key) DO NOTHING";
        command.Parameters.AddWithValue("$key", "remoteMcp.preset.seeded:" + serverId);
        command.Parameters.AddWithValue("$now", Timestamp());
        _ = command.ExecuteNonQuery();
    }
}
