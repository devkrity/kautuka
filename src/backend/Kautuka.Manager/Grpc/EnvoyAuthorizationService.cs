using Grpc.Core;

using Kautuka.Manager.Protos.EnvoyAuthorization;
using Kautuka.Manager.RuntimeContainer.Hosting;

using System.Net;
using System.Net.Sockets;

using Status = Kautuka.Manager.Protos.EnvoyAuthorization.Status;

namespace Kautuka.Manager.Grpc;

internal sealed class EnvoyAuthorizationService(ContainerEgressRuntime runtime)
    : Kautuka.Manager.Protos.EnvoyAuthorization.Authorization.AuthorizationBase
{
    private static readonly SemaphoreSlim capacity = new(32, 32);
    public override Task<CheckResponse> Check(CheckRequest request, ServerCallContext context)
    {
        if (!capacity.Wait(0)) return Task.FromResult(Deny());
        try
        {
            string[] generations = [.. context.RequestHeaders.Where(item => item.Key == "x-kautuka-nac-generation").Select(item => item.Value)];
            AttributeContext? attributes = request.Attributes;
            Kautuka.Manager.Protos.EnvoyAuthorization.SocketAddress? source = attributes?.Source?.Address?.SocketAddress;
            Kautuka.Manager.Protos.EnvoyAuthorization.SocketAddress? destination = attributes?.Destination?.Address?.SocketAddress;
            if (generations.Length != 1 || string.IsNullOrWhiteSpace(generations[0])
                || source is null || destination is null || source.Protocol != 0 || destination.Protocol != 0
                || source.PortValue is < 1 or > 65535 || destination.PortValue is < 1 or > 65535
                || context.CancellationToken.IsCancellationRequested || context.Deadline <= DateTime.UtcNow
                || !TryCanonicalIpv4(source.Address, out string sourceAddress)
                || !TryCanonicalIpv4(destination.Address, out string destinationAddress)) return Task.FromResult(Deny());
            bool allowed = runtime.AuthorizeNetworkConnection(generations[0], sourceAddress, destinationAddress,
                (int)destination.PortValue, attributes?.TlsSession?.Sni);
            return Task.FromResult(new CheckResponse { Status = new Status { Code = allowed ? 0 : 7 } });
        }
        catch (Exception) { return Task.FromResult(Deny()); }
        finally { capacity.Release(); }
    }

    private static bool TryCanonicalIpv4(string? address, out string canonical)
    {
        canonical = string.Empty;
        if (!IPAddress.TryParse(address, out IPAddress? parsed) || parsed.AddressFamily != AddressFamily.InterNetwork) return false;
        canonical = parsed.ToString();
        return true;
    }

    private static CheckResponse Deny() => new() { Status = new Status { Code = 7 } };
}
