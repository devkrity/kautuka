namespace Kautuka.Gateway.Domain.Execution;

using Kautuka.Gateway.Domain.AgentFiles;

using Narad.Contracts;

/// <summary>
/// Carries the user's approve/deny decision for a pending tool call approval request.
/// </summary>
internal sealed record ToolApprovalResponseInfo(
    string RequestId,
    string ToolCallId,
    string ToolName,
    string? ArgumentsJson,
    bool Approved,
    string? Reason,
    string? RunId = null,
    string? CheckpointId = null,
    bool StopRun = false,
    string? ResultJson = null,
    string? ContinuationRunId = null,
    string? RootRunId = null,
    string? TerminalOutcomeScope = null,
    bool TrustThisRun = false,
    bool Expired = false,
    bool ToolLifecycleAlreadyJournaled = false,
    IReadOnlyList<ToolApprovalResponseInfo>? PackedSiblingResponses = null)
{
    public IReadOnlyList<ToolApprovalResponseInfo> EnumeratePack()
    {
        if (PackedSiblingResponses is not { Count: > 0 })
        {
            return [this];
        }

        var pack = new List<ToolApprovalResponseInfo>(PackedSiblingResponses.Count + 1) { this };
        var seen = new HashSet<string>(StringComparer.Ordinal) { RequestId };
        foreach (ToolApprovalResponseInfo sibling in PackedSiblingResponses)
        {
            if (seen.Add(sibling.RequestId))
            {
                pack.Add(sibling);
            }
        }

        return pack;
    }
}

internal interface IAgentChatService
{
    IAsyncEnumerable<NaradPersistedEvent> StreamAsync(
        string agentId,
        string sessionId,
        string? userId,
        string runTrigger,
        string message,
        string? stageTransitionId,
        IReadOnlyList<GatewayConversationAsset> inputAssets,
        IReadOnlyList<AgentWorkspaceFileReference> workspaceFiles,
        IReadOnlyList<InjectedMemoryFact> memoryFacts,
        ToolApprovalResponseInfo? approvalResponse,
        AgentContinuationRequest? continuationRequest,
        AgentRequestContext? requestContext = null,
        CancellationToken ct = default);
}
