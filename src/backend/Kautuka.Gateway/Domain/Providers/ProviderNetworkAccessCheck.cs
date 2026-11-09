using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;

using Grpc.Core;

using Kautuka.Manager.Protos;

namespace Kautuka.Gateway.Domain.Providers;

// Diagnostics only: Manager's recorded denial is evidence, never a second policy decision.
internal static class ProviderNetworkAccessCheck
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Best-effort diagnostics must never replace the original durable terminal error.")]
    internal static async Task<string> DescribeChatFailureAsync(
        Func<NetworkAccessReadRequest, CancellationToken, Task<NetworkAccessPolicyResponse>> readPolicy,
        string endpoint, DateTimeOffset started, Exception failure, CancellationToken ct)
    {
        bool transport = false;
        for (Exception? current = failure; current is not null; current = current.InnerException)
        {
            if (current is System.ClientModel.ClientResultException response && response.Status > 0) return failure.Message;
            if (current is HttpRequestException http)
            {
                if (http.StatusCode is not null) return failure.Message;
                transport = true;
            }
            if (current is System.Net.Sockets.SocketException) transport = true;
        }
        if (!transport) return failure.Message;
        try
        {
            using var diagnosticCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            diagnosticCancellation.CancelAfter(TimeSpan.FromSeconds(2));
            GatewayProviderTestResult? diagnosis = await CheckRecordedDenialAsync(readPolicy,
                new JsonObject { ["base_url"] = endpoint }, started, diagnosticCancellation.Token)
                .WaitAsync(diagnosticCancellation.Token).ConfigureAwait(false);
            return diagnosis?.Message ?? failure.Message;
        }
        catch (Exception)
        {
            return failure.Message;
        }
    }

    internal static async Task<GatewayProviderTestResult?> CheckRecordedDenialAsync(
        Func<NetworkAccessReadRequest, CancellationToken, Task<NetworkAccessPolicyResponse>> readPolicy,
        JsonObject settings, DateTimeOffset started, CancellationToken ct)
    {
        if (!Uri.TryCreate((settings["base_url"] ?? settings["endpoint"])?.GetValue<string>(), UriKind.Absolute, out Uri? target)
            || target.Scheme is not ("http" or "https")) return null;
        if (target.IsLoopback) return null;
        // Plain HTTP hostnames are authorized by the connected IP, which may differ from DNS now.
        if (target.Scheme == Uri.UriSchemeHttp && !IPAddress.TryParse(target.IdnHost, out _)) return null;
        string destination = target.IdnHost.TrimEnd('.');
        try
        {
            var request = new NetworkAccessReadRequest { Actor = new NetworkAccessActorMessage { UserId = "gateway-provider-validation", IsAdmin = true } };
            NetworkAccessPolicyResponse policy = await readPolicy(request, ct).ConfigureAwait(false);
            for (int pages = 0; ; pages++)
            {
                NetworkAccessRequestMessage? observed = policy.Requests.FirstOrDefault(item => IsGateway(item.Identity)
                    && item.Status == "pending" && item.Port == target.Port
                    && string.Equals(item.Destination, destination, StringComparison.OrdinalIgnoreCase)
                    && DateTimeOffset.TryParse(item.LastSeenAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset lastSeen)
                    && lastSeen >= started);
                if (observed is not null)
                    return new(false, $"Manager recorded a recent network access denial for Gateway to {destination}:{target.Port}. Review the request in Network access > Permissions; local-network access requires administrator approval.",
                        new GatewayProviderNetworkBlock(destination, target.Port, observed.Id));
                if (policy.NextOffset == 0 || pages >= 220 || policy.NextOffset <= request.Offset) return null;
                request.Offset = policy.NextOffset;
                request.EnforceRevision = true;
                request.ExpectedRevision = policy.Revision;
                policy = await readPolicy(request, ct).ConfigureAwait(false);
            }
        }
        catch (RpcException) when (!ct.IsCancellationRequested) { return null; }
        catch (NotSupportedException) { return null; }
    }

    private static bool IsGateway(NetworkAccessIdentityMessage? identity)
        => identity is { ScopeType: "core", UserId: "system", AgentId: "gateway", Role: "core", ComponentId: "gateway" };
}
