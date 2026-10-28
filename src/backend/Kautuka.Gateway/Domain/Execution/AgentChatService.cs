using Kautuka.Gateway.Domain.Assets;
using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Runtime;
using Kautuka.Gateway.Domain.Sandbox;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Domain.Usage;

using Mediator;

namespace Kautuka.Gateway.Domain.Execution;

internal sealed partial class AgentChatService(
    IAgentRuntimeFactory runtimeFactory,
    IModelCapabilityService capabilityService,
    IProviderPathObservationStore observationStore,
    IMediator mediator,
    ILogger<AgentChatService> logger,
    IGatewayApprovalStore approvalStore,
    IGatewayContinuationStore continuationStore,
    IResumeTokenProvider resumeTokenProvider,
    IConversationAssetStore assetStore,
    IConversationAssetIngestionService assetIngestionService,
    GatewayState gatewayState,
    IAgentRunMetrics agentRunMetrics,
    IGatewayUsageCaptureAccessor usageCaptureAccessor,
    IGatewaySessionStore sessionStore,
    IAgentToolInvocationLinkStore agentToolInvocationLinkStore,
    IRunWaitStateStore runWaitStateStore,
    IPlatformConfigurationProvider platformConfigurationProvider,
    IToolExecutionPolicyService toolExecutionPolicyService,
    IRuntimeEventPublisher runtimeEventPublisher,
    IModelTurnOrdinalCounter modelTurnOrdinalCounter,
    IRuntimeEventHistoryReader runtimeEventHistoryReader,
    IAgentExecutionReadinessService executionReadinessService,
    IConfiguration configuration,
    IPackedApprovedToolInvoker? packedApprovedToolInvoker = null,
    IRunStopRegistry? runStopRegistry = null,
    IRunLimitResolver? runLimitResolver = null,
    IToolInvocationSettingsResolver? toolInvocationSettingsResolver = null,
    IWorkspaceRuntimeTool? workspaceTool = null) : IAgentChatService
{
    private IGatewayContinuationStore GetContinuationStore() => continuationStore;

    private IResumeTokenProvider GetResumeTokenProvider() => resumeTokenProvider;

    private IAgentRunMetrics GetAgentRunMetrics() => agentRunMetrics;

    private IGatewaySessionStore GetSessionStore() => sessionStore;

    private IAgentToolInvocationLinkStore GetAgentToolInvocationLinkStore() => agentToolInvocationLinkStore;

    private IRunWaitStateStore GetRunWaitStateStore() => runWaitStateStore;

    private IRuntimeEventPublisher GetRuntimeEventPublisher() => runtimeEventPublisher;

    private IRuntimeEventHistoryReader GetRuntimeEventHistoryReader() => runtimeEventHistoryReader;

    private IRunStopRegistry? GetRunStopRegistry() => runStopRegistry;

    private IGatewayUsageCaptureAccessor GetUsageCaptureAccessor() => usageCaptureAccessor;

    private string? GetTelemetryTenantId()
    {
        string? tenantId = configuration[KautukaTelemetryDefaults.TenantIdKey];
        return string.IsNullOrWhiteSpace(tenantId) ? null : tenantId.Trim();
    }
}
