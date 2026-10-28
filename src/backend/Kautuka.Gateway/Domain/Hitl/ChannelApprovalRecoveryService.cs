using Kautuka.Gateway.Domain.Channels;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Domain.Workflows;

using Mediator;

namespace Kautuka.Gateway.Domain.Hitl;

/// <summary>
/// Runs after startup and every two minutes to re-fire <see cref="HitlApprovalRequested"/> for any pending
/// approval that has no corresponding pending channel interaction. This recovers
/// approvals that were persisted to the database but whose mediator notification
/// was dropped when the SSE stream was disconnected before the event was consumed,
/// and reconciles decided-but-unsynced channel cards.
/// </summary>
internal sealed class ChannelApprovalRecoveryService(
    IGatewayApprovalStore approvalStore,
    GatewayState gatewayState,
    ChannelIngressService channelIngress,
    IMediator mediator,
    ILogger<ChannelApprovalRecoveryService> logger) : BackgroundService
{
    // Small delay to allow GatewayState (channel interactions) to finish loading
    // from disk before we check which approvals are missing interactions.
    private static readonly TimeSpan startupDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan recoverInterval = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(startupDelay, stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(recoverInterval);
            do
            {
                try
                {
                    await RecoverResolvedChannelApprovalsAsync(stoppingToken).ConfigureAwait(false);
                    await RecoverOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException exception)
                {
                    ChannelApprovalRecoveryServiceLog.RecoveryCycleFailed(logger, exception);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    ChannelApprovalRecoveryServiceLog.RecoveryCycleFailed(logger, exception);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal async Task RecoverOnceAsync(CancellationToken stoppingToken)
    {
        IReadOnlyList<GatewayToolApprovalRequest> pending = approvalStore.ListPending(null, adminAll: true);
        if (pending.Count == 0)
        {
            return;
        }

        // Only a delivered channel card covers the approval. A pending interaction
        // with no transport id means notify created local state but Telegram never sent.
        var coveredByDeliveredInteraction = new HashSet<string>(
            gatewayState.ChannelInteractions.Values
                .Where(i => string.Equals(i.Status, "pending", StringComparison.Ordinal)
                            && !string.IsNullOrWhiteSpace(i.ApprovalRequestId)
                            && ChannelIngressService.HasDeliveredChannelApproval(i))
                .Select(i => i.ApprovalRequestId!),
            StringComparer.Ordinal);

        foreach (GatewayToolApprovalRequest request in pending)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            if (coveredByDeliveredInteraction.Contains(request.Id))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(request.UserId))
            {
                ChannelApprovalRecoveryServiceLog.SkippingRecoveryNoUserId(logger, request.Id);
                continue;
            }

            ChannelApprovalRecoveryServiceLog.RecoveringApproval(logger, request.Id, request.ToolName, request.UserId);

            try
            {
                bool workflowReview = string.Equals(request.Kind, "workflow_review", StringComparison.Ordinal)
                    || string.Equals(request.ToolType, "workflow_review", StringComparison.Ordinal);
                GatewayWorkflowInstance? workflowInstance = workflowReview
                    ? channelIngress.WorkflowStore.GetInstance(request.InstanceId ?? string.Empty)
                    : null;
                string targetType = "agent";
                string targetId = workflowReview
                    ? workflowInstance?.Snapshot.Nodes.FirstOrDefault(node => string.Equals(node.NodeId, request.NodeId, StringComparison.Ordinal))?.AgentId ?? string.Empty
                    : request.AgentId ?? string.Empty;

                await mediator.Publish(new HitlApprovalRequested(
                    request.Id,
                    request.ToolCallId,
                    request.ToolName,
                    request.ArgumentsJson,
                    request.SessionId ?? string.Empty,
                    targetType,
                    targetId,
                    request.UserId,
                    RunId: request.RunId,
                    ToolType: request.ToolType ?? "mcp",
                    OuterSessionId: request.OuterSessionId,
                    OuterAgentId: request.OuterAgentId,
                    ExpiresAt: request.ExpiresAt),
                    stoppingToken).ConfigureAwait(false);

                ChannelApprovalRecoveryServiceLog.RecoveredApproval(logger, request.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ChannelApprovalRecoveryServiceLog.RecoveryFailed(logger, request.Id, ex);
            }
        }
    }

    /// <summary>
    /// Resolution notifications are in-process, while approval decisions and channel
    /// interactions are durable. Reconcile a decision if Gateway stopped or a handler
    /// failed after the decision was committed but before channel edits were enqueued.
    /// </summary>
    internal async Task RecoverResolvedChannelApprovalsAsync(CancellationToken stoppingToken)
    {
        GatewayChannelInteraction[] unresolved =
        [
            .. gatewayState.ChannelInteractions.Values.Where(interaction =>
                string.Equals(interaction.Status, "pending", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(interaction.ApprovalRequestId)
                && ChannelIngressService.HasDeliveredChannelApproval(interaction)),
        ];

        foreach (GatewayChannelInteraction interaction in unresolved)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            GatewayToolApprovalRequest? approval = approvalStore.GetById(interaction.ApprovalRequestId!);
            if (approval?.Decision is not { Length: > 0 })
            {
                continue;
            }

            try
            {
                await channelIngress.SyncResolvedChannelApprovalAfterDeliveryAsync(
                    interaction.Id,
                    stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ChannelApprovalRecoveryServiceLog.ResolvedApprovalRecoveryFailed(
                    logger,
                    interaction.ApprovalRequestId!,
                    interaction.Id,
                    ex);
            }
        }
    }
}

internal static partial class ChannelApprovalRecoveryServiceLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Recovering orphaned channel approval for request {RequestId} tool={ToolName} userId={UserId}")]
    public static partial void RecoveringApproval(ILogger logger, string requestId, string toolName, string userId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Channel approval recovery completed for request {RequestId}")]
    public static partial void RecoveredApproval(ILogger logger, string requestId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Skipping recovery for request {RequestId}; UserId is missing")]
    public static partial void SkippingRecoveryNoUserId(ILogger logger, string requestId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Channel approval recovery failed for request {RequestId}")]
    public static partial void RecoveryFailed(ILogger logger, string requestId, Exception ex);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Resolved channel approval recovery failed for request {RequestId}, interaction {InteractionId}")]
    public static partial void ResolvedApprovalRecoveryFailed(ILogger logger, string requestId, string interactionId, Exception ex);

    [LoggerMessage(EventId = 6, Level = LogLevel.Error, Message = "Channel approval recovery cycle failed")]
    public static partial void RecoveryCycleFailed(ILogger logger, Exception ex);
}
