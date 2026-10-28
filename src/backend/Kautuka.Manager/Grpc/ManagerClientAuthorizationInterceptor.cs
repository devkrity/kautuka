using System.Security.Cryptography.X509Certificates;

using Grpc.Core;
using Grpc.Core.Interceptors;

using Kautuka.Manager.Protos;

namespace Kautuka.Manager.Grpc;

internal sealed class ManagerClientAuthorizationInterceptor : Interceptor
{
    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        X509Certificate2? certificate = context.GetHttpContext().Connection.ClientCertificate;
        if (certificate is null)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Manager client certificate is required."));

        string commonName = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
        if (!ManagerRpcAuthorizationPolicy.IsAuthorized(commonName, context.Method))
            throw new RpcException(new Status(StatusCode.PermissionDenied, "Manager client is not authorized for this operation."));

        return await continuation(request, context).ConfigureAwait(false);
    }
}

internal static class ManagerRpcAuthorizationPolicy
{
    private const string servicePrefix = "/kautuka.manager.v1.ManagerService/";

    private static readonly HashSet<string> statusMethods = new(StringComparer.Ordinal)
    {
        servicePrefix + "Ping",
        servicePrefix + "GetHostStatus",
    };

    private static readonly HashSet<string> gatewayMethods = new(StringComparer.Ordinal)
    {
        servicePrefix + "Ping",
        servicePrefix + "GetHostStatus",
        servicePrefix + "ListRuntimeContainers",
        servicePrefix + "GetPkgsProxyCacheStats",
        servicePrefix + "DescribeWorkspace",
        servicePrefix + "RebuildWorkspace",
        servicePrefix + "DeleteWorkspace",
        servicePrefix + "SpawnSandbox",
        servicePrefix + "InspectSandbox",
        servicePrefix + "Exec",
        servicePrefix + "StopSandbox",
        servicePrefix + "PrimeWorkspace",
        servicePrefix + "UpdateDesiredState",
        servicePrefix + "ProbeMcpContainer",
        servicePrefix + "ListMcpContainerTools",
        servicePrefix + "CallMcpContainerTool",
    };

    public static bool IsAuthorized(string clientCommonName, string method)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientCommonName);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);

        return clientCommonName switch
        {
            ManagerClientIdentities.GatewayCommonName => gatewayMethods.Contains(method),
            ManagerClientIdentities.UpdateServiceCommonName => statusMethods.Contains(method),
            _ => false,
        };
    }
}
