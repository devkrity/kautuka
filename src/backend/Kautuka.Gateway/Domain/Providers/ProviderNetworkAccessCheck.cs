using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

using Grpc.Core;

using Kautuka.Manager.Protos;

namespace Kautuka.Gateway.Domain.Providers;

internal static class ProviderNetworkAccessCheck
{
    internal static async Task<GatewayProviderTestResult?> CheckAsync(
        Func<NetworkAccessReadRequest, CancellationToken, Task<NetworkAccessPolicyResponse>> readPolicy, JsonObject settings, CancellationToken ct)
    {
        if (!Uri.TryCreate(settings["base_url"]?.GetValue<string>(), UriKind.Absolute, out Uri? target)
            || target.Scheme is not ("http" or "https")) return null;
        try
        {
            var request = new NetworkAccessReadRequest { Actor = new NetworkAccessActorMessage { UserId = "gateway-provider-validation", IsAdmin = true } };
            NetworkAccessPolicyResponse policy = await readPolicy(request, ct).ConfigureAwait(false);
            if (!policy.Enabled) return null;
            if (target.IsLoopback)
                return new(false, "This address points to localhost inside the Gateway container, not your computer. Use a host address reachable from the development appliance for Ollama, then test again. Allowing network access cannot redirect localhost to your computer.");
            // Every page must describe the same accepted policy; incomplete or
            // pending data is not evidence that a connectivity failure is a deny.
            for (int pages = 0; policy.NextOffset != 0; pages++)
            {
                if (pages >= 220 || policy.NextOffset <= request.Offset) return null;
                request.Offset = policy.NextOffset;
                request.EnforceRevision = true;
                request.ExpectedRevision = policy.Revision;
                NetworkAccessPolicyResponse next = await readPolicy(request, ct).ConfigureAwait(false);
                if (next.Revision != policy.Revision || next.ActiveRevision != policy.ActiveRevision
                    || next.ActiveVersion != policy.ActiveVersion || next.ActivationState != policy.ActivationState) return null;
                policy.Rules.Add(next.Rules);
                policy.Defaults.Add(next.Defaults);
                policy.Sources.Add(next.Sources);
                policy.NextOffset = next.NextOffset;
            }
            string destination = target.IdnHost.TrimEnd('.');
            if (target.Scheme == Uri.UriSchemeHttp && !IPAddress.TryParse(destination, out _))
            {
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(target.DnsSafeHost, AddressFamily.InterNetwork, ct).ConfigureAwait(false);
                IPAddress[] unique = [.. addresses.Distinct()];
                if (unique.Length != 1) return null;
                destination = unique[0].ToString();
            }
            return Evaluate(policy, destination, target.Port);
        }
        catch (RpcException) { return null; }
        catch (SocketException) { return null; }
        catch (NotSupportedException) { return null; }
    }

    internal static GatewayProviderTestResult? Evaluate(NetworkAccessPolicyResponse policy, string destination, int port)
    {
        if (!policy.Enabled || policy.ActivationState != "active" || policy.Revision != policy.ActiveRevision
            || string.IsNullOrEmpty(policy.ActiveVersion) || !policy.Sources.Any(IsGateway)) return null;
        if (KautukaNetworkDestinationPolicy.IsMetadata(destination) || destination == KautukaNetworkDestinationPolicy.PlatformControlAddress
            || destination == KautukaNetworkDestinationPolicy.PrivateSourceAddress)
            return new(false, "This is a protected metadata, platform-control or reserved proxy address. It cannot be allowed for a provider, including by an administrator. Use the provider's normal service endpoint.");
        var overrides = policy.Rules.Where(rule => IsGateway(rule.Identity) && rule.Port == port).ToArray();
        var effective = policy.Defaults.Where(rule => IsGateway(rule.Identity) && rule.Port == port
            && !overrides.Any(other => string.Equals(other.Destination, rule.Destination, StringComparison.OrdinalIgnoreCase)))
            .Concat(overrides.Where(static rule => rule.Allowed))
            .Where(rule => !KautukaNetworkDestinationPolicy.IsNonPublic(rule.Destination) || rule.AllowPrivateNetwork).ToArray();
        if (effective.Any(rule => string.Equals(rule.Destination, destination, StringComparison.OrdinalIgnoreCase))) return null;
        // TLS hostname matching can fall back to a permitted original IP. Do not
        // diagnose a deny without knowing which address the HTTP client will use.
        if (!IPAddress.TryParse(destination, out _) && effective.Any(rule => IPAddress.TryParse(rule.Destination, out _))) return null;
        return new(false, $"Network access to {destination}:{port} is blocked for Gateway. Allow this destination, wait for the policy to become active, then retry the provider test.",
            new GatewayProviderNetworkBlock(destination, port));
    }

    private static bool IsGateway(NetworkAccessIdentityMessage? identity)
        => identity is { ScopeType: "core", UserId: "system", AgentId: "gateway", Role: "core", ComponentId: "gateway" };
}
