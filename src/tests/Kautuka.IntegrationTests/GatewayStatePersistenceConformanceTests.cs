using System.Reflection;

namespace Kautuka.IntegrationTests;

public sealed class GatewayStatePersistenceConformanceTests
{
    [Fact]
    public void GatewayStateExposesOnlyExplicitAuthoritativeWriteCommands()
    {
        const BindingFlags allMethods = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        Type gatewayStateType = typeof(GatewayState);

        Assert.Null(gatewayStateType.GetMethod("Persist", allMethods));
        Assert.Null(gatewayStateType.GetMethod("PersistCore", allMethods));
        Assert.Null(gatewayStateType.GetMethod("PersistRawSettings", allMethods));
        Assert.NotNull(gatewayStateType.GetMethod("PersistRawSettingsAuthoritatively", allMethods));
        Assert.Null(gatewayStateType.Assembly.GetType("Kautuka.Gateway.Domain.State.GatewayStatePersistenceCoordinator"));
    }
}
