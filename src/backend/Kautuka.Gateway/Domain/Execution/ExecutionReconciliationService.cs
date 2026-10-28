using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Autopilot;
using Kautuka.Gateway.Domain.Stores;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Execution;

internal sealed class ExecutionReconciliationService(
    IAgentToolInvocationStore invocationStore,
    IAgentToolInvocationLinkStore invocationLinkStore,
    IRunWaitStateStore waitStateStore,
    IGatewayApprovalStore approvalStore,
    IExecutionQuarantineStore quarantineStore,
    IAgentDefinitionStore agentDefinitionStore,
    IRuntimeEventHistoryReader runtimeEventHistoryReader,
    IRuntimeEventPublisher runtimeEventPublisher,
    ILogger<ExecutionReconciliationService> logger) : BackgroundService
{
    private static readonly TimeSpan interval = TimeSpan.FromSeconds(15);
    private readonly string startupCutoff = DateTimeOffset.UtcNow.ToString("O");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await ReconcileAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A failed reconciliation pass is isolated; the hosted loop retries and preserves durable state.
            catch (Exception exception)
            {
                ExecutionReconciliationLog.Failed(logger, exception);
            }
#pragma warning restore CA1031
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    internal async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<RunWaitState> recoverableWaits = waitStateStore.ListRecoverable();
        IReadOnlyList<AgentToolInvocation> recoverableInvocations = invocationStore.ListRecoverable();
        var protectedRootRunIds = recoverableWaits
            .Where(IsDurablyParked)
            .Select(static wait => wait.RootRunId)
            .Where(static rootRunId => !string.IsNullOrWhiteSpace(rootRunId))
            .ToHashSet(StringComparer.Ordinal);
        var expiredInterruptHandledRoots = new HashSet<string>(StringComparer.Ordinal);

        foreach (RunWaitState wait in recoverableWaits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.CompareOrdinal(wait.PausedAt, startupCutoff) >= 0
                || !string.Equals(wait.WaitReason, RunWaitReasons.Approval, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(wait.RootRunId))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(wait.CorrelationId)
                && approvalStore.GetById(wait.CorrelationId) is not null)
            {
                continue;
            }

            IReadOnlyList<NaradPersistedEvent> history = ReadReconciliationRootHistory(wait.RootRunId);
            if (HasLiveContinuationLease(history)) continue;
            NaradRunOpenState openState = NaradRunHistoryState.Analyze(history);
            bool hasMatchingOpenInterrupt = openState.OpenInterrupts.Any(interrupt =>
                string.Equals(interrupt.InterruptId, wait.CorrelationId, StringComparison.Ordinal)
                || string.Equals(interrupt.WaitId, wait.WaitId, StringComparison.Ordinal));
            if (!hasMatchingOpenInterrupt)
            {
                continue;
            }

            await ResolveExpiredOpenExecutionAsync(
                wait.RootRunId,
                history,
                openState,
                recoverableInvocations,
                cancellationToken).ConfigureAwait(false);
            expiredInterruptHandledRoots.Add(wait.RootRunId);
            waitStateStore.MarkFailed(wait.WaitId, "gateway_process_lost");
            quarantineStore.Resolve("run_wait", wait.WaitId);
        }

        foreach (string rootRunId in agentDefinitionStore.ListInterruptedNaradRootRuns(startupCutoff))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (protectedRootRunIds.Contains(rootRunId)
                || expiredInterruptHandledRoots.Contains(rootRunId))
            {
                continue;
            }

            if (!ShouldFailInterruptedRoot(rootRunId, recoverableInvocations, recoverableWaits))
            {
                continue;
            }

            await FailInterruptedExecutionAsync(rootRunId, recoverableInvocations, recoverableWaits, cancellationToken).ConfigureAwait(false);
        }

        var existing = quarantineStore.ListActive()
            .Where(static item => !string.IsNullOrWhiteSpace(item.AuditPublishedAt))
            .Select(static item => $"{item.EntityType}\u001f{item.EntityId}")
            .ToHashSet(StringComparer.Ordinal);

        foreach (AgentToolInvocation invocation in invocationStore.ListRecoverable())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.CompareOrdinal(invocation.CreatedAt, startupCutoff) >= 0
                || expiredInterruptHandledRoots.Contains(invocation.RootRunId))
            {
                continue;
            }

            if (TryFinalizeInvocationAfterParentTerminal(invocation))
            {
                quarantineStore.Resolve("aaat_invocation", invocation.InvocationId);
                continue;
            }

            AgentToolInvocationLink? link = invocationLinkStore.GetByChildSessionId(invocation.ChildSessionId);
            RunWaitState? parentWait = waitStateStore.GetActiveChildPauseForCaller(
                invocation.ParentSessionId,
                invocation.ParentToolCallId);
            string? reason = link is null || !string.Equals(link.InvocationId, invocation.InvocationId, StringComparison.Ordinal)
                ? "approval_link_missing"
                : parentWait is null
                    ? "parent_wait_missing"
                    : approvalStore.GetById(link.ApprovalRequestId) is null
                        ? "approval_record_missing"
                        : null;
            if (reason is null)
            {
                quarantineStore.Resolve("aaat_invocation", invocation.InvocationId);
                continue;
            }

            ExecutionQuarantineItem item = quarantineStore.Quarantine(
                "aaat_invocation",
                invocation.InvocationId,
                reason,
                "A non-terminal agent-tool invocation cannot be resumed from its durable execution lineage.");
            await PublishOnceAsync(existing, item, invocation, cancellationToken).ConfigureAwait(false);
        }

        foreach (RunWaitState wait in recoverableWaits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.CompareOrdinal(wait.PausedAt, startupCutoff) >= 0
                || (!string.IsNullOrWhiteSpace(wait.RootRunId)
                    && expiredInterruptHandledRoots.Contains(wait.RootRunId)))
            {
                continue;
            }

            bool valid = wait.WaitReason switch
            {
                RunWaitReasons.Approval => !string.IsNullOrWhiteSpace(wait.CorrelationId)
                    && approvalStore.GetById(wait.CorrelationId) is not null,
                RunWaitReasons.Child => !string.IsNullOrWhiteSpace(wait.ChildSessionId)
                    && invocationLinkStore.GetByChildSessionId(wait.ChildSessionId) is not null,
                _ => true,
            };
            if (valid)
            {
                quarantineStore.Resolve("run_wait", wait.WaitId);
                continue;
            }

            ExecutionQuarantineItem item = quarantineStore.Quarantine(
                "run_wait",
                wait.WaitId,
                "wait_correlation_missing",
                "A recoverable wait has no exact durable approval or child invocation correlation.");
            await PublishOnceAsync(existing, item, wait, cancellationToken).ConfigureAwait(false);
        }

        foreach (GatewayToolApprovalRequest approval in approvalStore.ListPending(userId: null, adminAll: true))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.CompareOrdinal(approval.RequestedAt, startupCutoff) >= 0)
            {
                continue;
            }

            bool valid = PackedToolApproval.FindActiveWait(waitStateStore, approval.Id, continuationRunId: null) is not null
                || string.Equals(approval.ToolType, AutopilotLaunchPolicy.PreRunToolType, StringComparison.Ordinal);
            if (valid)
            {
                quarantineStore.Resolve("approval", approval.Id);
                continue;
            }

            ExecutionQuarantineItem item = quarantineStore.Quarantine(
                "approval",
                approval.Id,
                "approval_wait_missing",
                "A pending approval has no exact durable wait state.");
            await PublishOnceAsync(existing, item, approval, cancellationToken).ConfigureAwait(false);
        }

        foreach (ExecutionQuarantineItem item in quarantineStore.ListActive()
            .Where(static item => string.IsNullOrWhiteSpace(item.AuditPublishedAt)))
        {
            if (!existing.Add($"{item.EntityType}\u001f{item.EntityId}"))
            {
                continue;
            }

            await PublishAsync(item, "system", "system", item.EntityId, item.EntityId, cancellationToken).ConfigureAwait(false);
        }
    }

    private IReadOnlyList<NaradPersistedEvent> ReadRecoveryRootHistory(string rootRunId)
    {
        string? storageLocatorKey = agentDefinitionStore.FindNaradRootStorageLocatorKey(rootRunId);
        return storageLocatorKey is null
            ? []
            : agentDefinitionStore.ListNaradRootHistoryFull(storageLocatorKey, rootRunId);
    }

    private IReadOnlyList<NaradPersistedEvent> ReadReconciliationRootHistory(string rootRunId)
    {
        IReadOnlyList<NaradPersistedEvent> storeHistory = ReadRecoveryRootHistory(rootRunId);
        return storeHistory.Count > 0
            ? storeHistory
            : runtimeEventHistoryReader.ReadRootHistory(rootRunId);
    }

    private bool ShouldFailInterruptedRoot(
        string rootRunId,
        IReadOnlyList<AgentToolInvocation> recoverableInvocations,
        IReadOnlyList<RunWaitState> recoverableWaits)
    {
        IReadOnlyList<NaradPersistedEvent> rootHistory = ReadRecoveryRootHistory(rootRunId);
        if (HasLiveContinuationLease(rootHistory)) return false;
        NaradPersistedEvent? runStarted = rootHistory.FirstOrDefault(runtimeEvent =>
            string.Equals(runtimeEvent.EventType, NaradEventTypes.RunStarted, StringComparison.Ordinal)
            && string.Equals(runtimeEvent.RunId, rootRunId, StringComparison.Ordinal));
        if (runStarted is null)
        {
            return false;
        }

        if (string.CompareOrdinal(runStarted.OccurredAt, startupCutoff) <= 0)
        {
            // waiting_on_child without a parked durable wait is process-local interruption, not recovery.
            bool hasDurableRecovery = recoverableWaits.Any(wait =>
                string.Equals(wait.RootRunId, rootRunId, StringComparison.Ordinal)
                && IsDurablyParked(wait));
            return !hasDurableRecovery;
        }

        return recoverableInvocations.Any(invocation =>
                string.Equals(invocation.RootRunId, rootRunId, StringComparison.Ordinal))
            || recoverableWaits.Any(wait =>
                string.Equals(wait.RootRunId, rootRunId, StringComparison.Ordinal));
    }

    private bool TryFinalizeInvocationAfterParentTerminal(AgentToolInvocation invocation)
    {
        if (string.IsNullOrWhiteSpace(invocation.ChildOutcome)
            || invocation.State is not (AgentToolInvocationStates.CompletingParent or AgentToolInvocationStates.ParentCompletionPending))
        {
            return false;
        }

        IReadOnlyList<NaradPersistedEvent> history = runtimeEventHistoryReader.ReadRootHistory(invocation.RootRunId);
        if (!AaatParentCompletionAuthority.HasAuthoritativeParentCompletion(
                history,
                invocation.RootRunId,
                invocation.ParentToolCallId))
        {
            return false;
        }

        if (string.Equals(invocation.ChildOutcome, "completed", StringComparison.Ordinal))
        {
            return invocationStore.TrySealCompleted(invocation.InvocationId);
        }

        invocationStore.MarkFailed(invocation.InvocationId, invocation.ChildOutcome);
        return true;
    }

    private bool IsDurablyParked(RunWaitState wait)
    {
        if (string.Equals(wait.WaitReason, RunWaitReasons.ExternalInput, StringComparison.Ordinal))
        {
            return true;
        }

        string? approvalRequestId = wait.WaitReason switch
        {
            RunWaitReasons.Approval => wait.CorrelationId,
            RunWaitReasons.Child when !string.IsNullOrWhiteSpace(wait.ChildSessionId)
                => invocationLinkStore.GetByChildSessionId(wait.ChildSessionId)?.ApprovalRequestId,
            _ => null,
        };
        GatewayToolApprovalRequest? approval = !string.IsNullOrWhiteSpace(approvalRequestId)
            ? approvalStore.GetById(approvalRequestId)
            : null;
        return approval is not null
            && (string.IsNullOrWhiteSpace(approval.Decision)
                || !string.Equals(approval.ResolutionSource, "chat", StringComparison.Ordinal));
    }

    private bool HasLiveContinuationLease(IReadOnlyList<NaradPersistedEvent> history)
    {
        // ListRecoverable deliberately excludes unexpired execution leases. Their absence
        // from the recovery candidates is not evidence that a resumed run was abandoned.
        string now = DateTimeOffset.UtcNow.ToString("O");
        foreach (NaradPersistedEvent item in history.Where(static item => item.EventType == NaradEventTypes.InterruptRequested))
        {
            string? interruptId = NaradPersistedEventReader.String(NaradPersistedEventReader.ParseBody(item), "interruptId");
            if (string.IsNullOrWhiteSpace(interruptId)) continue;
            RunWaitState? wait = waitStateStore.GetActiveByCorrelationId(interruptId);
            if (wait is { Status: RunWaitStatuses.Executing, LeaseOwner.Length: > 0, LeaseExpiresAt.Length: > 0 }
                && string.Equals(wait.RootRunId ?? wait.RunId, item.RootRunId, StringComparison.Ordinal)
                && string.CompareOrdinal(wait.LeaseExpiresAt, now) > 0) return true;
        }
        return false;
    }

    private async Task FailInterruptedExecutionAsync(
        string rootRunId,
        IReadOnlyList<AgentToolInvocation> recoverableInvocations,
        IReadOnlyList<RunWaitState> recoverableWaits,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<NaradPersistedEvent> rootHistory = ReadRecoveryRootHistory(rootRunId);
        NaradPersistedEvent? runStarted = rootHistory.FirstOrDefault(runtimeEvent =>
            string.Equals(runtimeEvent.EventType, NaradEventTypes.RunStarted, StringComparison.Ordinal)
            && string.Equals(runtimeEvent.RunId, rootRunId, StringComparison.Ordinal));
        if (runStarted is null)
        {
            return;
        }

        if (rootHistory.Any(item => string.Equals(item.EventId, $"recovery:{rootRunId}:run-terminal", StringComparison.Ordinal)))
        {
            return;
        }

        NaradEventContext context = BuildRecoveryContext(rootRunId, runStarted);
        NaradPersistedEvent? stopRequest = rootHistory.LastOrDefault(static runtimeEvent =>
            string.Equals(runtimeEvent.EventType, NaradEventTypes.RunStopRequested, StringComparison.Ordinal));
        bool userStopped = stopRequest is not null;
        string interruptionCode = userStopped ? "user_stopped" : "gateway_process_lost";
        string interruptionMessage = userStopped
            ? BuildStoppedMessage(stopRequest!)
            : "This run was interrupted because the Gateway process restarted. Please retry.";
        string terminalMessage = userStopped
            ? "This run was stopped by the user."
            : interruptionMessage;

        foreach (AgentToolInvocation invocation in recoverableInvocations.Where(invocation =>
            string.Equals(invocation.RootRunId, rootRunId, StringComparison.Ordinal)))
        {
            string childTerminalEventId = $"recovery:{rootRunId}:child-terminal:{invocation.ChildRunId}";
            if (!string.IsNullOrWhiteSpace(invocation.ChildRunId)
                && !rootHistory.Any(item => string.Equals(item.EventId, childTerminalEventId, StringComparison.Ordinal)))
            {
                await PublishRecoveryChildTerminalAsync(
                    rootRunId,
                    rootHistory,
                    invocation.ChildRunId,
                    context.UserId,
                    interruptionCode,
                    userStopped,
                    invocation.ChildAgentId,
                    invocation.ChildSessionId,
                    cancellationToken).ConfigureAwait(false);
            }

            string toolTerminalEventId = $"aaat:{invocation.InvocationId}:tool-terminal";
            if (!rootHistory.Any(item => string.Equals(item.EventId, toolTerminalEventId, StringComparison.Ordinal)))
            {
                string toolMessage = userStopped
                    ? "The run was stopped by the user while the agent tool was executing."
                    : "The Gateway process stopped while the agent tool was executing.";
                await runtimeEventPublisher.PublishWithIdAsync(
                    toolTerminalEventId,
                    NaradEventTypes.ToolCallFinished,
                    JsonSerializer.SerializeToElement(new JsonObject
                    {
                        ["toolCallId"] = invocation.ParentToolCallId,
                        ["toolName"] = invocation.ParentToolName,
                        ["outcome"] = "error",
                        ["message"] = toolMessage,
                        ["response"] = toolMessage,
                        ["code"] = interruptionCode,
                    }, KautukaJsonContext.Default.JsonObject),
                    context,
                    cancellationToken).ConfigureAwait(false);
            }

            invocationStore.MarkFailed(invocation.InvocationId, interruptionCode);
        }

        foreach (RunWaitState wait in recoverableWaits.Where(wait =>
            string.Equals(wait.RootRunId, rootRunId, StringComparison.Ordinal)))
        {
            waitStateStore.MarkFailed(wait.WaitId, interruptionCode);
        }

        NaradPersistedEvent? openAssistant = FindOpenAssistantMessage(rootHistory);
        string recoveryMessageId = openAssistant is not null
            ? ReadMessageId(NaradPersistedEventReader.ParseBody(openAssistant))
            : $"recovery-assistant:{rootRunId}";
        if (openAssistant is null)
        {
            await runtimeEventPublisher.PublishWithIdAsync(
                $"recovery:{rootRunId}:assistant-started",
                NaradEventTypes.MessageTextStarted,
                NaradEventBodies.MessageTextStarted(recoveryMessageId),
                context,
                cancellationToken).ConfigureAwait(false);
        }

        await runtimeEventPublisher.PublishWithIdAsync(
            $"recovery:{rootRunId}:assistant-interrupted",
            NaradEventTypes.MessageTextDelta,
            JsonSerializer.SerializeToElement(new JsonObject
            {
                ["messageId"] = recoveryMessageId,
                ["delta"] = interruptionMessage,
                ["isThinking"] = false,
            }, KautukaJsonContext.Default.JsonObject),
            context,
            cancellationToken).ConfigureAwait(false);
        await runtimeEventPublisher.PublishWithIdAsync(
            $"recovery:{rootRunId}:assistant-completed:{openAssistant?.EventId ?? "recovery"}",
            NaradEventTypes.MessageTextCompleted,
            JsonSerializer.SerializeToElement(new JsonObject
            {
                ["messageId"] = recoveryMessageId,
            }, KautukaJsonContext.Default.JsonObject),
            context,
            cancellationToken).ConfigureAwait(false);

        string terminalEventType = userStopped ? NaradEventTypes.RunCancelled : NaradEventTypes.RunError;
        await runtimeEventPublisher.PublishWithIdAsync(
            $"recovery:{rootRunId}:run-terminal",
            terminalEventType,
            JsonSerializer.SerializeToElement(new JsonObject
            {
                ["message"] = terminalMessage,
                ["code"] = interruptionCode,
                ["result"] = userStopped
                    ? JsonSerializer.SerializeToNode(
                        new GatewayChatResult(
                            "agent",
                            context.AgentId,
                            context.SessionId,
                            terminalMessage,
                            false,
                            new GatewayMetrics(0, 0, 0),
                            Run: new GatewayRunState(GatewayRunPhases.Stopped)),
                        KautukaJsonContext.Default.GatewayChatResult)
                    : null,
            }, KautukaJsonContext.Default.JsonObject),
            context,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ResolveExpiredOpenExecutionAsync(
        string rootRunId,
        IReadOnlyList<NaradPersistedEvent> rootHistory,
        NaradRunOpenState openState,
        IReadOnlyList<AgentToolInvocation> recoverableInvocations,
        CancellationToken cancellationToken)
    {
        NaradPersistedEvent? runStarted = rootHistory.FirstOrDefault(runtimeEvent =>
            string.Equals(runtimeEvent.EventType, NaradEventTypes.RunStarted, StringComparison.Ordinal)
            && string.Equals(runtimeEvent.RunId, rootRunId, StringComparison.Ordinal));
        if (runStarted is null)
        {
            return;
        }

        NaradEventContext context = BuildRecoveryContext(rootRunId, runStarted);
        const string interruptionCode = "gateway_process_lost";

        foreach (NaradOpenInterrupt openInterrupt in openState.OpenInterrupts)
        {
            string resolvedEventId = $"recovery:{rootRunId}:interrupt-resolved:{openInterrupt.InterruptId}";
            if (rootHistory.Any(item => string.Equals(item.EventId, resolvedEventId, StringComparison.Ordinal)))
            {
                continue;
            }

            await runtimeEventPublisher.PublishWithIdAsync(
                resolvedEventId,
                NaradEventTypes.InterruptResolved,
                JsonSerializer.SerializeToElement(new JsonObject
                {
                    ["interruptId"] = openInterrupt.InterruptId,
                    ["waitId"] = openInterrupt.WaitId,
                    ["decision"] = "expired",
                }, KautukaJsonContext.Default.JsonObject),
                context,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (string toolCallId in openState.OpenToolCallIds)
        {
            string toolTerminalEventId = $"recovery:{rootRunId}:tool-finished:{toolCallId}";
            if (rootHistory.Any(item => string.Equals(item.EventId, toolTerminalEventId, StringComparison.Ordinal)))
            {
                continue;
            }

            const string toolMessage = "The Gateway process stopped while the agent tool was executing.";
            await runtimeEventPublisher.PublishWithIdAsync(
                toolTerminalEventId,
                NaradEventTypes.ToolCallFinished,
                JsonSerializer.SerializeToElement(new JsonObject
                {
                    ["toolCallId"] = toolCallId,
                    ["outcome"] = "error",
                    ["message"] = toolMessage,
                    ["response"] = toolMessage,
                    ["code"] = interruptionCode,
                }, KautukaJsonContext.Default.JsonObject),
                context,
                cancellationToken).ConfigureAwait(false);
        }

        foreach (string childRunId in openState.OpenChildRunIds)
        {
            await PublishRecoveryChildTerminalAsync(
                rootRunId,
                rootHistory,
                childRunId,
                context.UserId,
                interruptionCode,
                userStopped: false,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        foreach (AgentToolInvocation invocation in recoverableInvocations.Where(invocation =>
            string.Equals(invocation.RootRunId, rootRunId, StringComparison.Ordinal)))
        {
            if (!string.IsNullOrWhiteSpace(invocation.ChildRunId))
            {
                await PublishRecoveryChildTerminalAsync(
                    rootRunId,
                    rootHistory,
                    invocation.ChildRunId,
                    context.UserId,
                    interruptionCode,
                    userStopped: false,
                    invocation.ChildAgentId,
                    invocation.ChildSessionId,
                    cancellationToken).ConfigureAwait(false);
            }

            string toolTerminalEventId = $"aaat:{invocation.InvocationId}:tool-terminal";
            string recoveryToolEventId = $"recovery:{rootRunId}:tool-finished:{invocation.ParentToolCallId}";
            bool toolAlreadyHandled = openState.OpenToolCallIds.Contains(invocation.ParentToolCallId, StringComparer.Ordinal)
                || rootHistory.Any(item =>
                    string.Equals(item.EventId, recoveryToolEventId, StringComparison.Ordinal)
                    || (string.Equals(item.EventType, NaradEventTypes.ToolCallFinished, StringComparison.Ordinal)
                        && string.Equals(
                            NaradPersistedEventReader.String(item, "toolCallId"),
                            invocation.ParentToolCallId,
                            StringComparison.Ordinal)));
            if (!toolAlreadyHandled
                && !rootHistory.Any(item => string.Equals(item.EventId, toolTerminalEventId, StringComparison.Ordinal)))
            {
                const string toolMessage = "The Gateway process stopped while the agent tool was executing.";
                await runtimeEventPublisher.PublishWithIdAsync(
                    toolTerminalEventId,
                    NaradEventTypes.ToolCallFinished,
                    JsonSerializer.SerializeToElement(new JsonObject
                    {
                        ["toolCallId"] = invocation.ParentToolCallId,
                        ["toolName"] = invocation.ParentToolName,
                        ["outcome"] = "error",
                        ["message"] = toolMessage,
                        ["response"] = toolMessage,
                        ["code"] = interruptionCode,
                    }, KautukaJsonContext.Default.JsonObject),
                    context,
                    cancellationToken).ConfigureAwait(false);
            }

            invocationStore.MarkFailed(invocation.InvocationId, interruptionCode);
        }
    }

    private async ValueTask PublishRecoveryChildTerminalAsync(
        string rootRunId,
        IReadOnlyList<NaradPersistedEvent> rootHistory,
        string childRunId,
        string userId,
        string interruptionCode,
        bool userStopped,
        string? childAgentId = null,
        string? childSessionId = null,
        CancellationToken cancellationToken = default)
    {
        string childTerminalEventId = $"recovery:{rootRunId}:child-terminal:{childRunId}";
        if (rootHistory.Any(item => string.Equals(item.EventId, childTerminalEventId, StringComparison.Ordinal)
            || item.RunId == childRunId && NaradEventTypes.IsRootRunTerminalEventType(item.EventType)))
        {
            return;
        }

        var childContext = new NaradEventContext(
            childAgentId ?? "system",
            childSessionId ?? "system",
            userId,
            childRunId,
            rootRunId);
        await runtimeEventPublisher.PublishWithIdAsync(
            childTerminalEventId,
            NaradEventTypes.RunError,
            JsonSerializer.SerializeToElement(new JsonObject
            {
                ["message"] = userStopped
                    ? "The run was stopped by the user while the agent tool was executing."
                    : "The Gateway process stopped while the agent tool was executing.",
                ["code"] = interruptionCode,
            }, KautukaJsonContext.Default.JsonObject),
            childContext,
            cancellationToken).ConfigureAwait(false);
    }

    private NaradEventContext BuildRecoveryContext(string rootRunId, NaradPersistedEvent runStarted, string userId = "system")
    {
        string agentId = agentDefinitionStore.FindNaradRootStorageLocatorKey(rootRunId)
            ?? NaradStreamObservation.ReadString(runStarted, "agentId")
            ?? NaradStreamObservation.ReadBodyString(runStarted, "agentId")
            ?? "system";
        string sessionId = runStarted.SessionId;
        string runId = runStarted.RunId ?? runStarted.RootRunId ?? rootRunId;
        return new NaradEventContext(agentId, sessionId, userId, runId, rootRunId);
    }

    private static string BuildStoppedMessage(NaradPersistedEvent stopRequest)
    {
        JsonElement body = NaradPersistedEventReader.ParseBody(stopRequest);
        string actor = NaradPersistedEventReader.String(body, "actor") ?? "The user";
        string channel = NaradPersistedEventReader.String(body, "channel") ?? "unknown";
        return $"This run was stopped by {actor} via {channel}.";
    }

    private static NaradPersistedEvent? FindOpenAssistantMessage(IReadOnlyList<NaradPersistedEvent> history)
    {
        var completedMessageIds = history
            .Where(static runtimeEvent => string.Equals(runtimeEvent.EventType, NaradEventTypes.MessageTextCompleted, StringComparison.Ordinal))
            .Select(static runtimeEvent => ReadMessageId(NaradPersistedEventReader.ParseBody(runtimeEvent)))
            .Where(static messageId => !string.IsNullOrWhiteSpace(messageId))
            .ToHashSet(StringComparer.Ordinal);
        return history.LastOrDefault(runtimeEvent =>
            string.Equals(runtimeEvent.EventType, NaradEventTypes.MessageTextStarted, StringComparison.Ordinal)
            && !completedMessageIds.Contains(ReadMessageId(NaradPersistedEventReader.ParseBody(runtimeEvent))));
    }

    private static string ReadMessageId(JsonElement payload)
        => payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("messageId", out JsonElement messageId)
            && messageId.ValueKind == JsonValueKind.String
                ? messageId.GetString() ?? string.Empty
                : string.Empty;

    private async ValueTask PublishOnceAsync(
        HashSet<string> existing,
        ExecutionQuarantineItem item,
        AgentToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (!existing.Add($"{item.EntityType}\u001f{item.EntityId}"))
        {
            return;
        }

        await PublishAsync(
            item,
            invocation.ChildAgentId,
            invocation.ParentSessionId,
            invocation.ParentRunId,
            invocation.RootRunId,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask PublishOnceAsync(
        HashSet<string> existing,
        ExecutionQuarantineItem item,
        RunWaitState wait,
        CancellationToken cancellationToken)
    {
        if (!existing.Add($"{item.EntityType}\u001f{item.EntityId}"))
        {
            return;
        }

        await PublishAsync(
            item,
            wait.AgentId ?? wait.TargetId ?? "system",
            wait.SessionId ?? "system",
            wait.RunId ?? wait.ContinuationRunId,
            wait.RootRunId ?? wait.RunId ?? wait.ContinuationRunId,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask PublishOnceAsync(
        HashSet<string> existing,
        ExecutionQuarantineItem item,
        GatewayToolApprovalRequest approval,
        CancellationToken cancellationToken)
    {
        if (!existing.Add($"{item.EntityType}\u001f{item.EntityId}"))
        {
            return;
        }

        string runId = approval.RunId ?? approval.Id;
        await PublishAsync(
            item,
            approval.AgentId ?? "system",
            approval.SessionId ?? "system",
            runId,
            runId,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask PublishAsync(
        ExecutionQuarantineItem item,
        string agentId,
        string sessionId,
        string runId,
        string rootRunId,
        CancellationToken cancellationToken)
    {
        var context = new NaradEventContext(agentId, sessionId, "system", runId, rootRunId);
        await runtimeEventPublisher.PublishAsync(
            NaradAuthorityEventDraftFactory.CreateNew(
                NaradEventTypes.TraceEvent,
                JsonSerializer.SerializeToElement(new JsonObject
                {
                    ["name"] = "execution.quarantined",
                    ["quarantineId"] = item.QuarantineId,
                    ["entityType"] = item.EntityType,
                    ["entityId"] = item.EntityId,
                    ["reasonCode"] = item.ReasonCode,
                    ["diagnostic"] = item.SafeDiagnostic,
                }, KautukaJsonContext.Default.JsonObject),
                context),
            context,
            cancellationToken).ConfigureAwait(false);
        quarantineStore.MarkAuditPublished(item.QuarantineId, DateTimeOffset.UtcNow.ToString("O"));
    }
}

internal static partial class ExecutionReconciliationLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Durable execution reconciliation failed")]
    public static partial void Failed(ILogger logger, Exception exception);
}
