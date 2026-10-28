using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Domain.Execution;

internal static class ApprovalRootRunIdResolver
{
    public static string? Resolve(
        GatewayToolApprovalRequest request,
        IAgentToolInvocationLinkStore invocationLinks,
        IAgentDefinitionStore agentDefinitionStore)
    {
        AgentToolInvocationLink? link = invocationLinks.GetByApprovalRequestId(request.Id);
        if (!string.IsNullOrWhiteSpace(link?.RootRunId))
        {
            return link.RootRunId;
        }

        if (!string.IsNullOrWhiteSpace(request.OuterSessionId)
            && !string.IsNullOrWhiteSpace(request.ToolCallId))
        {
            link = invocationLinks.GetByCallerSessionAndToolCallId(request.OuterSessionId, request.ToolCallId);
            if (!string.IsNullOrWhiteSpace(link?.RootRunId))
            {
                return link.RootRunId;
            }
        }

        if (!string.IsNullOrWhiteSpace(request.AgentId)
            && !string.IsNullOrWhiteSpace(request.RunId))
        {
            string storageLocatorKey = agentDefinitionStore.GetStorageLocatorKey(request.AgentId);
            string? fromHistory = agentDefinitionStore.FindNaradRootRunIdForRun(storageLocatorKey, request.RunId);
            if (!string.IsNullOrWhiteSpace(fromHistory))
            {
                return fromHistory;
            }
        }

        if (!string.IsNullOrWhiteSpace(request.OuterSessionId))
        {
            string? ambientRoot = AgentToolInvocationContext.Current?.RootRunId;
            if (!string.IsNullOrWhiteSpace(ambientRoot))
            {
                return ambientRoot;
            }
        }

        return request.RunId;
    }

    public static bool BelongsToRootScope(
        GatewayToolApprovalRequest request,
        string? rootRunId,
        IAgentToolInvocationLinkStore invocationLinks,
        IAgentDefinitionStore agentDefinitionStore)
    {
        if (string.IsNullOrWhiteSpace(rootRunId))
        {
            return true;
        }

        string? canonicalRootRunId = Resolve(request, invocationLinks, agentDefinitionStore);
        return string.Equals(canonicalRootRunId, rootRunId, StringComparison.Ordinal)
            || string.Equals(request.RunId, rootRunId, StringComparison.Ordinal);
    }
}
