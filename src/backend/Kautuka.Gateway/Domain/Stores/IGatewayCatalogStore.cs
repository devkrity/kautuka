using System.Text.Json;

namespace Kautuka.Gateway.Domain.Stores;

internal interface IGatewayCatalogStore
{
    Task<bool> SeedRemoteMcpPresetAsync(string serverId, GatewayConnectorUpsertRequest request, CancellationToken cancellationToken = default)
        => Task.FromException<bool>(new NotSupportedException("This catalogue does not support remote preset seeding."));
    bool TryGetProvider(string id, out GatewayProvider? provider);
    IReadOnlyList<GatewayProvider> ListProviders();
    GatewayProvider SaveProvider(JsonElement body);
    bool DeleteProvider(string id);

    bool TryGetModel(string id, out GatewayModel? model);
    IReadOnlyList<GatewayModel> ListModels();
    GatewayModel SaveModel(JsonElement body);
    void UpdateModelCapabilities(string modelId, GatewayModelCapabilities capabilities);
    bool DeleteModel(string id);

    bool TryGetAgent(string id, out GatewayAgent? agent);
    IReadOnlyList<GatewayAgent> ListAgents();
    GatewayAgent SaveAgent(string agentId, GatewayAgentUpsertRequest request, string ownerUserId);
    Task<GatewayAgent> SaveAgentAsync(string agentId, GatewayAgentUpsertRequest request, string ownerUserId, CancellationToken cancellationToken = default)
        => Task.FromResult(SaveAgent(agentId, request, ownerUserId));
    bool DeleteAgent(string agentId);
    Task<bool> DeleteAgentAsync(string agentId, CancellationToken cancellationToken = default)
        => Task.FromResult(DeleteAgent(agentId));
    GatewayAgentRevisionsResponse? TryListAgentRevisions(string agentId) => null;
    GatewayAgentRevisionDiffResponse? TryGetAgentRevisionDiff(string agentId, long revision, long? fromRevision) => null;

    IReadOnlyList<GatewayMcpServer> ListMcpServers();
    bool TryGetMcpServer(string id, out GatewayMcpServer? server);
    GatewayMcpServer SaveMcpServer(string serverId, GatewayConnectorUpsertRequest request, string scope, string? ownerUserId);
    Task<GatewayMcpServer> SaveMcpServerAsync(string serverId, GatewayConnectorUpsertRequest request, string scope, string? ownerUserId, CancellationToken cancellationToken = default)
        => Task.FromResult(SaveMcpServer(serverId, request, scope, ownerUserId));
    Task<GatewayMcpServer> SaveSystemMcpServerAsync(string serverId, GatewayConnectorUpsertRequest request, CancellationToken cancellationToken = default)
        => SaveMcpServerAsync(serverId, request, GatewayOwnershipScopes.System, ownerUserId: null, cancellationToken);
    Task<GatewayMcpServer> MutateMcpCredentialAssociationAsync(string serverId, Func<GatewayMcpServer, CancellationToken, Task<(string AssociationId, string OperationId)>> stage, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    Task WithMcpServerLockAsync(string serverId, Func<CancellationToken, Task> action, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<GatewayMcpServer> ActivateAssociationDraftAsync(string serverId, GatewayConnectorUpsertRequest request, string expectedUpdatedAt, string credentialNamespace, string expectedSource, string operationId, string scope, string? ownerUserId, Func<GatewayMcpServer?, bool> authorize, Func<CancellationToken, Task<string>> stage, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    GatewayMcpServer SaveMcpServerDiscovery(string serverId, int? toolCount, IReadOnlyList<string>? toolNames);
    Task<GatewayMcpServer> SaveMcpServerDiscoveryAsync(string serverId, int? toolCount, IReadOnlyList<string>? toolNames, CancellationToken cancellationToken = default)
        => Task.FromResult(SaveMcpServerDiscovery(serverId, toolCount, toolNames));
    bool DeleteMcpServer(string serverId);
    Task<bool> DeleteMcpServerAsync(string serverId, CancellationToken cancellationToken = default)
        => Task.FromResult(DeleteMcpServer(serverId));

    IReadOnlyList<GatewaySkill> ListSkills();
    bool TryGetSkill(string id, out GatewaySkill? skill);
    GatewaySkill SaveSkill(string skillId, JsonElement body, string scope, string? ownerUserId);
    bool DeleteSkill(string skillId);
}
