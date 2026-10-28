using System.Collections.ObjectModel;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Infrastructure.Configuration;

/// <summary>
/// Reads platform topology and infrastructure settings from kautuka.db.
/// Fails fast with a clear diagnostic if the DB has not been initialized.
/// Native AOT safe: manual SQL, ordinal-indexed column reads, no reflection.
/// </summary>
internal partial class SqlitePlatformConfigurationProvider(
    KautukaDatabase database,
    IAgentDefinitionStore agentDefinitionStore) : IPlatformConfigurationProvider
{
    private static readonly PlatformConfigurationJsonContext jsonContext = PlatformConfigurationJsonContext.Default;

    public PlatformConfiguration GetCurrent()
    {
        if (!database.IsInitialized())
        {
            throw new InvalidOperationException(
                "kautuka.db has not been initialized. Ensure data/kautuka.db exists and contains initialized schema.");
        }

        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();

        return new PlatformConfiguration
        {
            Providers = new Collection<ProviderDefinition>(LoadProviders(connection)),
            Models = new Collection<ModelDefinition>(LoadModels(connection)),
            Agents = new Collection<AgentDefinition>(LoadAgents(connection)),
            McpServers = new Collection<McpServerDefinition>(LoadMcpServers(connection)),
            Skills = new Collection<SkillDefinition>(LoadSkills(connection)),
            Kautuka = LoadInfrastructureDefinition(connection),
            System = LoadSystemDefinition(connection),
            ProviderPathPolicy = LoadProviderPathPolicySettings(connection),
            Memory = LoadMemorySettings(connection),
        };
    }

    // ── providers ────────────────────────────────────────────────────────────
}
