using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Stores;

using Mediator;

using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.InProc;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Workflows;

internal interface IWorkflowExecutionService
{
    void Enqueue(string instanceId);

    Task ContinueAsync(string instanceId, string decision, CancellationToken cancellationToken, string? waitId = null);

    void Cancel(string instanceId);
}

internal sealed class WorkflowExecutionService(
    IGatewayWorkflowStore workflowStore,
    IChatGatewayService chatGatewayService,
    IWorkflowNaradPublisher naradPublisher,
    IRuntimeEventHistoryReader historyReader,
    IRuntimeEventStreamHub runtimeEventStreamHub,
    IGatewayApprovalStore approvalStore,
    IMediator mediator,
    ILogger<WorkflowExecutionService> logger) : IWorkflowExecutionService
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> runs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> executing = new(StringComparer.Ordinal);

    public void Enqueue(string instanceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        GatewayWorkflowInstance? instance = workflowStore.GetInstance(instanceId);
        bool resume = instance is not null
            && !WorkflowInstanceStatuses.IsTerminal(instance.Status)
            && !string.IsNullOrWhiteSpace(workflowStore.GetMafCheckpointJson(instanceId));
        ScheduleExecute(instanceId, resume, retryNodeId: null);
    }

    public Task ContinueAsync(string instanceId, string decision, CancellationToken cancellationToken, string? waitId = null)
    {
        _ = cancellationToken;
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        GatewayWorkflowInstance? instance = workflowStore.GetInstance(instanceId);
        if (instance is null)
        {
            throw new InvalidOperationException("Instance not found.");
        }

        if (IsStopDecision(decision))
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Cancel(instanceId);
            string code = string.Equals(decision, "expired", StringComparison.OrdinalIgnoreCase)
                ? WorkflowFailureCodes.GateExpired
                : WorkflowFailureCodes.WorkflowStopped;
            naradPublisher.Publish(instance, NaradEventTypes.WorkflowCancelled, new JsonObject
            {
                ["code"] = code,
            });
            _ = workflowStore.TrySetInstanceStatus(
                instanceId,
                WorkflowInstanceStatuses.Cancelled,
                instance.CurrentNodeId,
                code,
                string.Equals(code, WorkflowFailureCodes.GateExpired, StringComparison.Ordinal)
                    ? "Review expired."
                    : "Review stopped.",
                stopRequested: true,
                now,
                out _);
            return Task.CompletedTask;
        }

        if (string.Equals(decision, "request_changes", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryAllocateNextAttempt(
                    instance,
                    allowExternal: true,
                    WorkflowFailureCodes.GateRejectedExhausted,
                    out string? retryNodeId,
                    out string? exhaustedCode))
            {
                // A stale request changes action must not turn a paused run into a cancelled run.
                return Task.CompletedTask;
            }

            if (!string.IsNullOrWhiteSpace(waitId))
            {
                naradPublisher.Publish(instance, NaradEventTypes.WorkflowResumed, new JsonObject
                {
                    ["waitId"] = waitId,
                });
            }

            ScheduleExecute(instanceId, resume: false, retryNodeId);
            return Task.CompletedTask;
        }

        if (!string.IsNullOrWhiteSpace(waitId))
        {
            naradPublisher.Publish(instance, NaradEventTypes.WorkflowResumed, new JsonObject
            {
                ["waitId"] = waitId,
            });
        }

        ScheduleExecute(instanceId, resume: true, retryNodeId: null);
        return Task.CompletedTask;
    }

    private void ScheduleExecute(string instanceId, bool resume, string? retryNodeId)
        => _ = Task.Run(() => ExecuteSafeAsync(instanceId, resume, retryNodeId, CancellationToken.None));

    public void Cancel(string instanceId)
    {
        if (runs.TryRemove(instanceId, out CancellationTokenSource? source))
        {
            source.Cancel();
            source.Dispose();
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "MAF and step failures must become instance error state instead of an unobserved background crash.")]
    private async Task ExecuteSafeAsync(string instanceId, bool resume, string? retryNodeId, CancellationToken cancellationToken)
    {
        if (!executing.TryAdd(instanceId, 0))
        {
            return;
        }

        CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        runs[instanceId] = linked;
        string? retryAfterUnlock = null;
        try
        {
            await ExecuteAsync(instanceId, resume, retryNodeId, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            GatewayWorkflowInstance? instance = workflowStore.GetInstance(instanceId);
            if (instance is not null && !WorkflowInstanceStatuses.IsTerminal(instance.Status))
            {
                naradPublisher.Publish(instance, NaradEventTypes.WorkflowCancelled, new JsonObject
                {
                    ["code"] = WorkflowFailureCodes.WorkflowStopped,
                });
                _ = workflowStore.TrySetInstanceStatus(
                    instanceId,
                    WorkflowInstanceStatuses.Cancelled,
                    instance.CurrentNodeId,
                    WorkflowFailureCodes.WorkflowStopped,
                    "Stopped.",
                    stopRequested: true,
                    DateTimeOffset.UtcNow,
                    out _);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (TryBeginStructuredOutputRetry(instanceId, ex, out string? nextNodeId))
            {
                retryAfterUnlock = nextNodeId;
            }
            else
            {
                WorkflowExecutionLog.RunFailed(logger, ex, instanceId);
                GatewayWorkflowInstance? instance = workflowStore.GetInstance(instanceId);
                if (instance is not null && !WorkflowInstanceStatuses.IsTerminal(instance.Status))
                {
                    string code = FailureCodeFor(ex, instance);
                    naradPublisher.Publish(instance, NaradEventTypes.WorkflowError, new JsonObject
                    {
                        ["code"] = code,
                        ["message"] = ex.Message,
                    });
                    _ = workflowStore.TrySetInstanceStatus(
                        instanceId,
                        WorkflowInstanceStatuses.Error,
                        instance.CurrentNodeId,
                        code,
                        ex.Message,
                        stopRequested: null,
                        DateTimeOffset.UtcNow,
                        out _);
                }
            }
        }
        finally
        {
            if (runs.TryRemove(instanceId, out CancellationTokenSource? source))
            {
                source.Dispose();
            }

            executing.TryRemove(instanceId, out _);
        }

        if (!string.IsNullOrWhiteSpace(retryAfterUnlock))
        {
            ScheduleExecute(instanceId, resume: false, retryAfterUnlock);
        }
    }

    private async Task ExecuteAsync(string instanceId, bool resume, string? retryNodeId, CancellationToken cancellationToken)
    {
        GatewayWorkflowInstance instance = workflowStore.GetInstance(instanceId)
            ?? throw new InvalidOperationException($"Workflow instance '{instanceId}' was not found.");
        if (WorkflowInstanceStatuses.IsTerminal(instance.Status) && !resume)
        {
            return;
        }

        var checkpointStore = new WorkflowInstanceJsonCheckpointStore(
            () => workflowStore.GetMafCheckpointJson(instanceId),
            json => _ = workflowStore.TrySaveMafCheckpointJson(instanceId, json, DateTimeOffset.UtcNow));
        CheckpointManager manager = CheckpointManager.CreateJson(checkpointStore);
        InProcessExecutionEnvironment environment = InProcessExecution.Default.WithCheckpointing(manager);
        Workflow workflow = WorkflowMafGraphFactory.Build(
            instance,
            workflowStore,
            approvalStore,
            chatGatewayService,
            naradPublisher,
            historyReader,
            runtimeEventStreamHub,
            retryNodeId);

        if (!resume && string.IsNullOrWhiteSpace(retryNodeId))
        {
            naradPublisher.Publish(instance, NaradEventTypes.WorkflowStarted, new JsonObject
            {
                ["definitionId"] = instance.WorkflowId,
            });
        }
        else
        {
            // A pause closes with one workflow.resumed, published with its waitId from ContinueAsync.
            _ = workflowStore.TrySetInstanceStatus(
                instanceId,
                WorkflowInstanceStatuses.Running,
                instance.CurrentNodeId,
                failureCode: null,
                error: null,
                stopRequested: null,
                DateTimeOffset.UtcNow,
                out _);
        }

        StreamingRun run;
        if (resume)
        {
            IReadOnlyList<CheckpointInfo> index = [.. await checkpointStore.RetrieveIndexAsync(instanceId).ConfigureAwait(false)];
            if (index.Count == 0)
            {
                throw new InvalidOperationException("Cannot continue; the run has no checkpoint.");
            }

            run = await environment.ResumeStreamingAsync(workflow, index[^1], cancellationToken).ConfigureAwait(false);
        }
        else
        {
            run = await environment.RunStreamingAsync(workflow, instance.Envelope.ToJsonString(), instanceId, cancellationToken).ConfigureAwait(false);
        }

        await using (run.ConfigureAwait(false))
        {
            await DrainAsync(instanceId, run, replyToPending: resume, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task DrainAsync(string instanceId, StreamingRun run, bool replyToPending, CancellationToken cancellationToken)
    {
        ExternalRequest? pending = null;
        string? output = null;
        await foreach (WorkflowEvent evt in run.WatchStreamAsync(blockOnPendingRequest: false, cancellationToken).ConfigureAwait(false))
        {
            if (evt is RequestInfoEvent request)
            {
                pending = request.Request;
            }

            if (evt is WorkflowOutputEvent yielded && yielded.Is(out string? text))
            {
                output = text;
            }

            if (evt is WorkflowErrorEvent error && error.Exception is not null)
            {
                throw error.Exception;
            }

            if (evt is ExecutorFailedEvent failed && failed.Data is Exception failure)
            {
                throw failure;
            }
        }

        GatewayWorkflowInstance instance = workflowStore.GetInstance(instanceId)
            ?? throw new InvalidOperationException($"Workflow instance '{instanceId}' was not found.");

        if (pending is not null)
        {
            string? pendingEnvelope = pending.TryGetDataAs(out string? envelopeJson) ? envelopeJson : null;
            if (replyToPending)
            {
                if (!string.IsNullOrWhiteSpace(pendingEnvelope))
                {
                    JsonObject envelope = JsonNode.Parse(pendingEnvelope)!.AsObject();
                    if (workflowStore.TryUpdateEnvelope(instanceId, envelope, DateTimeOffset.UtcNow, out GatewayWorkflowInstance? updated) && updated is not null)
                    {
                        instance = updated;
                    }
                }

                await run.SendResponseAsync(pending.CreateResponse(pendingEnvelope ?? instance.Envelope.ToJsonString())).ConfigureAwait(false);
                await DrainAsync(instanceId, run, replyToPending: false, cancellationToken).ConfigureAwait(false);
                return;
            }

            _ = workflowStore.TrySetInstanceStatus(
                instanceId,
                WorkflowInstanceStatuses.Paused,
                instance.CurrentNodeId,
                failureCode: null,
                error: null,
                stopRequested: null,
                DateTimeOffset.UtcNow,
                out _);
            await CreateWorkflowReviewAsync(instance, pendingEnvelope).ConfigureAwait(false);
            return;
        }

        if (!string.IsNullOrWhiteSpace(output))
        {
            JsonObject envelope = JsonNode.Parse(output)!.AsObject();
            if (workflowStore.TryUpdateEnvelope(instanceId, envelope, DateTimeOffset.UtcNow, out GatewayWorkflowInstance? updated) && updated is not null)
            {
                instance = updated;
            }
        }

        naradPublisher.Publish(instance, NaradEventTypes.WorkflowFinished, new JsonObject());
        _ = workflowStore.TrySetInstanceStatus(
            instanceId,
            WorkflowInstanceStatuses.Finished,
            instance.CurrentNodeId,
            failureCode: null,
            error: null,
            stopRequested: null,
            DateTimeOffset.UtcNow,
            out _);
    }

    private bool TryBeginStructuredOutputRetry(string instanceId, Exception exception, out string? retryNodeId)
    {
        retryNodeId = null;
        if (FindSchemaException(exception) is null)
        {
            return false;
        }

        GatewayWorkflowInstance? instance = workflowStore.GetInstance(instanceId);
        return instance is not null
            && TryAllocateNextAttempt(
                instance,
                allowExternal: false,
                WorkflowFailureCodes.StepBudgetExceeded,
                out retryNodeId,
                out _);
    }

    private bool TryAllocateNextAttempt(
        GatewayWorkflowInstance instance,
        bool allowExternal,
        string exhaustedWhenOverBudget,
        out string? retryNodeId,
        out string? exhaustedCode)
    {
        retryNodeId = instance.CurrentNodeId;
        exhaustedCode = null;
        if (string.IsNullOrWhiteSpace(retryNodeId))
        {
            exhaustedCode = WorkflowFailureCodes.ReviewRejected;
            return false;
        }

        string nodeId = retryNodeId;
        GatewayWorkflowNode? node = instance.Snapshot.Nodes.FirstOrDefault(
            candidate => string.Equals(candidate.NodeId, nodeId, StringComparison.Ordinal));
        if (node is null)
        {
            exhaustedCode = WorkflowFailureCodes.ReviewRejected;
            return false;
        }

        GatewayWorkflowStepAttempt[] nodeAttempts = instance.Steps
            .Where(step => string.Equals(step.NodeId, node.NodeId, StringComparison.Ordinal))
            .OrderBy(step => step.Attempt)
            .ToArray();
        GatewayWorkflowStepAttempt? latest = nodeAttempts.LastOrDefault();
        int currentAttempt = latest?.Attempt ?? 0;
        if (allowExternal)
        {
            int finishedAttempts = nodeAttempts.Count(step =>
                string.Equals(step.Status, WorkflowStepAttemptStatuses.Finished, StringComparison.Ordinal));
            if (!WorkflowRetryPolicy.CanRequestChanges(finishedAttempts))
            {
                exhaustedCode = WorkflowFailureCodes.GateChangesExhausted;
                return false;
            }
        }
        else
        {
            int failedSinceLastFinish = WorkflowRetryPolicy.CountFailuresSinceLastFinish(nodeAttempts);
            if (failedSinceLastFinish == 0
                || !WorkflowRetryPolicy.CanRetryStructuredOutput(node.SideEffects, failedSinceLastFinish, node.MaxRetries))
            {
                exhaustedCode = WorkflowDefinitionCodec.ParseSideEffects(node.SideEffects) == WorkflowSideEffects.External
                    ? WorkflowFailureCodes.StructuredOutputInvalid
                    : exhaustedWhenOverBudget;
                return false;
            }
        }

        int nextAttempt = currentAttempt + 1;

        string now = DateTimeOffset.UtcNow.ToString("O");
        var attempt = new GatewayWorkflowStepAttempt(
            Id: $"wattempt-{Guid.NewGuid():N}",
            InstanceId: instance.Id,
            NodeId: node.NodeId,
            Attempt: nextAttempt,
            RunId: $"wrun-{Guid.NewGuid():N}",
            SessionId: null,
            Status: WorkflowStepAttemptStatuses.Pending,
            FailureCode: null,
            StartedAt: null,
            EndedAt: null,
            CreatedAt: now);
        if (!workflowStore.TryAddStepAttempt(attempt))
        {
            exhaustedCode = WorkflowFailureCodes.ReviewRejected;
            return false;
        }

        _ = workflowStore.TryClearMafCheckpoint(instance.Id, DateTimeOffset.UtcNow);
        return true;
    }

    private static string FailureCodeFor(Exception exception, GatewayWorkflowInstance instance)
    {
        if (FindSchemaException(exception) is null)
        {
            return WorkflowFailureCodes.StepRunError;
        }

        GatewayWorkflowNode? node = instance.Snapshot.Nodes.FirstOrDefault(
            candidate => string.Equals(candidate.NodeId, instance.CurrentNodeId, StringComparison.Ordinal));
        if (node is not null
            && WorkflowDefinitionCodec.ParseSideEffects(node.SideEffects) == WorkflowSideEffects.External)
        {
            return WorkflowFailureCodes.StructuredOutputInvalid;
        }

        return WorkflowFailureCodes.StepBudgetExceeded;
    }

    private static WorkflowSchemaException? FindSchemaException(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is WorkflowSchemaException schema)
            {
                return schema;
            }
        }

        return null;
    }

    private async Task CreateWorkflowReviewAsync(GatewayWorkflowInstance instance, string? proposedEnvelope)
    {
        GatewayWorkflowNode? node = instance.Snapshot.Nodes.FirstOrDefault(
            candidate => string.Equals(candidate.NodeId, instance.CurrentNodeId, StringComparison.Ordinal));
        GatewayWorkflowStepAttempt? attempt = instance.Steps
            .Where(step => string.Equals(step.NodeId, instance.CurrentNodeId, StringComparison.Ordinal))
            .OrderBy(step => step.Attempt)
            .LastOrDefault();
        string requestId = $"wrev-{Guid.NewGuid():N}";
        string now = DateTimeOffset.UtcNow.ToString("O");
        var arguments = new JsonObject
        {
            ["kind"] = "workflow_review",
            ["workflowId"] = instance.WorkflowId,
            ["instanceId"] = instance.Id,
            ["nodeId"] = instance.CurrentNodeId,
            ["envelope"] = JsonNode.Parse(instance.Envelope.ToJsonString()),
            ["proposed"] = string.IsNullOrWhiteSpace(proposedEnvelope)
                ? JsonNode.Parse(instance.Envelope.ToJsonString())
                : JsonNode.Parse(proposedEnvelope),
        };
        var request = new GatewayToolApprovalRequest(
            Id: requestId,
            SessionId: attempt?.SessionId ?? instance.Id,
            RunId: attempt?.RunId,
            CheckpointId: null,
            AgentId: node?.AgentId,
            UserId: instance.OwnerUserId,
            ToolCallId: $"{instance.Id}:{instance.CurrentNodeId}:{attempt?.Attempt ?? 1}",
            ToolName: "workflow_review",
            ToolType: "workflow_review",
            McpServerId: null,
            ArgumentsJson: arguments.ToJsonString(),
            RequestedAt: now,
            Decision: null,
            DecidedBy: null,
            DecidedAt: null,
            Reason: null,
            ExpiresAt: DateTimeOffset.UtcNow.AddHours(72).ToString("O"),
            Kind: "workflow_review",
            WorkflowId: instance.WorkflowId,
            InstanceId: instance.Id,
            NodeId: instance.CurrentNodeId);
        approvalStore.CreateRequest(request);
        string waitId = NaradEventBodies.ApprovalWaitId(request.Id);
        int finishedAttempts = instance.Steps.Count(step =>
            string.Equals(step.NodeId, instance.CurrentNodeId, StringComparison.Ordinal)
            && string.Equals(step.Status, WorkflowStepAttemptStatuses.Finished, StringComparison.Ordinal));
        string[] actions = ApprovalActionCatalog.ForToolType("workflow_review")
            .Where(action => action.Value != ApprovalActionCatalog.RequestChanges
                || WorkflowRetryPolicy.CanRequestChanges(finishedAttempts))
            .Select(static action => action.Value)
            .ToArray();
        naradPublisher.Publish(
            instance,
            NaradEventTypes.InterruptRequested,
            JsonNode.Parse(
                NaradEventBodies.WithOwnerUserId(
                    NaradEventBodies.InterruptRequested(
                        request.Id,
                        waitId,
                        "workflow_review",
                        actions,
                        request.ToolCallId,
                        scope: "workflow"),
                    instance.OwnerUserId).GetRawText())!.AsObject());
        naradPublisher.Publish(instance, NaradEventTypes.WorkflowPaused, new JsonObject
        {
            ["nodeId"] = instance.CurrentNodeId,
            ["waitId"] = waitId,
            ["interruptId"] = request.Id,
            ["userId"] = instance.OwnerUserId,
        });
        await mediator.Publish(new HitlApprovalRequested(
            RequestId: request.Id,
            ToolCallId: request.ToolCallId,
            ToolName: request.ToolName,
            ArgumentsJson: request.ArgumentsJson,
            SessionId: request.SessionId ?? instance.Id,
            TargetType: "agent",
            TargetId: node?.AgentId ?? string.Empty,
            UserId: instance.OwnerUserId,
            RunId: request.RunId,
            ToolType: "workflow_review")).ConfigureAwait(false);
    }

    private static bool IsStopDecision(string decision)
        => string.Equals(decision, "stop", StringComparison.OrdinalIgnoreCase)
           || string.Equals(decision, "stop_run", StringComparison.OrdinalIgnoreCase)
           || string.Equals(decision, "expired", StringComparison.OrdinalIgnoreCase)
           || string.Equals(decision, "reject", StringComparison.OrdinalIgnoreCase);
}

internal static partial class WorkflowExecutionLog
{
    [LoggerMessage(EventId = 7201, Level = LogLevel.Error, Message = "Workflow instance {InstanceId} failed.")]
    public static partial void RunFailed(ILogger logger, Exception exception, string instanceId);
}
