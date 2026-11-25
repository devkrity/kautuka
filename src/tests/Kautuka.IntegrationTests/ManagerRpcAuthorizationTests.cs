using Kautuka.Manager.Grpc;
using Kautuka.Manager.Protos;

namespace Kautuka.IntegrationTests;

public sealed class ManagerRpcAuthorizationTests
{
    private const string service = "/kautuka.manager.v1.ManagerService/";

    [Fact]
    public void EveryManagerRpcHasAnExplicitClientAuthorizationClassification()
    {
        string[] gatewayOnly = ["ListRuntimeContainers", "GetPkgsProxyCacheStats", "DescribeWorkspace", "RebuildWorkspace", "DeleteWorkspace", "SpawnSandbox", "InspectSandbox", "Exec", "StopSandbox", "PrimeWorkspace", "UpdateDesiredState", "ProbeMcpContainer", "ListMcpContainerTools", "CallMcpContainerTool", "PrepareProviderTest", "EndProviderTest", "EnsureProviderPermission", "ListRequests", "DecideRequest", "ListRules", "SetRule", "SetServicePermission", "DeleteRule", "ListActivity", "GetUserPrivileges", "SetUserPrivileges", "GetBrake", "SetBrake", "GetApprovalLimits", "SetApprovalLimits", "GetNetworkAccessStatus", "GetImageCompatibility", "SetImageCompatibility", "DeleteImageCompatibility"];
        string[] sharedStatus = ["Ping", "GetHostStatus"];
        string[] gatewayAndChannel = ["CheckOutbound"];
        string[] classified = [.. gatewayOnly, .. sharedStatus, .. gatewayAndChannel];
        var descriptor = ManagerReflection.Descriptor.Services.Single(s => s.Name == "ManagerService");
        Assert.Equal(descriptor.Methods.Select(m => m.Name).Order(StringComparer.Ordinal), classified.Order(StringComparer.Ordinal));
        foreach (string method in classified)
        {
            Assert.True(ManagerRpcAuthorizationPolicy.IsAuthorized(ManagerClientIdentities.GatewayCommonName, service + method), method);
            Assert.Equal(sharedStatus.Contains(method), ManagerRpcAuthorizationPolicy.IsAuthorized(ManagerClientIdentities.UpdateServiceCommonName, service + method));
            Assert.Equal(gatewayAndChannel.Contains(method), ManagerRpcAuthorizationPolicy.IsAuthorized(ManagerClientIdentities.ChannelHostCommonName, service + method));
            Assert.False(ManagerRpcAuthorizationPolicy.IsAuthorized("unknown-client", service + method));
        }
    }

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

    [Fact]
    public void NetworkAccessRpcAuthorization_EnforcesChannelHostGatewayAndUpdateServiceBoundaries()
    {
        // ChannelHost may call CheckOutbound ONLY
        Assert.True(ManagerRpcAuthorizationPolicy.IsAuthorized(
            ManagerClientIdentities.ChannelHostCommonName,
            service + "CheckOutbound"));
        Assert.False(ManagerRpcAuthorizationPolicy.IsAuthorized(
            ManagerClientIdentities.ChannelHostCommonName,
            service + "DecideRequest"));
        Assert.False(ManagerRpcAuthorizationPolicy.IsAuthorized(
            ManagerClientIdentities.ChannelHostCommonName,
            service + "SetRule"));
        Assert.False(ManagerRpcAuthorizationPolicy.IsAuthorized(
            ManagerClientIdentities.ChannelHostCommonName,
            service + "SetUserPrivileges"));
        Assert.False(ManagerRpcAuthorizationPolicy.IsAuthorized(
            ManagerClientIdentities.ChannelHostCommonName,
            service + "SetBrake"));
        Assert.False(ManagerRpcAuthorizationPolicy.IsAuthorized(
            ManagerClientIdentities.ChannelHostCommonName,
            service + "Exec"));

        // Gateway can call all NAC and ImageCompatibility RPCs
        string[] nacRpcs =
        [
            "CheckOutbound",
            "ListRequests",
            "DecideRequest",
            "ListRules",
            "SetRule",
            "DeleteRule",
            "ListActivity",
            "GetUserPrivileges",
            "SetUserPrivileges",
            "GetBrake",
            "SetBrake",
            "GetNetworkAccessStatus",
            "GetImageCompatibility",
            "SetImageCompatibility",
            "DeleteImageCompatibility",
        ];

        foreach (string rpc in nacRpcs)
        {
            Assert.True(ManagerRpcAuthorizationPolicy.IsAuthorized(
                ManagerClientIdentities.GatewayCommonName,
                service + rpc));
            Assert.False(ManagerRpcAuthorizationPolicy.IsAuthorized(
                ManagerClientIdentities.UpdateServiceCommonName,
                service + rpc));
        }
    }
}
