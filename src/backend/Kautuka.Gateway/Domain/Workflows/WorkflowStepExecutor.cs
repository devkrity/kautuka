using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Stores;

using Microsoft.Agents.AI.Workflows;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Workflows;

internal sealed class WorkflowStepExecutor : Executor<string, string>
{
    private readonly string instanceId;
    private readonly GatewayWorkflowNode node;
    private readonly GatewayWorkflowStepAttempt attempt;
    private readonly IGatewayWorkflowStore workflowStore;
    private readonly IGatewayApprovalStore approvalStore;
    private readonly IChatGatewayService chatGatewayService;
    private readonly IRuntimeEventHistoryReader historyReader;
    private readonly IRuntimeEventStreamHub streamHub;
    private readonly IWorkflowNaradPublisher naradPublisher;

    public WorkflowStepExecutor(
        string instanceId,
        GatewayWorkflowNode node,
        GatewayWorkflowStepAttempt attempt,
        IGatewayWorkflowStore workflowStore,
        IGatewayApprovalStore approvalStore,
        IChatGatewayService chatGatewayService,
        IRuntimeEventHistoryReader historyReader,
        IRuntimeEventStreamHub streamHub,
        IWorkflowNaradPublisher naradPublisher)
        : base(node.NodeId, options: null, declareCrossRunShareable: true)
    {
        this.instanceId = instanceId;
        this.node = node;
        this.attempt = attempt;
        this.workflowStore = workflowStore;
        this.approvalStore = approvalStore;
        this.chatGatewayService = chatGatewayService;
        this.historyReader = historyReader;
        this.streamHub = streamHub;
        this.naradPublisher = naradPublisher;
    }

