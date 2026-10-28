using Kautuka.Gateway.Domain.Channels;
using Kautuka.Gateway.Domain.Stores;

using Mediator;

namespace Kautuka.Gateway.Domain.Hitl;

/// <summary>
/// Stamps <c>channel_tether_mode</c> on the approval record for audit/UI only.
/// ChannelHost interprets <c>hitl_tether_mode</c> for delivery; this handler must not gate outbound fan-out.
/// </summary>
internal sealed class ApprovalTetherModeAssignmentHandler(
    IGatewayApprovalStore approvalStore,
    IChannelAttachmentRegistry attachments) : INotificationHandler<HitlApprovalRequested>
{
    public async ValueTask Handle(HitlApprovalRequested notification, CancellationToken cancellationToken)
    {
        GatewayToolApprovalRequest? request = approvalStore.GetById(notification.RequestId);
        if (request is null)
        {
            return;
        }

        string? userId = notification.UserId;
        if (!string.IsNullOrWhiteSpace(userId))
        {
            await attachments.RefreshUserAsync(userId, cancellationToken).ConfigureAwait(false);
        }

        ChannelHitlTetherMode effectiveMode = string.IsNullOrWhiteSpace(userId)
            ? ChannelHitlTetherMode.Mirror
            : attachments.GetChannelHitlTetherMode(userId, notification.SessionId);

        approvalStore.SetChannelTetherMode(notification.RequestId, effectiveMode);
    }
}
