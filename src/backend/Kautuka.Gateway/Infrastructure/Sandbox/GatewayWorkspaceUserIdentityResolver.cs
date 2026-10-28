using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Infrastructure.Sandbox;

internal sealed class GatewayWorkspaceUserIdentityResolver(KautukaDatabase database) : IWorkspaceUserIdentityResolver
{
    public string? ResolveWorkspaceUserKey(string? userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            return userId;
        }

        try
        {
            using var connection = new SqliteConnection(database.ConnectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT username FROM runtime_users WHERE user_id = $userId LIMIT 1;";
            command.Parameters.AddWithValue("$userId", userId);
            object? scalar = command.ExecuteScalar();

            if (scalar is string username && !string.IsNullOrWhiteSpace(username))
            {
                return username;
            }
        }
        catch (SqliteException)
        {
            return userId;
        }
        catch (InvalidOperationException)
        {
            return userId;
        }

        return userId;
    }

    public string? ResolveAgentWorkspaceUserKey(string agentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        try
        {
            using var connection = new SqliteConnection(database.ConnectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COALESCE(NULLIF(users.username, ''), NULLIF(agents.owner_user_id, ''))
                FROM agents
                LEFT JOIN runtime_users AS users ON users.user_id = agents.owner_user_id
                WHERE agents.id = $agentId
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$agentId", agentId);
            object? scalar = command.ExecuteScalar();

            if (scalar is string ownerKey && !string.IsNullOrWhiteSpace(ownerKey))
            {
                return ownerKey;
            }
        }
        catch (SqliteException)
        {
            return agentId;
        }
        catch (InvalidOperationException)
        {
            return agentId;
        }

        return agentId;
    }
}
