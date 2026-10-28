using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Infrastructure.Agents;

internal sealed partial class McpAgentToolProvider
{
    internal static IEnumerable<AIFunction> ApplyApprovalPolicy(
        IEnumerable<(string ServerId, IReadOnlyList<AIFunction>? Tools)> serverTools,
        ApprovalPolicy policy,
        IToolExecutionPolicyService? toolExecutionPolicyService = null,
        List<AIFunction>? allToolsFastPath = null)
    {
        toolExecutionPolicyService ??= new ToolExecutionPolicyService();
        bool anyDisabledFunctions = policy.DisabledMcpFunctions.Count > 0;

        if (policy.RequireMcpIds.Count == 0
            && policy.RequireMcpFunctionFilters.Count == 0
            && !anyDisabledFunctions)
        {
            return allToolsFastPath ?? serverTools
                .Where(static s => s.Tools is not null)
                .SelectMany(static s => s.Tools!);
        }

        var wrappedTools = new List<AIFunction>();
        foreach ((string serverId, IReadOnlyList<AIFunction>? tools) in serverTools)
        {
            if (tools is null)
                continue;

            foreach (AIFunction tool in tools)
            {
                ToolApprovalDirective directive = toolExecutionPolicyService.EvaluateApproval(new ToolApprovalRequest(
                    Identity: new ToolIdentity(
                        Category: ToolPolicyCategory.Connector,
                        ToolName: tool.Name,
                        SourceId: serverId),
                    ApprovalPolicy: policy));
                if (!directive.Include)
                    continue;

                wrappedTools.Add(directive.RequiresApproval ? new ApprovalRequiredAIFunction(tool) : tool);
            }
        }

        return wrappedTools;
    }

    internal static bool MatchesGlob(string toolName, string pattern)
        => ToolExecutionPolicyService.MatchesGlobToolName(toolName, pattern);
}
