using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Infrastructure.Configuration;

internal partial class SqliteTopologyConfigurationWriter(KautukaDatabase database, IPlatformConfigurationProvider configurationProvider)
    : ITopologyConfigurationWriter
{
    private readonly object lockObject = new();
    private static readonly PlatformConfigurationJsonContext jsonContext = PlatformConfigurationJsonContext.Default;

    public PlatformConfiguration SaveProvider(ProviderDefinition provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        lock (lockObject)
        {
            UpsertProvider(provider);
            return configurationProvider.GetCurrent();
        }
    }

    public PlatformConfiguration DeleteProvider(string providerId)
    {
        lock (lockObject)
        {
            using var connection = new SqliteConnection(database.ConnectionString);
            connection.Open();
            ExecuteNonQuery(connection, static (conn, tx) => new SqliteCommand("DELETE FROM providers WHERE id = $id", conn, tx), command => command.Parameters.AddWithValue("$id", providerId));
            return configurationProvider.GetCurrent();
        }
    }

    public PlatformConfiguration SaveModel(ModelDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);

        lock (lockObject)
        {
            UpsertModel(model);
            return configurationProvider.GetCurrent();
        }
    }

    public PlatformConfiguration DeleteModel(string modelId)
    {
        lock (lockObject)
        {
            using var connection = new SqliteConnection(database.ConnectionString);
            connection.Open();
            ExecuteNonQuery(connection, static (conn, tx) => new SqliteCommand("DELETE FROM models WHERE id = $id", conn, tx), command => command.Parameters.AddWithValue("$id", modelId));
            return configurationProvider.GetCurrent();
        }
    }

    public PlatformConfiguration SaveAgent(AgentRegistryEntry agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        lock (lockObject)
        {
            UpsertAgent(agent);
            return configurationProvider.GetCurrent();
        }
    }

    public PlatformConfiguration DeleteAgent(string agentId)
    {
        lock (lockObject)
        {
            using var connection = new SqliteConnection(database.ConnectionString);
            connection.Open();
            ExecuteNonQuery(connection, static (conn, tx) => new SqliteCommand("DELETE FROM agents WHERE id = $id", conn, tx), command => command.Parameters.AddWithValue("$id", agentId));
            return configurationProvider.GetCurrent();
        }
    }

    public PlatformConfiguration SaveMcpServer(McpServerDefinition server)
    {
        ArgumentNullException.ThrowIfNull(server);

        lock (lockObject)
        {
            UpsertMcpServer(server);
            return configurationProvider.GetCurrent();
        }
    }

    public PlatformConfiguration ClearMcpServerDiscovery(string serverId)
    {
        lock (lockObject)
        {
            using var connection = new SqliteConnection(database.ConnectionString);
            connection.Open();
            ExecuteNonQuery(connection, static (conn, tx) => new SqliteCommand("UPDATE mcp_servers SET tool_count=NULL, tool_names_json=NULL WHERE id=$id", conn, tx), command => command.Parameters.AddWithValue("$id", serverId));
            return configurationProvider.GetCurrent();
        }
    }

    public void SwitchMcpCredentialAssociation(string serverId, string credentialNamespace, string expectedSource, string associationId, string operationId)
    {
        lock (lockObject)
        {
            using var connection = new SqliteConnection(database.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE mcp_servers SET remote_config_json=json_set(remote_config_json,'$.credentialAssociationId',$association,'$.credentialOperationId',$operation,'$.credentialSourceId',$source) WHERE id=$id AND json_extract(remote_config_json,'$.credentialNamespace')=$namespace AND COALESCE(json_extract(remote_config_json,'$.credentialAssociationId'),'')=$source";
            command.Parameters.AddWithValue("$id", serverId); command.Parameters.AddWithValue("$namespace", credentialNamespace);
            command.Parameters.AddWithValue("$source", expectedSource); command.Parameters.AddWithValue("$association", associationId); command.Parameters.AddWithValue("$operation", operationId);
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("Connector credential association changed.");
        }
    }

    public PlatformConfiguration DeleteMcpServer(string serverId)
    {
        lock (lockObject)
        {
            using var connection = new SqliteConnection(database.ConnectionString);
            connection.Open();
            ExecuteNonQuery(connection, static (conn, tx) => new SqliteCommand("DELETE FROM mcp_servers WHERE id = $id", conn, tx), command => command.Parameters.AddWithValue("$id", serverId));
            return configurationProvider.GetCurrent();
        }
    }

    public PlatformConfiguration SaveSkill(SkillDefinition skill)
    {
        ArgumentNullException.ThrowIfNull(skill);

        lock (lockObject)
        {
            UpsertSkill(skill);
            return configurationProvider.GetCurrent();
        }
    }

    public PlatformConfiguration DeleteSkill(string skillId)
    {
        lock (lockObject)
        {
            using var connection = new SqliteConnection(database.ConnectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();

            ExecuteNonQuery(connection, static (conn, tx) => new SqliteCommand("DELETE FROM skills WHERE id = $id", conn, tx), command => command.Parameters.AddWithValue("$id", skillId), transaction);

            transaction.Commit();
            return configurationProvider.GetCurrent();
        }
    }
}
