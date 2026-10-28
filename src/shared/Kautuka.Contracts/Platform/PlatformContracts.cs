namespace Kautuka.Contracts.Platform;

public sealed record ServiceHealthResponse(
    string Status,
    string Service,
    string? AssignedId = null,
    string? AssignedKind = null,
    IReadOnlyList<string>? BuiltInTools = null);

public sealed record SystemHealthResponse(
    ServiceHealthResponse Runtime,
    ServiceHealthResponse Channels,
    ServiceHealthResponse Workspace,
    SystemHealthLifecycleResponse Lifecycle,
    IReadOnlyList<string>? ActionHints = null,
    IReadOnlyList<string>? DriftActions = null);

public sealed record SystemHealthLifecycleResponse(
    bool CoreReady,
    bool AgentsPending,
    bool AgentsReady,
    string Status);

public sealed record TopologyResponse(
    string GatewayService,
    IReadOnlyList<TopologyServiceItem> AgentServices);

public sealed record TopologyServiceItem(
    string ServiceName,
    string DefinitionId,
    string Kind);
