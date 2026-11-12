using System.Diagnostics;

using Grpc.Core;

using Kautuka.Manager.Protos;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.NetworkAccess.Client;

/// <summary>
/// Evaluates outbound network access by calling Kautuka.Manager's CheckOutbound gRPC RPC.
/// Enforces fail-closed semantics with a strict 2-second deadline.
/// </summary>
public sealed partial class GrpcNetworkAccessChecker(
    Func<ManagerService.ManagerServiceClient> clientAccessor,
    ILogger<GrpcNetworkAccessChecker>? logger = null) : INetworkAccessChecker
{
    private static readonly TimeSpan deadlineTimeout = TimeSpan.FromSeconds(2);
    private readonly ILogger logger = logger ?? NullLogger<GrpcNetworkAccessChecker>.Instance;

    public GrpcNetworkAccessChecker(ManagerService.ManagerServiceClient client, ILogger<GrpcNetworkAccessChecker>? logger = null)
        : this(() => client, logger)
    {
    }

    public async Task<NetworkAccessCheckResult> CheckAsync(OutboundTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);

        // Explicit userId per v7-6: "ALWAYS pass an explicit UserId ('system' only when legitimately system-owned, or actual user ID)"
        // Note: ChannelHost is pinned by certificate identity to system/channelhost in Manager and requires empty strings.
        string effectiveUserId;
        string effectiveAgentId;
        bool effectiveSystemwide = target.Systemwide;

        if (string.Equals(target.ExecutionContext, "channelhost", StringComparison.OrdinalIgnoreCase))
        {
            effectiveUserId = string.Empty;
            effectiveAgentId = string.Empty;
            effectiveSystemwide = false;
        }
        else
        {
            effectiveUserId = target.UserId?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(effectiveUserId))
            {
                // Fail closed when a non-ChannelHost caller lacks an explicit user context (v7-6);
                // never default to "system".
                return NetworkAccessCheckResult.CreateDenied("unknown_source");
            }

            effectiveAgentId = !string.IsNullOrWhiteSpace(target.AgentId)
                ? target.AgentId.Trim()
                : (effectiveUserId == "system" ? "gateway" : string.Empty);
        }

        var request = new NetworkAccessCheckOutboundRequest
        {
            UserId = effectiveUserId,
            AgentId = effectiveAgentId,
            ConnectorId = target.ConnectorId ?? string.Empty,
            Systemwide = effectiveSystemwide,
            Host = target.Host,
            Port = target.Port,
            Purpose = target.Purpose ?? string.Empty,
            CorrelationId = target.CorrelationId ?? Activity.Current?.Id ?? Guid.NewGuid().ToString("N"),
        };

        try
        {
            ManagerService.ManagerServiceClient client = clientAccessor();
            DateTime deadline = DateTime.UtcNow.Add(deadlineTimeout);

            NetworkAccessCheckOutboundResponse response = await client.CheckOutboundAsync(
                request,
                deadline: deadline,
                cancellationToken: cancellationToken).ResponseAsync.ConfigureAwait(false);

            if (response.Allowed)
            {
                return NetworkAccessCheckResult.CreateAllowed(
                    response.Reason,
                    response.RequestId,
                    response.OpenInternetBadge);
            }

            string reason = string.IsNullOrWhiteSpace(response.Reason) ? "denied" : response.Reason;

            // Brake check mapping
            if (reason is "brake_user" or "brake_system")
            {
                return NetworkAccessCheckResult.CreatePaused(reason, response.RequestId);
            }

            // Controller unavailable returned as reason
            if (reason is "controller_unavailable" or "unavailable")
            {
                return NetworkAccessCheckResult.CreateUnavailable(reason);
            }

            return NetworkAccessCheckResult.CreateDenied(
                reason,
                response.RequestId,
                response.OpenInternetBadge);
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.Cancelled)
        {
            LogGrpcUnavailable(logger, ex.Status.ToString(), ex);
            return NetworkAccessCheckResult.CreateUnavailable("controller_unavailable");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogGrpcFailedClosed(logger, ex.Message, ex);
            return NetworkAccessCheckResult.CreateUnavailable("controller_unavailable");
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Manager CheckOutbound gRPC returned unavailable/deadline: {Status}")]
    private static partial void LogGrpcUnavailable(ILogger logger, string status, Exception ex);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Manager CheckOutbound gRPC failed closed: {Message}")]
    private static partial void LogGrpcFailedClosed(ILogger logger, string message, Exception ex);
}
