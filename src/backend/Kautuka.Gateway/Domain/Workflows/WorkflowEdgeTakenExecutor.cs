using System.Text.Json.Nodes;

using Microsoft.Agents.AI.Workflows;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Workflows;

internal sealed class WorkflowEdgeTakenExecutor : Executor<string, string>
{
    private readonly GatewayWorkflowInstance instance;
    private readonly IWorkflowNaradPublisher naradPublisher;
    private readonly string fromNodeId;
    private readonly string toNodeId;
    private readonly int caseIndex;
    private readonly string pointer;
    private readonly string op;

    public WorkflowEdgeTakenExecutor(
        GatewayWorkflowInstance instance,
        IWorkflowNaradPublisher naradPublisher,
        string fromNodeId,
        string toNodeId,
        int caseIndex,
        string pointer,
        string op)
        : base($"edge-{fromNodeId}-{toNodeId}-{caseIndex}", options: null, declareCrossRunShareable: true)
    {
        this.instance = instance;
        this.naradPublisher = naradPublisher;
        this.fromNodeId = fromNodeId;
        this.toNodeId = toNodeId;
        this.caseIndex = caseIndex;
        this.pointer = pointer;
        this.op = op;
    }

    public override ValueTask<string> HandleAsync(string message, IWorkflowContext context, CancellationToken cancellationToken)
    {
        naradPublisher.Publish(instance, NaradEventTypes.WorkflowEdgeTaken, new JsonObject
        {
            ["from"] = fromNodeId,
            ["to"] = toNodeId,
            ["caseIndex"] = caseIndex,
            ["pointer"] = pointer,
            ["op"] = op,
        });
        return ValueTask.FromResult(message);
    }
}
