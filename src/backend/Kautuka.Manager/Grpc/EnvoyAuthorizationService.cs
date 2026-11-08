using Grpc.Core;

using Kautuka.Manager.Protos.EnvoyAuthorization;
using Kautuka.Manager.RuntimeContainer.Hosting;

using System.Net;
using System.Net.Sockets;

using Status = Kautuka.Manager.Protos.EnvoyAuthorization.Status;
using RpcStatus = global::Grpc.Core.Status;

namespace Kautuka.Manager.Grpc;

internal sealed class EnvoyAuthorizationService(ContainerEgressRuntime runtime, SemaphoreSlim? admissionCapacity = null)
    : Kautuka.Manager.Protos.EnvoyAuthorization.Authorization.AuthorizationBase
{
    private static readonly SemaphoreSlim sharedCapacity = new(32, 32);
    private readonly SemaphoreSlim capacity = admissionCapacity ?? sharedCapacity;
    public override Task<CheckResponse> Check(CheckRequest request, ServerCallContext context)
    {
        if (context.CancellationToken.IsCancellationRequested)
            throw new RpcException(new RpcStatus(StatusCode.Cancelled, "The authorization check was cancelled."));
        if (context.Deadline <= DateTime.UtcNow)
            throw new RpcException(new RpcStatus(StatusCode.DeadlineExceeded, "The authorization check deadline expired."));
        if (!capacity.Wait(0))
            throw new RpcException(new RpcStatus(StatusCode.Unavailable, "Network authorization capacity is temporarily exhausted."));
        try
        {
            string[] generations = [.. context.RequestHeaders.Where(item => item.Key == "x-kautuka-nac-generation").Select(item => item.Value)];
            AttributeContext? attributes = request.Attributes;
            Kautuka.Manager.Protos.EnvoyAuthorization.SocketAddress? source = attributes?.Source?.Address?.SocketAddress;
            Kautuka.Manager.Protos.EnvoyAuthorization.SocketAddress? destination = attributes?.Destination?.Address?.SocketAddress;
            if (generations.Length != 1 || string.IsNullOrWhiteSpace(generations[0])
                || source is null || destination is null || source.Protocol != 0 || destination.Protocol != 0
                || source.PortValue is < 1 or > 65535 || destination.PortValue is < 1 or > 65535
                || !TryCanonicalIpv4(source.Address, out string sourceAddress)
                || !TryCanonicalIpv4(destination.Address, out string destinationAddress)) return Task.FromResult(Deny());
            bool allowed = runtime.AuthorizeNetworkConnection(generations[0], sourceAddress, destinationAddress,
                (int)destination.PortValue, attributes?.TlsSession?.Sni);
            return Task.FromResult(new CheckResponse { Status = new Status { Code = allowed ? 0 : 7 } });
        }
        catch (RpcException) { throw; }
        catch (OperationCanceledException)
        {
            throw new RpcException(new RpcStatus(context.Deadline <= DateTime.UtcNow
                ? StatusCode.DeadlineExceeded : StatusCode.Cancelled, "The authorization check did not complete."));
        }
        catch (Exception)
        {
            throw new RpcException(new RpcStatus(StatusCode.Unavailable, "Network authorization is temporarily unavailable."));
        }
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
