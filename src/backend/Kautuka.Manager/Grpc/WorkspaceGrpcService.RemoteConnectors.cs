using Grpc.Core;

using Kautuka.Manager.Protos;
using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.Manager.Grpc;

public sealed partial class WorkspaceGrpcService
{
    public override Task<RemoteConnectorNetworkResponse> CheckRemoteConnectorNetwork(RemoteConnectorNetworkRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        context.CancellationToken.ThrowIfCancellationRequested();
        try
        {
            RemoteConnectorNetworkDecision decision = NetworkRuntime.CheckRemoteConnectorNetwork(Actor(request.Actor), Identity(request.Identity),
                request.Scheme, request.Hostname, request.Port, request.PinnedIpv4);
            return Task.FromResult(new RemoteConnectorNetworkResponse { Enabled = decision.Enabled, Decision = decision.Decision, Message = decision.Message, RouteBinding = decision.RouteBinding });
        }
        catch (UnauthorizedAccessException) { throw new RpcException(new Status(StatusCode.PermissionDenied, "The remote connector scope is not accessible to this caller.")); }
        catch (ArgumentException ex) { throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message)); }
        catch (InvalidOperationException) { throw new RpcException(new Status(StatusCode.Unavailable, "Network authorization is unavailable. Retry the operation.")); }
        catch (IOException) { throw new RpcException(new Status(StatusCode.Unavailable, "Network authorization storage is unavailable.")); }
    }
}
