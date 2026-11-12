using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Manager.Protos;
using Kautuka.NetworkAccess.Client;

namespace Kautuka.Gateway.Domain.Execution;

/// <summary>
/// Resolves network-access failure details when an agent tool execution fails without
/// elevating privileges or exposing another user's pending destinations.
/// </summary>
internal static class ChatToolNetworkAccessHelper
{
    public static NetworkAccessDeniedException? FindNetworkAccessDeniedException(Exception ex)
    {
        return ex as NetworkAccessDeniedException
            ?? ex.InnerException as NetworkAccessDeniedException
            ?? (ex is AggregateException agg ? agg.InnerExceptions.OfType<NetworkAccessDeniedException>().FirstOrDefault() : null);
    }

    public static async Task<JsonElement> BuildToolCallFinishedPayloadAsync(
        Exception ex,
        NetworkAccessDeniedException? nacEx,
        string activeToolCallId,
        string? activeToolName,
        string? userId,
        string? agentId,
        ManagerService.ManagerServiceClient? managerClient)
    {
        Func<NetworkAccessListRequestsRequest, Task<NetworkAccessListRequestsResponse>>? listAsync = managerClient is null
            ? null
            : req => managerClient.ListRequestsAsync(
                req,
                deadline: DateTime.UtcNow.AddSeconds(1),
                cancellationToken: CancellationToken.None).ResponseAsync;

        return await BuildToolCallFinishedPayloadAsync(
            ex,
            nacEx,
            activeToolCallId,
            activeToolName,
            userId,
            agentId,
            listAsync).ConfigureAwait(false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Best-effort pending request enrichment must never mask the primary tool failure.")]
    internal static async Task<JsonElement> BuildToolCallFinishedPayloadAsync(
        Exception ex,
        NetworkAccessDeniedException? nacEx,
        string activeToolCallId,
        string? activeToolName,
        string? userId,
        string? agentId,
        Func<NetworkAccessListRequestsRequest, Task<NetworkAccessListRequestsResponse>>? listRequestsAsync)
    {
        string code = nacEx?.NaradReason ?? "tool_execution_failed";
        string? destination = nacEx?.Destination;
        int? port = nacEx?.Port;
        string? requestId = nacEx?.RequestId;
        string? approvalUrl = nacEx is not null ? "/network-access" : null;

        if (nacEx is null
            && listRequestsAsync is not null
            && !string.IsNullOrWhiteSpace(userId)
            && !string.IsNullOrWhiteSpace(agentId))
        {
            try
            {
                NetworkAccessListRequestsResponse listResp = await listRequestsAsync(
                    new NetworkAccessListRequestsRequest
                    {
                        Actor = new NetworkAccessActorMessage
                        {
                            UserId = userId,
                            IsAdmin = false,
                        },
                    }).ConfigureAwait(false);

                NetworkAccessRequestMessage? recent = listResp.Requests.FirstOrDefault(r =>
                    string.Equals(r.Status, "pending", StringComparison.Ordinal)
                    && !r.Systemwide
                    && !string.IsNullOrWhiteSpace(r.UserId)
                    && string.Equals(r.UserId, userId, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(r.AgentId)
                    && string.Equals(r.AgentId, agentId, StringComparison.OrdinalIgnoreCase));

                if (recent is not null)
                {
                    code = "network_access_required";
                    destination = recent.Destination;
                    port = recent.Port;
                    requestId = recent.Id;
                    approvalUrl = "/network-access";
                }
            }
            catch
            {
                // Ignore best-effort lookup errors
            }
        }

        var toolFinishedPayload = new JsonObject
        {
            ["toolCallId"] = activeToolCallId,
            ["toolName"] = activeToolName,
            ["response"] = null,
            ["outcome"] = "failed",
            ["code"] = code,
            ["message"] = ex.Message,
        };
        if (!string.IsNullOrWhiteSpace(destination))
        {
            toolFinishedPayload["destination"] = destination;
            toolFinishedPayload["reason"] = code;
        }
        if (port.HasValue)
        {
            toolFinishedPayload["port"] = port.Value;
        }
        if (!string.IsNullOrWhiteSpace(requestId))
        {
            toolFinishedPayload["requestId"] = requestId;
        }
        if (!string.IsNullOrWhiteSpace(approvalUrl))
        {
            toolFinishedPayload["approvalUrl"] = approvalUrl;
        }

        return JsonSerializer.SerializeToElement(toolFinishedPayload, KautukaJsonContext.Default.JsonObject);
    }
}
