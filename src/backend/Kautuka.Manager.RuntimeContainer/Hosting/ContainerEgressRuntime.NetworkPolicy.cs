using Kautuka.Manager.RuntimeContainer.Configuration;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

public sealed record NetworkAccessPolicyView(NetworkAccessPolicyState Policy, long ActiveRevision,
    string? ActiveVersion, string ActivationState, string? LastError,
    IReadOnlyList<NetworkAccessIdentity> Sources, IReadOnlyList<ContainerEgressGrant> Defaults, bool RequestCapacityReached = false);

public sealed partial class ContainerEgressRuntime
{
    private readonly NetworkAccessPolicyStore networkPolicy = new(Path.Combine(stateRoot, "egress", "policy", "state.json"));
    private long activePolicyRevision = -1;
    private string? policyActivationError;
    private NetworkAccessIdentity[] policySources = [];
    private ContainerEgressGrant[] policyDefaults = [.. options.Grants];

    public NetworkAccessPolicyView GetNetworkAccessPolicy(NetworkAccessActor actor)
    {
        NetworkAccessPolicyState policy = networkPolicy.Read(actor);
        long accepted = Interlocked.Read(ref activePolicyRevision);
        string? error = Volatile.Read(ref policyActivationError);
        if (recoveryRequiresProxyStop) error ??= "Network policy cannot be confirmed. Runtime recovery is required.";
        return new NetworkAccessPolicyView(policy, accepted, active?.Version,
            !Enabled ? "disabled" : error is not null ? "failed" : !ready || accepted != policy.Revision ? "pending" : "active",
            error, [.. policySources.Where(identity => NetworkAccessPolicyStore.CanAccess(actor, identity))],
            [.. policyDefaults.Where(grant => NetworkAccessPolicyStore.CanAccess(actor, NetworkAccessPolicyStore.Identity(grant)))], networkPolicy.RequestCapacityReached);
    }

    public Task<NetworkAccessPolicyView> SetNetworkAccessRuleAsync(NetworkAccessActor actor, NetworkAccessIdentity identity,
        string destination, int port, bool allowed, long expectedRevision, CancellationToken cancellationToken)
        => ChangeNetworkPolicyAsync(actor, () => networkPolicy.SetRule(actor, identity, destination, port, allowed, expectedRevision), cancellationToken);

    public Task<NetworkAccessPolicyView> DecideNetworkAccessRequestAsync(NetworkAccessActor actor, string requestId,
        bool allowed, long expectedRevision, CancellationToken cancellationToken)
        => ChangeNetworkPolicyAsync(actor, () => networkPolicy.DecideRequest(actor, requestId, allowed, expectedRevision), cancellationToken);

    private async Task<NetworkAccessPolicyView> ChangeNetworkPolicyAsync(NetworkAccessActor actor, Action change,
        CancellationToken cancellationToken)
    {
        if (!Enabled) throw new InvalidOperationException("Managed network access is not enabled on this installation.");
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Persistence precedes publication. Pending/failed is visible until
            // the existing CDS/LDS acknowledgement checks accept this revision.
            change();
            policyActivationError = null;
            if (ready)
            {
                try { await PublishAsync(CompileCurrent(), cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    policyActivationError = recoveryRequiresProxyStop
                        ? "Network policy could not be confirmed. Runtime recovery is required."
                        : "The proxy did not accept the new policy. The last accepted policy remains in effect.";
                }
            }
            return GetNetworkAccessPolicy(actor);
        }
        finally { gate.Release(); }
    }
}
