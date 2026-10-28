using System.Text.Json.Nodes;

namespace Kautuka.Gateway.Domain.Workflows;

internal static class WorkflowFailureCodes
{
    public const string GateChangesExhaustedMessage = "You've asked for changes twice. Approve or stop this run.";
    public const string GateSchemaFailed = "gate.schema_failed";
    public const string GateRejectedExhausted = "gate.rejected_exhausted";
    public const string GateChangesExhausted = "gate.changes_exhausted";
    public const string GateExpired = "gate.expired";
    public const string StepRunError = "step.run_error";
    public const string StepRunCancelled = "step.run_cancelled";
    public const string StepToolUnresolvedFailure = "step.tool_unresolved_failure";
    public const string StepBudgetExceeded = "step.budget_exceeded";
    public const string WorkflowStopped = "workflow.stopped";
    public const string DependencyUnavailable = "dependency.unavailable";
    public const string SnapshotDivergent = "snapshot.divergent";
    public const string EnvelopeSecretDetected = "envelope.secret_detected";
    public const string EnvelopeSizeExceeded = "envelope.size_exceeded";
    public const string StructuredOutputInvalid = "structured_output_invalid";
    public const string ReviewRejected = "review_rejected";
}

internal static class WorkflowRetryPolicy
{
    private const int maxFinishedAttemptsWithChangeRequests = 3;

    public static string ReviewFailureMessage(string? exhaustedCode)
        => string.Equals(exhaustedCode, WorkflowFailureCodes.GateChangesExhausted, StringComparison.Ordinal)
            ? WorkflowFailureCodes.GateChangesExhaustedMessage
            : "Review rejected.";

    public static bool CanRequestChanges(int finishedAttempts)
        => finishedAttempts < maxFinishedAttemptsWithChangeRequests;

    public static int RemainingChangeRequests(int finishedAttempts)
        => Math.Max(0, maxFinishedAttemptsWithChangeRequests - finishedAttempts);

    public static int CountFailuresSinceLastFinish(IReadOnlyList<GatewayWorkflowStepAttempt> attempts)
        => attempts
            .Reverse()
            .TakeWhile(step => !string.Equals(step.Status, WorkflowStepAttemptStatuses.Finished, StringComparison.Ordinal))
            .Count(step => string.Equals(step.Status, WorkflowStepAttemptStatuses.Failed, StringComparison.Ordinal));

    public static bool CanRetryStructuredOutput(string sideEffects, int currentAttempt, int maxRetries)
    {
        if (WorkflowDefinitionCodec.ParseSideEffects(sideEffects) == WorkflowSideEffects.External)
        {
            return false;
        }

        return currentAttempt < maxRetries + 1;
    }
}

internal enum WorkflowSideEffects
{
    None,
    Idempotent,
    External,
}

internal enum WorkflowOutputKind
{
    Schema,
    Text,
}

internal sealed record WorkflowNodeDefinition(
    string NodeId,
    string AgentId,
    string PromptTemplate,
    WorkflowOutputKind OutputKind,
    JsonObject? OutputSchema,
    IReadOnlyList<WorkflowStateMap> Maps,
    WorkflowSideEffects SideEffects,
    bool MemoryWrites,
    bool RequiresReview,
    int MaxRetries,
    IReadOnlyList<GatewayWorkflowSwitchCase>? SwitchCases = null,
    string? DefaultGoTo = null);

internal sealed record WorkflowDefinitionDraft(
    string Id,
    string Name,
    bool Governed,
    JsonObject InputSchema,
    JsonObject WorkingSchema,
    JsonObject OutputSchema,
    IReadOnlyList<WorkflowNodeDefinition> Nodes);

internal static class WorkflowDefinitionValidator
{
    public static void Validate(WorkflowDefinitionDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(draft.Id))
        {
            throw new WorkflowSchemaException("Workflow id is required.");
        }

        if (string.IsNullOrWhiteSpace(draft.Name))
        {
            throw new WorkflowSchemaException("Workflow name is required.");
        }

