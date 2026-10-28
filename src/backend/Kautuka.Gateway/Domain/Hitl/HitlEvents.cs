using Mediator;

namespace Kautuka.Gateway.Domain.Hitl;

internal enum HitlResolutionSource
{
    Browser = 0,
    ApprovalApi = 1,
    ChannelCallback = 2,
    SystemTimeout = 3,
}

internal sealed record HitlApprovalRequested(
    string RequestId,
    string ToolCallId,
    string ToolName,
    string? ArgumentsJson,
    string SessionId,
    string TargetType,
    string TargetId,
    string? UserId,
    string? RunId = null,
    string ToolType = "mcp",
    string? OuterSessionId = null,
    string? OuterAgentId = null,
    string? ExpiresAt = null) : INotification;

internal sealed record HitlApprovalResolved(
    string RequestId,
    bool Approved,
    string? Reason,
    string? DecidedByUserId,
    HitlResolutionSource Source,
    string? SessionId = null,
    string? TargetType = null,
    string? TargetId = null,
    string? UserId = null,
    bool StopRun = false,
    bool TrustThisRun = false) : INotification;

internal sealed record HitlContinuationCompleted(
    string RequestId,
    string SessionId,
    string TargetType,
    string TargetId,
    string? UserId,
    bool Approved,
    HitlResolutionSource Source,
    bool HasNestedApprovalRequest,
    string OutputText) : INotification;

internal sealed record HitlContinuationFailed(
    string RequestId,
    string SessionId,
    string TargetType,
    string TargetId,
    string? UserId,
    bool Approved,
    string Error) : INotification;
