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
