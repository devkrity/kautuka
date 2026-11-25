using Kautuka.NetworkAccess.Client;

namespace Kautuka.Gateway.Infrastructure.Egress;

internal sealed class GatewayOutboundApproval(INetworkAccessChecker checker, bool enabled)
{
    internal bool Enabled => enabled;

    internal async ValueTask WaitAsync(Uri destination, CancellationToken cancellationToken)
    {
        NetworkAccessScope? scope = NetworkAccessContext.Current;
        if (!enabled || scope?.SkipApprovalHold == true
            || scope?.Purpose is "provider-test" or "probe" or "ai-probe" or "save-discovery")
            return;

        var target = new OutboundTarget(destination.IdnHost, destination.Port, destination.Scheme,
            Service: scope?.Service ?? "http", Purpose: scope?.Purpose ?? "http-request",
            CorrelationId: scope?.CorrelationId).MergeContext(scope);
        NetworkAccessCheckResult result = await checker.CheckAsync(target, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        result.ThrowIfDenied(target);
    }

    // Policy still runs at send time. A new denial must not wait inside the
    // provider's HTTP budget or let an already signed request grow stale.
    internal IDisposable? BeginProviderRequest()
        => enabled && NetworkAccessContext.Current is { } scope
            ? NetworkAccessContext.BeginScope(scope with { SkipApprovalHold = true })
            : null;
}
