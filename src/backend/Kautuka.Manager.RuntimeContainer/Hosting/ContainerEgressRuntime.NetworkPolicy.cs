using Kautuka.Manager.RuntimeContainer.Configuration;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

public sealed record NetworkAccessPolicyView(NetworkAccessPolicyState Policy, long ActiveRevision,
    string? ActiveVersion, string ActivationState, string? LastError,
    IReadOnlyList<NetworkAccessIdentity> Sources, IReadOnlyList<ContainerEgressGrant> Defaults, bool RequestCapacityReached = false,
    int MaxPrivateNetworks = 0, int ActivePrivateNetworks = -1, string? DnsRefreshError = null);

public sealed partial class ContainerEgressRuntime
{
    private readonly NetworkAccessPolicyStore networkPolicy = new(Path.Combine(stateRoot, "egress", "policy", "state.json"));
    private long activePolicyRevision = -1;
    private string? policyActivationError;
    private NetworkAccessIdentity[] policySources = [];
    private ContainerEgressGrant[] policyDefaults = [.. options.Grants, .. HostedProviderDefaults()];
    private static readonly string[] hostedProviderHosts = ["api.openai.com", "api.anthropic.com", "api.groq.com",
        "api.mistral.ai", "openrouter.ai", "generativelanguage.googleapis.com"];

    // Only the fixed hosted-provider endpoints shipped in AiProviderCatalog.
    // The integration contract test cross-checks that catalog; custom/tenant URLs
    // and local providers still require an explicit administrator decision.
    internal static IReadOnlyList<ContainerEgressGrant> HostedProviderDefaults()
        => [.. hostedProviderHosts.Select(static host => new ContainerEgressGrant
            {
                ScopeType = "core", UserId = "system", AgentId = "gateway", Role = "core",
                ComponentId = "gateway", Destination = host, Port = 443,
            })];

    public NetworkAccessPolicyView GetNetworkAccessPolicy(NetworkAccessActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        NetworkAccessPolicyState policy = networkPolicy.Read(actor);
        long accepted = Interlocked.Read(ref activePolicyRevision);
        string? error = Volatile.Read(ref policyActivationError);
        if (recoveryRequiresProxyStop) error ??= "Network policy cannot be confirmed. Runtime recovery is required.";
        return new NetworkAccessPolicyView(policy, accepted, active?.Version,
            !Enabled ? "disabled" : error is not null ? "failed" : !ready || accepted != policy.Revision ? "pending" : "active",
            error, [.. policySources.Where(identity => NetworkAccessPolicyStore.CanAccess(actor, identity))],
            [.. policyDefaults.Where(grant => NetworkAccessPolicyStore.CanAccess(actor, NetworkAccessPolicyStore.Identity(grant)))], networkPolicy.RequestCapacityReached,
            actor.IsAdmin && Enabled ? options.MaxPrivateNetworks : 0,
            actor.IsAdmin && Enabled && Volatile.Read(ref ready) ? Volatile.Read(ref activePrivateNetworks) : -1,
            DnsErrorForActor(DnsRefreshError, actor.IsAdmin));
    }

    internal static string? DnsErrorForActor(string? error, bool isAdmin)
        => error is null ? null : isAdmin ? error
            : "DNS refresh needs attention. Name resolution may be impaired; an administrator should check the appliance resolver.";

    public Task<NetworkAccessPolicyView> SetNetworkAccessRuleAsync(NetworkAccessActor actor, NetworkAccessIdentity identity,
        string destination, int port, bool allowed, long expectedRevision, CancellationToken cancellationToken, bool allowPrivateNetwork = false)
        => ChangeNetworkPolicyAsync(actor, () => networkPolicy.SetRule(actor, identity, destination, port, allowed, expectedRevision, allowPrivateNetwork), cancellationToken);

    public Task<NetworkAccessPolicyView> DecideNetworkAccessRequestAsync(NetworkAccessActor actor, string requestId,
        bool allowed, long expectedRevision, CancellationToken cancellationToken, bool allowPrivateNetwork = false)
        => ChangeNetworkPolicyAsync(actor, () => networkPolicy.DecideRequest(actor, requestId, allowed, expectedRevision, allowPrivateNetwork), cancellationToken);

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
