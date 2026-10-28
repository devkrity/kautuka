using System.Text.Json.Nodes;

using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Infrastructure.Agents;

internal sealed class RunShellAgentToolProvider(IToolExecutionPolicyService toolExecutionPolicyService) : IAgentToolSurfaceContributor
{
    public string SurfaceSource => AgentToolSurfaceSources.Shell;

    public ValueTask<AgentToolSurfaceContribution> ContributeAsync(AgentToolContext context, CancellationToken ct)
    {
        _ = ct;
        ArgumentNullException.ThrowIfNull(context);

        ToolPolicyDecision exposureDecision = toolExecutionPolicyService.EvaluateExposure(
            ToolPolicyCategory.SuperExec,
            context.AgentDefinition);
        if (!exposureDecision.Allowed)
        {
            return ValueTask.FromResult(new AgentToolSurfaceContribution([], Complete: true));
        }

        return ValueTask.FromResult(new AgentToolSurfaceContribution(
            [
                AgentToolSurfaceEntry.Describe(
                    SurfaceSource,
                    "run_shell",
                    "run_shell",
                    "Execute a shell command in the active sandbox container (agent context). Returns exit_code, stdout, and stderr.",
                    "{}"),
            ],
            Complete: true));
    }

    public ValueTask<IEnumerable<AIFunction>> BuildToolsAsync(AgentToolContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        ToolPolicyDecision exposureDecision = toolExecutionPolicyService.EvaluateExposure(
            ToolPolicyCategory.SuperExec,
            context.AgentDefinition);
        if (!exposureDecision.Allowed)
        {
            return ValueTask.FromResult<IEnumerable<AIFunction>>([]);
        }

        AIFunction shellTool = AIFunctionFactory.Create(
            async (
                [System.ComponentModel.Description("The shell command to execute inside the sandbox container.")] string command,
                CancellationToken cancellationToken) =>
            {
                ToolPolicyDecision executionDecision = toolExecutionPolicyService.ValidateExecution(
                    ToolPolicyCategory.SuperExec,
                    context.AgentDefinition);
                if (!executionDecision.Allowed)
                {
                    throw new InvalidOperationException(
                        $"SuperExec execution denied by policy ({executionDecision.ReasonCode}): {executionDecision.Message}");
                }

                var result = await context.WorkspaceTool.RunAgentShellAsync(context.AgentId, command, context.UserId, null, cancellationToken).ConfigureAwait(false);

                return new JsonObject
                {
                    ["exit_code"] = result.ExitCode,
                    ["stdout"] = result.StdOut.Length > 8000 ? result.StdOut[^8000..] : result.StdOut,
                    ["stderr"] = result.StdErr.Length > 2000 ? result.StdErr[^2000..] : result.StdErr,
                };
            },
            "run_shell",
            "Execute a shell command in the active sandbox container (agent context). Returns exit_code, stdout, and stderr.",
            serializerOptions: null);

        ToolApprovalDirective directive = toolExecutionPolicyService.EvaluateApproval(new ToolApprovalRequest(
            Identity: new ToolIdentity(
                Category: ToolPolicyCategory.SuperExec,
                ToolName: "run_shell"),
            Agent: context.AgentDefinition));
        IEnumerable<AIFunction> result = [directive.RequiresApproval ? new ApprovalRequiredAIFunction(shellTool) : shellTool];
        return ValueTask.FromResult(result);
    }
}