    public override async ValueTask<string> HandleAsync(string message, IWorkflowContext context, CancellationToken cancellationToken)
    {
        _ = context;
        GatewayWorkflowInstance instance = workflowStore.GetInstance(instanceId)
            ?? throw new InvalidOperationException($"Workflow instance '{instanceId}' was not found.");
        if (instance.StopRequested || string.Equals(instance.Status, WorkflowInstanceStatuses.Cancelled, StringComparison.Ordinal))
        {
            throw new OperationCanceledException("Workflow stop requested.");
        }

        GatewayWorkflowStepAttempt currentAttempt = instance.Steps.First(step => string.Equals(step.Id, attempt.Id, StringComparison.Ordinal));
        JsonObject envelope = JsonNode.Parse(message)?.AsObject()
            ?? JsonNode.Parse(instance.Envelope.ToJsonString())!.AsObject();

        if (string.Equals(currentAttempt.Status, WorkflowStepAttemptStatuses.Finished, StringComparison.Ordinal))
        {
            return instance.Envelope.ToJsonString();
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        string startedAt = currentAttempt.StartedAt ?? now.ToString("O");
        string? sessionId = currentAttempt.SessionId;
        string? response = null;
        string? terminalType = null;
        if (!TryReadExistingTerminal(sessionId, out terminalType, out response))
        {
            _ = workflowStore.TrySetInstanceStatus(
                instance.Id,
                WorkflowInstanceStatuses.Running,
                node.NodeId,
                failureCode: null,
                error: null,
                stopRequested: null,
                now,
                out _);
            _ = workflowStore.TryUpdateStepAttempt(
                currentAttempt with
                {
                    Status = WorkflowStepAttemptStatuses.Running,
                    StartedAt = startedAt,
                },
                now);

            if (HasOpenPauseForAttempt(sessionId))
            {
                (terminalType, response) = await WaitForAgentSessionTerminalAsync(sessionId!, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                string prompt = WorkflowPromptRenderer.RenderStep(node, envelope, instance.Snapshot.Governed);
                string? reviewNote = WorkflowReviewNotes.ForAttempt(instance, currentAttempt, approvalStore);
                if (reviewNote is not null)
                {
                    prompt = $"{prompt}\n\nThe owner asked for changes: {reviewNote}";
                }
                var body = new JsonObject
                {
                    ["target_type"] = "agent",
                    ["target_id"] = node.AgentId,
                    ["message"] = prompt,
                    ["user_id"] = instance.OwnerUserId,
                    ["source"] = "workflow",
                    ["run_id"] = currentAttempt.RunId,
                    ["memoryWrites"] = node.MemoryWrites,
                };
                if (instance.Snapshot.Nodes.Length > 0
                    && string.Equals(instance.Snapshot.Nodes[0].NodeId, node.NodeId, StringComparison.Ordinal))
                {
                    body["workspaceFiles"] = JsonSerializer.SerializeToNode(
                        workflowStore.ListArtifacts(instance.Id).Select(static artifact => artifact.StoragePath).ToArray(),
                        KautukaJsonContext.Default.StringArray);
                }
                if (!string.IsNullOrWhiteSpace(sessionId))
                {
                    body["session_id"] = sessionId;
                }

                bool publishedStart = false;
                await foreach (NaradPersistedEvent evt in chatGatewayService.StreamChatAsync(
                    JsonSerializer.SerializeToElement(body, KautukaJsonContext.Default.JsonObject),
                    cancellationToken: cancellationToken).ConfigureAwait(false))
                {
                    PersistSession(ref currentAttempt, ref sessionId, evt.SessionId, startedAt);

                    if (!publishedStart)
                    {
                        var startedBody = new JsonObject
                        {
                            ["nodeId"] = node.NodeId,
                            ["agentRunId"] = currentAttempt.RunId,
                            ["stepAttempt"] = currentAttempt.Attempt,
                        };
                        if (!string.IsNullOrWhiteSpace(sessionId))
                        {
                            startedBody["agentSessionId"] = sessionId;
                        }

                        naradPublisher.Publish(instance, NaradEventTypes.WorkflowStepStarted, startedBody);
                        publishedStart = true;
                    }

                    if (!IsStepRunTerminal(evt))
                    {
                        continue;
                    }

                    terminalType = evt.EventType;
                    response = NaradStreamObservation.BuildTerminalDone(evt, "agent", node.AgentId, evt.SessionId).Result.Response;
                    break;
                }

                if (terminalType is null && !string.IsNullOrWhiteSpace(sessionId)
                    && !TryReadExistingTerminal(sessionId, out terminalType, out response))
                {
                    (terminalType, response) = await WaitForAgentSessionTerminalAsync(sessionId, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }

        now = DateTimeOffset.UtcNow;
        instance = workflowStore.GetInstance(instanceId)
            ?? throw new InvalidOperationException($"Workflow instance '{instanceId}' was not found.");
        if (instance.StopRequested)
        {
            throw new OperationCanceledException("Workflow stop requested.");
        }

        if (!string.Equals(terminalType, NaradEventTypes.RunFinished, StringComparison.Ordinal))
        {
            string failure = string.IsNullOrWhiteSpace(response) ? "Step agent run failed." : response;
            string failureCode = string.Equals(terminalType, NaradEventTypes.RunCancelled, StringComparison.Ordinal)
                ? WorkflowFailureCodes.StepRunCancelled
                : WorkflowFailureCodes.StepRunError;
            _ = workflowStore.TryUpdateStepAttempt(
                currentAttempt with
                {
                    SessionId = sessionId,
                    Status = WorkflowStepAttemptStatuses.Failed,
                    FailureCode = failureCode,
                    StartedAt = startedAt,
                    EndedAt = now.ToString("O"),
                },
                now);
            naradPublisher.Publish(instance, NaradEventTypes.WorkflowStepFailed, new JsonObject
            {
                ["nodeId"] = node.NodeId,
                ["agentRunId"] = currentAttempt.RunId,
                ["stepAttempt"] = currentAttempt.Attempt,
                ["code"] = failureCode,
                ["message"] = failure,
            });
            throw new InvalidOperationException(failure);
        }

        JsonObject output;
        IReadOnlyList<string> droppedKeys;
        try
        {
            output = WorkflowStepOutputParser.Parse(
                response ?? string.Empty,
                node,
                instance.Snapshot.Governed,
                out droppedKeys);
        }
        catch (WorkflowSchemaException ex)
        {
            _ = workflowStore.TryUpdateStepAttempt(
                currentAttempt with
                {
                    SessionId = sessionId,
                    Status = WorkflowStepAttemptStatuses.Failed,
                    FailureCode = WorkflowFailureCodes.StructuredOutputInvalid,
                    StartedAt = startedAt,
                    EndedAt = now.ToString("O"),
                },
                now);
            naradPublisher.Publish(instance, NaradEventTypes.WorkflowStepFailed, new JsonObject
            {
                ["nodeId"] = node.NodeId,
                ["agentRunId"] = currentAttempt.RunId,
                ["stepAttempt"] = currentAttempt.Attempt,
                ["code"] = WorkflowFailureCodes.StructuredOutputInvalid,
                ["message"] = ex.Message,
            });
            throw;
        }

        JsonObject nextEnvelope = node.Maps.Length == 0
            ? MergeObjects(envelope, output)
            : WorkflowMapApplier.Apply(
                envelope,
                output,
                [.. node.Maps.Select(static map => new WorkflowStateMap(map.From, map.To))]);
        WorkflowEnvelopeCanonical.EnsureSafe(nextEnvelope);
        if (droppedKeys.Count > 0)
        {
            naradPublisher.Publish(instance, NaradEventTypes.StructuredOutputExtracted, new JsonObject
            {
                ["nodeId"] = node.NodeId,
                ["droppedKeys"] = new JsonArray([.. droppedKeys.Select(static key => (JsonNode)key)]),
            });
        }

        if (instance.Snapshot.WorkingSchema.Count > 0)
        {
            WorkflowSchemaSubset.ValidateInstance(instance.Snapshot.WorkingSchema, nextEnvelope);
        }

        GatewayWorkflowInstance? updated = instance;
        if (!node.RequiresReview)
        {
            _ = workflowStore.TryUpdateEnvelope(instance.Id, nextEnvelope, now, out updated);
        }

        _ = workflowStore.TryUpdateStepAttempt(
            currentAttempt with
            {
                SessionId = sessionId,
                Status = WorkflowStepAttemptStatuses.Finished,
                StartedAt = startedAt,
                EndedAt = now.ToString("O"),
            },
            now);
        naradPublisher.Publish(updated ?? instance, NaradEventTypes.WorkflowStepFinished, new JsonObject
        {
            ["nodeId"] = node.NodeId,
            ["agentRunId"] = currentAttempt.RunId,
            ["stepAttempt"] = currentAttempt.Attempt,
            ["envelopeHash"] = WorkflowEnvelopeCanonical.Sha256Hex(nextEnvelope),
            ["envelopeRevision"] = (updated ?? instance).EnvelopeRevision,
        });
        return nextEnvelope.ToJsonString();
    }

    private void PersistSession(
        ref GatewayWorkflowStepAttempt currentAttempt,
        ref string? sessionId,
        string? eventSessionId,
        string startedAt)
    {
        sessionId ??= eventSessionId;
        if (string.IsNullOrWhiteSpace(sessionId)
            || string.Equals(currentAttempt.SessionId, sessionId, StringComparison.Ordinal))
        {
            return;
        }

        _ = workflowStore.TryUpdateStepAttempt(
            currentAttempt with
            {
                SessionId = sessionId,
                Status = WorkflowStepAttemptStatuses.Running,
                StartedAt = startedAt,
            },
            DateTimeOffset.UtcNow);
        currentAttempt = currentAttempt with { SessionId = sessionId };
    }

    private async Task<(string TerminalType, string? Response)> WaitForAgentSessionTerminalAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using RuntimeEventStreamSubscription subscription = streamHub.SubscribeForSession(sessionId);
            if (TryReadExistingTerminal(sessionId, out string? existingType, out string? existingResponse)
                && existingType is not null)
            {
                return (existingType, existingResponse);
            }

            try
            {
                await foreach (NaradPersistedEvent evt in subscription.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (!IsStepRunTerminal(evt))
                    {
                        continue;
                    }

                    return (
                        evt.EventType,
                        NaradStreamObservation.BuildTerminalDone(evt, "agent", node.AgentId, evt.SessionId).Result.Response);
                }
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("reconnect to replay durable history", StringComparison.Ordinal))
            {
                continue;
            }

            throw new InvalidOperationException($"Agent session '{sessionId}' ended without a terminal run event.");
        }
    }

    private bool TryReadExistingTerminal(string? sessionId, out string? terminalType, out string? response)
    {
        terminalType = null;
        response = null;
        NaradPersistedEvent? terminal = FindStepRunTerminal(sessionId);
        if (terminal is null)
        {
            return false;
        }

        terminalType = terminal.EventType;
        response = NaradStreamObservation.BuildTerminalDone(terminal, "agent", node.AgentId, terminal.SessionId).Result.Response;
        return true;
    }

    private bool HasOpenPauseForAttempt(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        bool paused = false;
        foreach (NaradPersistedEvent evt in historyReader.ReadSessionHistory(node.AgentId, sessionId))
        {
            if (!IsThisAttemptRun(evt))
            {
                continue;
            }

            if (evt.EventType is NaradEventTypes.InterruptRequested or NaradEventTypes.RunPaused)
            {
                paused = true;
            }

            if (NaradEventTypes.IsRootRunTerminalEventType(evt.EventType))
            {
                return false;
            }
        }

        return paused;
    }

    private NaradPersistedEvent? FindStepRunTerminal(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        NaradPersistedEvent? terminal = null;
        foreach (NaradPersistedEvent evt in historyReader.ReadSessionHistory(node.AgentId, sessionId))
        {
            if (IsStepRunTerminal(evt))
            {
                terminal = evt;
            }
        }

        return terminal;
    }

    private bool IsStepRunTerminal(NaradPersistedEvent evt)
        => IsThisAttemptRun(evt) && NaradEventTypes.IsRootRunTerminalEventType(evt.EventType);

    private bool IsThisAttemptRun(NaradPersistedEvent evt)
        => !string.IsNullOrWhiteSpace(evt.RunId)
            && string.Equals(evt.RunId, evt.RootRunId, StringComparison.Ordinal)
            && string.Equals(evt.RunId, attempt.RunId, StringComparison.Ordinal);

    private static JsonObject MergeObjects(JsonObject envelope, JsonObject output)
    {
        JsonObject clone = JsonNode.Parse(envelope.ToJsonString())!.AsObject();
        foreach (KeyValuePair<string, JsonNode?> pair in output)
        {
            clone[pair.Key] = pair.Value?.DeepClone();
        }

        return clone;
    }
}