        WorkflowSchemaSubset.ValidateSchema(draft.InputSchema);
        WorkflowSchemaSubset.ValidateSchema(draft.WorkingSchema);
        WorkflowSchemaSubset.ValidateSchema(draft.OutputSchema);
        if (draft.Nodes.Count == 0)
        {
            throw new WorkflowSchemaException("Workflow must declare at least one node.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool sawExternalWithoutReview = false;
        foreach (WorkflowNodeDefinition node in draft.Nodes)
        {
            if (string.IsNullOrWhiteSpace(node.NodeId) || !seen.Add(node.NodeId))
            {
                throw new WorkflowSchemaException("Each node needs a unique nodeId.");
            }

            if (string.IsNullOrWhiteSpace(node.AgentId))
            {
                throw new WorkflowSchemaException($"Node '{node.NodeId}' needs an agentId.");
            }

            if (node.MaxRetries < 0)
            {
                throw new WorkflowSchemaException($"Node '{node.NodeId}' maxRetries must be >= 0.");
            }

            if (node.OutputKind == WorkflowOutputKind.Text)
            {
                if (draft.Governed)
                {
                    throw new WorkflowSchemaException("Governed workflows cannot use output: text.");
                }

                if (node.Maps.Count > 0)
                {
                    throw new WorkflowSchemaException($"Node '{node.NodeId}' text output cannot declare maps.");
                }
            }
            else
            {
                if (node.OutputSchema is null)
                {
                    throw new WorkflowSchemaException($"Node '{node.NodeId}' must declare outputSchema.");
                }

                WorkflowSchemaSubset.ValidateSchema(node.OutputSchema);
                foreach (WorkflowStateMap map in node.Maps)
                {
                    if (!WorkflowSchemaSubset.PointerResolvesToDeclaredProperty(node.OutputSchema, map.From)
                        || !WorkflowSchemaSubset.PointerResolvesToDeclaredProperty(draft.WorkingSchema, map.To))
                    {
                        throw new WorkflowSchemaException(
                            $"Node '{node.NodeId}' map '{map.From}' -> '{map.To}' is not resolvable against schemas.");
                    }
                }
            }

            if (node.SideEffects == WorkflowSideEffects.External && !node.RequiresReview && !sawExternalWithoutReview)
            {
                sawExternalWithoutReview = true;
            }

            ValidateSwitch(draft, node);
        }

        ValidateAcyclicPaths(draft.Nodes);

        if (draft.Governed)
        {
            foreach (WorkflowNodeDefinition node in draft.Nodes)
            {
                if (node.OutputKind == WorkflowOutputKind.Schema && node.Maps.Count == 0)
                {
                    throw new WorkflowSchemaException($"Governed node '{node.NodeId}' must declare maps.");
                }

                if (node.SideEffects == WorkflowSideEffects.External && !EveryPathToNodeHasReview(draft.Nodes, node.NodeId))
                {
                    throw new WorkflowSchemaException(
                        $"Governed workflow requires a review gate on every path before external-effect node '{node.NodeId}'.");
                }
            }
        }
    }

    private static void ValidateSwitch(
        WorkflowDefinitionDraft draft,
        WorkflowNodeDefinition node)
    {
        IReadOnlyList<GatewayWorkflowSwitchCase> cases = node.SwitchCases ?? [];
        if (cases.Count == 0 && string.IsNullOrWhiteSpace(node.DefaultGoTo))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(node.DefaultGoTo))
        {
            throw new WorkflowSchemaException($"Node '{node.NodeId}' switch needs a defaultGoTo.");
        }

        if (!draft.Nodes.Any(candidate => candidate.NodeId == node.DefaultGoTo))
        {
            throw new WorkflowSchemaException($"Node '{node.NodeId}' defaultGoTo '{node.DefaultGoTo}' is not a node.");
        }

        foreach (GatewayWorkflowSwitchCase rule in cases)
        {
            if (string.IsNullOrWhiteSpace(rule.GoTo) || !draft.Nodes.Any(candidate => candidate.NodeId == rule.GoTo))
            {
                throw new WorkflowSchemaException($"Node '{node.NodeId}' switch goTo '{rule.GoTo}' is not a node.");
            }

            if (!WorkflowSwitchOps.All.Contains(rule.Op))
            {
                throw new WorkflowSchemaException($"Node '{node.NodeId}' switch operator '{rule.Op}' is not allowed.");
            }

            if (!WorkflowSchemaSubset.PointerResolvesToDeclaredProperty(draft.WorkingSchema, rule.Pointer))
            {
                throw new WorkflowSchemaException($"Node '{node.NodeId}' switch pointer '{rule.Pointer}' is not a workingSchema field.");
            }
        }
    }

