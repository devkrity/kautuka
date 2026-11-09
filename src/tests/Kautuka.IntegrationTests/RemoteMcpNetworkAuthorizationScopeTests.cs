namespace Kautuka.IntegrationTests;

public sealed class RemoteMcpNetworkAuthorizationScopeTests
{
    [Fact]
    public void AgentRemoteConnectorsUseTheExistingUserAgentPermissionIdentity()
    {
        var first = RemoteMcpNetworkAuthorization.CreateIdentity("agent", "owner", "workspace-alpha", "mail");
        var second = RemoteMcpNetworkAuthorization.CreateIdentity("agent", "owner", "workspace-alpha", "search");

        Assert.Equal("agent", first.ScopeType);
        Assert.Equal("owner", first.UserId);
        Assert.Equal("workspace-alpha", first.AgentId);
        Assert.Equal("agent", first.Role);
        Assert.Equal("workspace-alpha", first.ComponentId);
        Assert.Equal(first, second);
    }

    [Fact]
    public void ManagementConnectorTestsRemainConnectorScoped()
    {
        var mail = RemoteMcpNetworkAuthorization.CreateIdentity("management", "owner", "ignored", "mail");
        var search = RemoteMcpNetworkAuthorization.CreateIdentity("management", "owner", "ignored", "search");

        Assert.Equal("management", mail.ScopeType);
        Assert.Equal("management", mail.AgentId);
        Assert.Equal("remote-connector", mail.Role);
        Assert.NotEqual(mail.ComponentId, search.ComponentId);
    }
}
