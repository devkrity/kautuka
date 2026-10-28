namespace Kautuka.Gateway.Hosting.Architecture;

internal static class GatewayArchitecturePolicy
{
    public static IReadOnlyDictionary<string, string> InMemoryDeliverySuppressions { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Domain/Execution/AgentChatService.Streaming.cs"] =
                "Per-invocation producer and consumer share one cancellation scope; canonical runtime events remain durable.",
            ["Domain/Execution/AgentExecutionEventWriter.cs"] =
                "Per-invocation handoff channel bridges async agent produce into the durable journal writer.",
            ["Domain/Execution/RuntimeEventPublisher.cs"] =
                "The owned retry task is cancelled and joined by IDisposable; the durable outbox is authoritative.",
            ["Domain/Execution/RuntimeEventStreamHub.cs"] =
                "The bounded channel is derived live delivery; reconnect replays canonical runtime history.",
            ["Domain/Memory/AgentMemoryEmbeddingReconciliation.cs"] =
                "Embedding work is derived and the hosted reconciliation service repairs dropped work.",
            ["Domain/Sessions/SessionRetentionQueue.cs"] =
                "Retention work is maintenance-only and is owned by a hosted dispatcher.",
            ["Infrastructure/Channels/ChannelHostOwnerTargetSync.cs"] =
                "Target synchronization is derived and the hosted reconciliation service repairs drift.",
            ["Domain/Workflows/WorkflowNaradStreamHub.cs"] =
                "The bounded channel is derived live delivery; reconnect replays the durable workflow Narad journal.",
            ["Domain/Workflows/WorkflowExecutionService.cs"] =
                "Enqueue is process-local; WorkflowRecoveryHostedService restarts recoverable instances from durable rows.",
        };

    public static IReadOnlyDictionary<string, string> DomainInfrastructureSuppressions { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Domain/Channels/ChannelAttachmentRegistry.cs"] = "Channel transport persistence is not yet extracted behind a domain contract.",
            ["Domain/Channels/ChannelIngressService.cs"] = "Channel transport persistence is not yet extracted behind a domain contract.",
            ["Domain/Channels/ChannelIngressService.ChannelMessageSendResult.cs"] = "Observability is a shared kernel pending namespace relocation.",
            ["Domain/Channels/ChannelIngressService.ChannelMessaging.cs"] = "Observability is a shared kernel pending namespace relocation.",
            ["Domain/Channels/ChannelOutboundMessagePublisher.cs"] = "Observability is a shared kernel pending namespace relocation.",
            ["Domain/Channels/ChannelRegistrationProvisioningService.cs"] = "Channel transport persistence is not yet extracted behind a domain contract.",
            ["Domain/Execution/AgentChatService.Streaming.cs"] = "Observability is a shared kernel pending namespace relocation.",
            ["Domain/Execution/AgentChatService.Streaming.Packed.cs"] = "Observability is a shared kernel pending namespace relocation.",
            ["Domain/Execution/ChatGatewayService.cs"] = "Observability is a shared kernel pending namespace relocation.",
            ["Domain/Hitl/ApprovalContinuationDispatcherService.cs"] = "Observability is a shared kernel pending namespace relocation.",
            ["Domain/Hitl/HitlEventHandlers.cs"] = "Observability is a shared kernel pending namespace relocation.",
            ["Domain/Stores/GatewayApprovalStore.cs"] = "Observability is a shared kernel pending namespace relocation.",
            ["Domain/Stores/GatewaySettingsStore.cs"] = "Provider catalog and update clients are pending extraction behind domain contracts.",
            ["Domain/Stores/IRunWaitStateStore.cs"] = "Observability is a shared kernel pending namespace relocation.",
        };

    public static IReadOnlyDictionary<string, string> DomainEndpointSuppressions { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Domain/Execution/ChatGatewayService.cs"] =
                "Narad projection contracts are pending relocation from Endpoints into the execution kernel.",
        };

    public static IReadOnlyDictionary<string, string> InfrastructureEndpointSuppressions { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}