    private static bool EveryPathToNodeHasReview(IReadOnlyList<WorkflowNodeDefinition> nodes, string targetId)
    {
        if (nodes.Count == 0)
        {
            return false;
        }

        Dictionary<string, WorkflowNodeDefinition> byId = nodes.ToDictionary(node => node.NodeId, StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var memo = new Dictionary<string, bool>(StringComparer.Ordinal);

        bool Walk(string nodeId, bool sawReview)
        {
            if (string.Equals(nodeId, targetId, StringComparison.Ordinal))
            {
                return sawReview;
            }

            if (visiting.Contains(nodeId) || !byId.TryGetValue(nodeId, out WorkflowNodeDefinition? node))
            {
                return true;
            }

            string key = nodeId + ":" + sawReview;
            if (memo.TryGetValue(key, out bool cached))
            {
                return cached;
            }

            visiting.Add(nodeId);
            bool review = sawReview || node.RequiresReview;
            List<string> next = NextNodes(nodes, node);
            bool ok = next.Count == 0 || next.All(candidate => Walk(candidate, review));
            visiting.Remove(nodeId);
            memo[key] = ok;
            return ok;
        }

        return Walk(nodes[0].NodeId, false);
    }

    private static List<string> NextNodes(IReadOnlyList<WorkflowNodeDefinition> nodes, WorkflowNodeDefinition node)
    {
        var next = new List<string>();
        foreach (GatewayWorkflowSwitchCase rule in node.SwitchCases ?? [])
        {
            next.Add(rule.GoTo);
        }

        if (!string.IsNullOrWhiteSpace(node.DefaultGoTo))
        {
            next.Add(node.DefaultGoTo);
        }

        if (next.Count > 0)
        {
            return next;
        }

        int index = -1;
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].NodeId == node.NodeId)
            {
                index = i;
                break;
            }
        }

        if (index >= 0 && index + 1 < nodes.Count)
        {
            next.Add(nodes[index + 1].NodeId);
        }

        return next;
    }

    private static void ValidateAcyclicPaths(IReadOnlyList<WorkflowNodeDefinition> nodes)
    {
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int index = 0; index < nodes.Count; index++)
        {
            indexes[nodes[index].NodeId] = index;
        }

        var targeted = new HashSet<string>(StringComparer.Ordinal);
        foreach (WorkflowNodeDefinition node in nodes)
        {
            foreach (GatewayWorkflowSwitchCase rule in node.SwitchCases ?? [])
            {
                targeted.Add(rule.GoTo);
            }

            if (!string.IsNullOrWhiteSpace(node.DefaultGoTo))
            {
                targeted.Add(node.DefaultGoTo);
            }
        }

        var edges = nodes.ToDictionary(node => node.NodeId, _ => new List<string>(), StringComparer.Ordinal);
        for (int index = 0; index < nodes.Count; index++)
        {
            WorkflowNodeDefinition node = nodes[index];
            List<string> next = edges[node.NodeId];
            IReadOnlyList<GatewayWorkflowSwitchCase> cases = node.SwitchCases ?? [];
            if (cases.Count > 0 || !string.IsNullOrWhiteSpace(node.DefaultGoTo))
            {
                next.AddRange(cases.Select(rule => rule.GoTo));
                if (!string.IsNullOrWhiteSpace(node.DefaultGoTo))
                {
                    next.Add(node.DefaultGoTo);
                }

                continue;
            }

            if (index + 1 < nodes.Count)
            {
                string nextNodeId = nodes[index + 1].NodeId;
                if (!targeted.Contains(nextNodeId) || string.Equals(nextNodeId, nodes[0].NodeId, StringComparison.Ordinal))
                {
                    next.Add(nextNodeId);
                }
            }
        }

        bool Reaches(string from, string target, HashSet<string> visited)
        {
            if (string.Equals(from, target, StringComparison.Ordinal))
            {
                return true;
            }

            if (!visited.Add(from))
            {
                return false;
            }

            return edges[from].Any(candidate => Reaches(candidate, target, visited));
        }

        for (int sourceIndex = 0; sourceIndex < nodes.Count; sourceIndex++)
        {
            WorkflowNodeDefinition source = nodes[sourceIndex];
            foreach (string target in edges[source.NodeId])
            {
                if (indexes[target] <= sourceIndex && Reaches(target, source.NodeId, new HashSet<string>(StringComparer.Ordinal)))
                {
                    throw new WorkflowSchemaException(
                        $"Workflow paths can't loop; step '{source.NodeId}' leads back into a step that comes before it on the same path.");
                }
            }
        }
    }
}
