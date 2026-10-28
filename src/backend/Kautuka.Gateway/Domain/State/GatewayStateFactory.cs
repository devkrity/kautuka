using Kautuka.Gateway.Domain.Memory;
using Kautuka.Gateway.Domain.Sandbox;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Domain.State;

internal static partial class GatewayStateFactory
{
    public static GatewayState FromDatabase(
        KautukaDatabase database,
        ILogger<GatewayState> logger,
        IGatewayMemoryStore memoryStore,
        IAgentDefinitionStore agentDefinitionStore,
        IConfiguration configuration,
        IWorkspaceRuntimeTool workspaceTool)
    {
        if (!database.IsInitialized())
        {
            throw new InvalidOperationException(
                "kautuka.db is not initialized. Ensure data/kautuka.db exists and contains initialized schema before startup.");
        }

        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();

        var agentStoreDatabase = new SqliteAgentStoreDatabase(configuration);
        GatewayState state = GatewayState.Create(
            database,
            logger,
            agentDefinitionStore,
            new SqliteAgentRuntimeSessionRepository(agentStoreDatabase),
            new SqliteAgentRuntimeRunRepository(agentStoreDatabase),
            new SqliteAgentMemoryFactRepository(agentStoreDatabase),
            memoryStore);
        GatewayState.HydrateFromDatabase(connection, state, workspaceTool, configuration, agentDefinitionStore);
        state.PersistRawSettingsAuthoritatively(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["topology_hash"] = state.TopologyHash ?? string.Empty,
            ["desired_state.version"] = state.DesiredStateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["desired_state.content_hash"] = state.DesiredStateContentHash ?? string.Empty,
            ["desired_state.publisher_epoch"] = state.DesiredStatePublisherEpoch,
        });
        return state;
    }
}
