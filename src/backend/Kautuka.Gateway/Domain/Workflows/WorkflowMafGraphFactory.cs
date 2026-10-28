using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Stores;

using Microsoft.Agents.AI.Workflows;

namespace Kautuka.Gateway.Domain.Workflows;

internal static class WorkflowMafGraphFactory
{
    public static Workflow Build(
        GatewayWorkflowInstance instance,
        IGatewayWorkflowStore workflowStore,
        IGatewayApprovalStore approvalStore,
        IChatGatewayService chatGatewayService,
        IWorkflowNaradPublisher naradPublisher,
        IRuntimeEventHistoryReader historyReader,
        IRuntimeEventStreamHub streamHub,
        string? startNodeId = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        GatewayWorkflowNode[] nodes = instance.Snapshot.Nodes;
        if (nodes.Length == 0)
        {
            throw new InvalidOperationException("Workflow snapshot has no steps.");
        }

        Dictionary<string, GatewayWorkflowStepAttempt> attempts = instance.Steps
            .GroupBy(step => step.NodeId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(step => step.Attempt).Last(), StringComparer.Ordinal);

        var steps = new Dictionary<string, ExecutorBinding>(StringComparer.Ordinal);
        foreach (GatewayWorkflowNode node in nodes)
        {
            if (!attempts.TryGetValue(node.NodeId, out GatewayWorkflowStepAttempt? attempt))
            {
                throw new InvalidOperationException($"Workflow instance '{instance.Id}' is missing a pre-allocated attempt for '{node.NodeId}'.");
            }

            steps[node.NodeId] = new WorkflowStepExecutor(
                instance.Id,
                node,
                attempt,
                workflowStore,
                approvalStore,
                chatGatewayService,
                historyReader,
                streamHub,
                naradPublisher);
        }

        return Compose(
            nodes,
            startNodeId,
            steps,
            (from, to, index, pointer, op) => new WorkflowEdgeTakenExecutor(
                instance,
                naradPublisher,
                from,
                to,
                index,
                pointer,
                op));
    }

    public static string Diagram(
        GatewayWorkflowNode[] nodes,
        IReadOnlyDictionary<string, string>? agentNames)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (nodes.Length == 0)
        {
            return string.Empty;
        }

        var steps = new Dictionary<string, ExecutorBinding>(StringComparer.Ordinal);
        foreach (GatewayWorkflowNode node in nodes)
        {
            steps[node.NodeId] = new WorkflowDiagramExecutor(node.NodeId);
        }

