using Kautuka.Manager.Grpc;
using Kautuka.Manager.Protos;

namespace Kautuka.IntegrationTests;

public sealed class ManagerRpcAuthorizationTests
{
    private const string service = "/kautuka.manager.v1.ManagerService/";

    [Theory]
    [InlineData("Ping")]
    [InlineData("GetHostStatus")]
    public void UpdateServiceIdentityCanReadStatus(string method)
        => Assert.True(ManagerRpcAuthorizationPolicy.IsAuthorized(
            ManagerClientIdentities.UpdateServiceCommonName,
            service + method));

    [Theory]
    [InlineData("ListRuntimeContainers")]
    [InlineData("GetPkgsProxyCacheStats")]
    [InlineData("DescribeWorkspace")]
    [InlineData("Exec")]
    [InlineData("UpdateDesiredState")]
    [InlineData("CallMcpContainerTool")]
    public void UpdateServiceIdentityCannotUseGatewayCapabilities(string method)
        => Assert.False(ManagerRpcAuthorizationPolicy.IsAuthorized(
            ManagerClientIdentities.UpdateServiceCommonName,
            service + method));

    [Theory]
    [InlineData("GetHostStatus")]
    [InlineData("ListRuntimeContainers")]
    [InlineData("GetPkgsProxyCacheStats")]
    [InlineData("Exec")]
    [InlineData("UpdateDesiredState")]
    [InlineData("CallMcpContainerTool")]
    public void GatewayIdentityHasRequiredManagerCapabilities(string method)
        => Assert.True(ManagerRpcAuthorizationPolicy.IsAuthorized(
            ManagerClientIdentities.GatewayCommonName,
            service + method));

    [Fact]
    public void UnknownIdentityAndUnknownMethodAreDenied()
    {
        Assert.False(ManagerRpcAuthorizationPolicy.IsAuthorized("Kautuka Unknown Client", service + "GetHostStatus"));
        Assert.False(ManagerRpcAuthorizationPolicy.IsAuthorized(ManagerClientIdentities.GatewayCommonName, service + "FutureMethod"));
    }
}
