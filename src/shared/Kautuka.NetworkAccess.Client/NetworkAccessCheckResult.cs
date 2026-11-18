namespace Kautuka.NetworkAccess.Client;

public sealed record NetworkAccessCheckResult(
    bool Allowed,
    string Reason,
    string? RequestId = null,
    bool OpenInternetBadge = false,
    NetworkAccessDenialKind Kind = NetworkAccessDenialKind.Denied)
{
    public static NetworkAccessCheckResult CreateAllowed(string reason, string? requestId = null, bool openInternetBadge = false)
        => new(true, reason, requestId, openInternetBadge, NetworkAccessDenialKind.Denied);

    public static NetworkAccessCheckResult CreateDenied(string reason, string? requestId = null, bool openInternetBadge = false)
        => new(false, reason, requestId, openInternetBadge, NetworkAccessDenialKind.Denied);

    public static NetworkAccessCheckResult CreateUnavailable(string reason = "controller_unavailable")
        => new(false, reason, null, false, NetworkAccessDenialKind.Unavailable);

    public static NetworkAccessCheckResult CreatePaused(string reason, string? requestId = null)
        => new(false, reason, requestId, false, NetworkAccessDenialKind.Paused);

    public void ThrowIfDenied(OutboundTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (Allowed)
        {
            return;
        }

        throw new NetworkAccessDeniedException(
            destination: target.Host,
            port: target.Port,
            scheme: target.Scheme,
            kind: Kind,
            reason: Reason,
            requestId: RequestId,
            userId: target.UserId,
            agentId: target.AgentId,
            connectorId: target.ConnectorId,
            service: target.Service,
            purpose: target.Purpose);
    }
}
