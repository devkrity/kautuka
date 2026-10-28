using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Domain.Execution;

internal interface IPersonaSettingsReader
{
    JsonObject ResolveProfile(string agentId);
}

internal sealed class PersonaSettingsReader(IGatewaySettingsStore settingsStore) : IPersonaSettingsReader
{
    public JsonObject ResolveProfile(string agentId) => settingsStore.GetAgentPersonaProfile(agentId);
}
