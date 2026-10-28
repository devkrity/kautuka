using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kautuka.Gateway.Domain.Stores;

internal interface IGatewaySettingsStore
{
    void Save(JsonElement body);
    Task SaveAsync(JsonElement body, CancellationToken cancellationToken = default)
    {
        Save(body);
        return Task.CompletedTask;
    }
    GatewaySystemSettingsResponse GetPayload();
    GatewayAdvancedSettingsResponse GetAdvancedSettingsPayload();
    Task SaveAdvancedSettingsAsync(JsonElement body, CancellationToken cancellationToken = default);
    void SaveProviderPathPolicySettings(JsonElement body);
    void SaveMemorySettings(JsonElement body);
    GatewayProviderPathPolicySettingsResponse GetProviderPathPolicyPayload();
    GatewayMemorySettingsResponse GetMemorySettingsPayload();
    GatewayPersonaDefinitionsResponse GetPersonaDefinitionsPayload();
    void SavePersonaDefinitions(JsonElement body);
    GatewayPersonaProfilesResponse GetPersonaProfilesPayload();
    JsonObject GetAgentPersonaProfile(string agentId);
    JsonObject NormalizeAgentPersonaProfile(JsonObject? profile);
    JsonObject ResolveAgentPersonaProfile(JsonObject? profile);
    GatewayOrganizationSettingsResponse GetOrganizationPayload();
    void SaveOrganization(JsonElement body);
    string? ReadSettingJson(string key);
    void WriteSettingJson(string key, string rawJson);
}
