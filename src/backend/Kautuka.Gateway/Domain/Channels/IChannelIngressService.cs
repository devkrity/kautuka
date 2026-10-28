using System.Text.Json;

namespace Kautuka.Gateway.Domain.Channels;

internal interface IChannelIngressService
{
    Task<ChannelIngressResult> ProcessInboundEventAsync(ChannelInboundEvent evt, CancellationToken cancellationToken);

    // Transport admission returns after persisting prompts, independently of execution.
    Task<ChannelIngressResult> AdmitInboundEventAsync(ChannelInboundEvent evt, CancellationToken cancellationToken)
        => ProcessInboundEventAsync(evt, cancellationToken);

    Task<ChannelInteractionDispatchResult> DispatchChannelInteractionAsync(string registrationId, JsonElement payload, CancellationToken cancellationToken);

    Task SyncChannelApprovalResolutionAsync(ChannelApprovalResolution resolution, CancellationToken cancellationToken);

    Task NotifyChannelApprovalAsync(
        string userId,
        string sessionId,
        string? targetType,
        string? targetId,
        GatewayChatApprovalRequest approvalReq,
        CancellationToken cancellationToken);

    Task NotifyChannelContinuationAsync(string requestId, string continuationText, CancellationToken cancellationToken);
}

internal sealed record ChannelApprovalResolution(
    string RequestId,
    string Action,
    string? DecidedByUserId);

internal sealed record ChannelIngressResult(
    bool Success,
    string Status,
    string Detail,
    string? SessionId = null,
    string? TargetType = null,
    string? TargetId = null,
    string? InteractionId = null,
    string? RunId = null);

internal sealed record ChannelInteractionDispatchResult(
    bool Success,
    string Status,
    string Detail,
    string? InteractionId = null,
    string? RunId = null);
