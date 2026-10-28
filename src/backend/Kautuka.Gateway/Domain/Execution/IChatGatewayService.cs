using System.Text.Json;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Execution;

internal interface IChatGatewayService
{
    IAsyncEnumerable<NaradPersistedEvent> StreamChatAsync(
        JsonElement body,
        AgentRequestContext? requestContext = null,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<NaradPersistedEvent> StreamChatWithApprovalAsync(
        string targetType,
        string targetId,
        string sessionId,
        ToolApprovalResponseInfo approvalResponse,
        string? userId,
        Func<NaradPersistedEvent, CancellationToken, ValueTask>? beforeWaitTerminal = null,
        CancellationToken cancellationToken = default);

    void PersistRun(string sessionId, string userMessage, GatewayChatResult result, IReadOnlyList<string>? userAssetIds = null, string? userId = null, string? runId = null, string? sourceChannel = null);

    void RecordUserTurnStart(string sessionId, string userMessage, IReadOnlyList<string>? userAssetIds = null, string? userId = null, string? sourceChannel = null);

    void PersistAssistantRunResult(string sessionId, GatewayChatResult result, string? userId = null);
}