        Workflow workflow = Compose(
            nodes,
            startNodeId: null,
            steps,
            (from, to, index, _, _) => new WorkflowDiagramExecutor($"edge-{from}-{to}-{index}"));
        return WorkflowDiagramText.Present(workflow.ToMermaidString(), nodes, agentNames);
    }

    private static Workflow Compose(
        GatewayWorkflowNode[] nodes,
        string? startNodeId,
        Dictionary<string, ExecutorBinding> steps,
        Func<string, string, int, string, string, ExecutorBinding> createEdge)
    {
        string startId = string.IsNullOrWhiteSpace(startNodeId) ? nodes[0].NodeId : startNodeId;
        if (!steps.TryGetValue(startId, out ExecutorBinding? startExecutor))
        {
            throw new InvalidOperationException($"Start node '{startId}' was not found.");
        }

        var outlets = new Dictionary<string, ExecutorBinding>(StringComparer.Ordinal);
        foreach (GatewayWorkflowNode node in nodes)
        {
            outlets[node.NodeId] = steps[node.NodeId];
        }

        var builder = new WorkflowBuilder(startExecutor);
        foreach (GatewayWorkflowNode node in nodes)
        {
            ExecutorBinding outlet = steps[node.NodeId];
            if (node.RequiresReview)
            {
                RequestPort port = RequestPort.Create<string, string>($"review-{node.NodeId}");
                builder.AddEdge(steps[node.NodeId], port);
                outlet = port;
                outlets[node.NodeId] = port;
            }

            if (node.HasSwitch)
            {
                builder.AddSwitch(outlet, switchBuilder =>
                {
                    for (int index = 0; index < node.EffectiveSwitchCases.Length; index++)
                    {
                        GatewayWorkflowSwitchCase rule = node.EffectiveSwitchCases[index];
                        ExecutorBinding taken = createEdge(node.NodeId, rule.GoTo, index, rule.Pointer, rule.Op);
                        builder.AddEdge(taken, steps[rule.GoTo], WorkflowDiagramText.Condition(rule), idempotent: false);
                        switchBuilder.AddCase<string>(
                            payload => payload is not null && WorkflowSwitch.Predicate(rule)(payload),
                            [taken]);
                    }

                    if (!string.IsNullOrWhiteSpace(node.DefaultGoTo))
                    {
                        ExecutorBinding taken = createEdge(
                            node.NodeId,
                            node.DefaultGoTo,
                            node.EffectiveSwitchCases.Length,
                            string.Empty,
                            "default");
                        builder.AddEdge(
                            taken,
                            steps[node.DefaultGoTo],
                            node.EffectiveSwitchCases.Length == 0 ? string.Empty : "Otherwise",
                            idempotent: false);
                        switchBuilder.WithDefault([taken]);
                    }
                });
                continue;
            }

            int nodeIndex = Array.FindIndex(nodes, candidate => string.Equals(candidate.NodeId, node.NodeId, StringComparison.Ordinal));
            if (nodeIndex >= 0 && nodeIndex + 1 < nodes.Length)
            {
                GatewayWorkflowNode next = nodes[nodeIndex + 1];
                if (!IsTargetedBySwitch(nodes, next.NodeId) || string.Equals(startId, next.NodeId, StringComparison.Ordinal))
                {
                    builder.AddEdge(outlet, steps[next.NodeId]);
                }
            }
        }

        ExecutorBinding[] ends = [.. FindEndSteps(nodes, outlets, startId)];
        return builder.WithOutputFrom(ends.Length == 0 ? [outlets[nodes[^1].NodeId]] : ends).Build();
    }

    private static bool IsTargetedBySwitch(GatewayWorkflowNode[] nodes, string nodeId)
        => nodes.Any(node =>
            node.EffectiveSwitchCases.Any(rule => string.Equals(rule.GoTo, nodeId, StringComparison.Ordinal))
            || string.Equals(node.DefaultGoTo, nodeId, StringComparison.Ordinal));

    private static IEnumerable<ExecutorBinding> FindEndSteps(
        GatewayWorkflowNode[] nodes,
        Dictionary<string, ExecutorBinding> outlets,
        string startId)
    {
        for (int index = 0; index < nodes.Length; index++)
        {
            GatewayWorkflowNode node = nodes[index];
            if (node.HasSwitch)
            {
                if (node.EffectiveSwitchCases.Length == 0 && string.IsNullOrWhiteSpace(node.DefaultGoTo))
                {
                    yield return outlets[node.NodeId];
                }
                continue;
            }

            if (index + 1 < nodes.Length)
            {
                GatewayWorkflowNode next = nodes[index + 1];
                if (!IsTargetedBySwitch(nodes, next.NodeId) || string.Equals(startId, next.NodeId, StringComparison.Ordinal))
                {
                    continue;
                }
            }

            yield return outlets[node.NodeId];
        }
    }
}

internal sealed class WorkflowDiagramExecutor : Executor<string, string>
{
    public WorkflowDiagramExecutor(string id)
        : base(id, options: null, declareCrossRunShareable: true)
    {
    }

    public override ValueTask<string> HandleAsync(string message, IWorkflowContext context, CancellationToken cancellationToken)
    {
        _ = context;
        _ = cancellationToken;
        return ValueTask.FromResult(message);
    }
}
