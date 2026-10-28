namespace Kautuka.Gateway.Domain.Jobs;

internal enum BackgroundWorkGuarantee
{
    Durable,
    RecoverableDerived,
    BestEffort,
}

internal sealed record BackgroundWorkDescriptor(
    string WorkerName,
    BackgroundWorkGuarantee Guarantee,
    int Capacity,
    string OverloadBehavior,
    string Recovery);

internal static class BackgroundWorkDeliveryCatalog
{
    public static IReadOnlyList<BackgroundWorkDescriptor> All { get; } =
    [
        new("ApprovalContinuationDispatcherService", BackgroundWorkGuarantee.Durable, 0, "leased database jobs", "expired leases are reclaimed"),
        new("ApprovalResolutionEventDispatcher", BackgroundWorkGuarantee.Durable, 0, "leased canonical event staging", "expired leases are reclaimed with bounded retry and dead-letter state"),
        new("AutopilotSchedulerService", BackgroundWorkGuarantee.Durable, 0, "leased database schedules", "startup reconciliation"),
        new("ChannelHostIngressWorker", BackgroundWorkGuarantee.Durable, 0, "ChannelHost-owned inbox", "redelivery from ChannelHost"),
        new("ChannelHostOutboxWorker", BackgroundWorkGuarantee.Durable, 0, "database outbox", "retry with durable status"),
        new("SessionTurnQueueWorker", BackgroundWorkGuarantee.Durable, 10000, "reject admission at session, count or byte limits", "expired leases recover against stable run history without replaying admitted execution"),
        new("ChannelSessionUiWorker", BackgroundWorkGuarantee.RecoverableDerived, 0, "single sequential presentation scan", "rebuild queue and session views from Gateway state every three seconds; transport intents use the durable outbox"),
        new("GatewayUsageWriterService", BackgroundWorkGuarantee.Durable, 0, "global database staging", "retry until usage commit"),
        new("MemoryExtractionDispatcherService", BackgroundWorkGuarantee.Durable, 0, "leased database jobs", "lease recovery and bounded retry"),
        new("ExecutionReconciliationService", BackgroundWorkGuarantee.Durable, 0, "database reconciliation scan", "repairs or quarantines non-terminal execution on startup"),
        new("RuntimeEventPublisher", BackgroundWorkGuarantee.Durable, 0, "leased runtime outbox", "startup discovery, retry, and dead-letter state"),
        new("AutopilotRunLaunchTask", BackgroundWorkGuarantee.Durable, 0, "durable autopilot run before launch", "execution startup reconciliation"),
        new("AgentMemoryEmbeddingDispatcherService", BackgroundWorkGuarantee.RecoverableDerived, 128, "reject when full", "hourly authoritative reconciliation"),
        new("AgentMemoryEmbeddingReconciliationService", BackgroundWorkGuarantee.RecoverableDerived, 0, "periodic scan", "rebuild from memory facts"),
        new("ChannelHostOwnerTargetSyncDispatcherService", BackgroundWorkGuarantee.RecoverableDerived, 256, "reject when full", "five-minute authoritative reconciliation"),
        new("ChannelHostOwnerTargetReconciliationService", BackgroundWorkGuarantee.RecoverableDerived, 0, "periodic scan", "rebuild from owned agents"),
        new("WorkspaceDesiredStateStartupService", BackgroundWorkGuarantee.RecoverableDerived, 0, "single startup pass", "repeat on restart"),
        new("BuiltInMcpPackageBootstrapService", BackgroundWorkGuarantee.RecoverableDerived, 0, "single startup pass", "idempotent reinstall on restart"),
        new("BuiltInSkillPackageBootstrapService", BackgroundWorkGuarantee.RecoverableDerived, 0, "single startup pass", "idempotent reinstall on restart"),
        new("BuiltInConnectorCatalogService", BackgroundWorkGuarantee.RecoverableDerived, 0, "single startup pass", "idempotent reinstall on restart"),
        new("ChannelApprovalRecoveryService", BackgroundWorkGuarantee.RecoverableDerived, 0, "periodic database scan", "rebuild channel notifications from pending approvals"),
        new("HitlExpirySweeperService", BackgroundWorkGuarantee.RecoverableDerived, 0, "periodic database scan", "repeat on interval and restart until due approvals expire"),
        new("ConversationAssetRecoveryService", BackgroundWorkGuarantee.RecoverableDerived, 0, "single startup staging scan", "repeat on restart until orphan cleanup completes"),
        new("SessionRetentionDispatcherService", BackgroundWorkGuarantee.BestEffort, 256, "reject when full", "next session activity or retention sweep"),
        new("GatewayLogLevelStartupTask", BackgroundWorkGuarantee.BestEffort, 1, "single startup attempt", "persisted setting is retried on next restart"),
        new("AgentChatStreamingBridge", BackgroundWorkGuarantee.BestEffort, 0, "request-scoped stream", "request cancellation terminates non-durable transport projection"),
        new("WorkflowRecoveryHostedService", BackgroundWorkGuarantee.Durable, 0, "database instance scan", "startup reconciliation re-enqueues recoverable workflow instances"),
    ];
}
