using System.Text.Json.Nodes;
using System.Text.Json;

using Grpc.Core;

using Kautuka.Gateway.Domain.Providers;
using Kautuka.Manager.Protos;

namespace Kautuka.IntegrationTests;

public sealed class ProviderNetworkAccessCheckTests
{
    [Fact]
    public void LegacyLanAllowRequiresAdministratorReapproval()
    {
        NetworkAccessPolicyResponse policy = Active();
        var rule = new NetworkAccessRuleMessage { Identity = Gateway(), Destination = "192.168.1.20", Port = 11434, Allowed = true };
        policy.Rules.Add(rule);
        Assert.Equal(new GatewayProviderNetworkBlock("192.168.1.20", 11434), ProviderNetworkAccessCheck.Evaluate(policy, rule.Destination, rule.Port)?.NetworkBlock);
        rule.AllowPrivateNetwork = true;
        Assert.Null(ProviderNetworkAccessCheck.Evaluate(policy, rule.Destination, rule.Port));
    }

    [Theory]
    [InlineData("169.254.169.254")]
    [InlineData("100.100.100.200")]
    [InlineData("168.63.129.16")]
    [InlineData("metadata.google.internal")]
    public void ProtectedProviderEndpointNeverOffersAllow(string address)
    {
        NetworkAccessPolicyResponse policy = Active();
        policy.Rules.Add(new NetworkAccessRuleMessage { Identity = Gateway(), Destination = address, Port = 80, Allowed = true, AllowPrivateNetwork = true });
        GatewayProviderTestResult? result = ProviderNetworkAccessCheck.Evaluate(policy, address, 80);
        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Null(result.NetworkBlock);
        Assert.Contains("cannot be allowed", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderResponseSerializesStructuredBlockWithAotContext()
    {
        var response = new GatewayProviderTestResponse(false, "blocked", new GatewayProviderNetworkBlock("192.0.2.4", 11434));
        string json = JsonSerializer.Serialize(response, KautukaJsonContext.Default.GatewayProviderTestResponse);
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal("192.0.2.4", document.RootElement.GetProperty("networkBlock").GetProperty("destination").GetString());
        Assert.Equal(11434, document.RootElement.GetProperty("networkBlock").GetProperty("port").GetInt32());
    }
    private static NetworkAccessIdentityMessage Gateway() => new() { ScopeType = "core", UserId = "system", AgentId = "gateway", Role = "core", ComponentId = "gateway" };
    private static NetworkAccessPolicyResponse Active()
    {
        var policy = new NetworkAccessPolicyResponse { Enabled = true, Revision = 2, ActiveRevision = 2, ActiveVersion = "accepted", ActivationState = "active" };
        policy.Sources.Add(Gateway());
        return policy;
    }

    [Fact]
    public void ActiveExactTargetDenyOffersOnlyItsDestinationAndPort()
    {
        GatewayProviderTestResult result = Assert.IsType<GatewayProviderTestResult>(ProviderNetworkAccessCheck.Evaluate(Active(), "192.0.2.40", 11434));
        Assert.False(result.Success);
        Assert.Equal(new GatewayProviderNetworkBlock("192.0.2.40", 11434), result.NetworkBlock);
    }

    [Fact]
    public void AllowedTargetIsNotMisclassifiedAndExplicitDenyOverridesDefault()
    {
        NetworkAccessPolicyResponse policy = Active();
        policy.Defaults.Add(new NetworkAccessRuleMessage { Identity = Gateway(), Destination = "api.openai.com", Port = 443, Allowed = true });
        Assert.Null(ProviderNetworkAccessCheck.Evaluate(policy, "api.openai.com", 443));
        policy.Rules.Add(new NetworkAccessRuleMessage { Identity = Gateway(), Destination = "api.openai.com", Port = 443, Allowed = false });
        Assert.NotNull(ProviderNetworkAccessCheck.Evaluate(policy, "api.openai.com", 443)?.NetworkBlock);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("failed")]
    [InlineData("disabled")]
    public void UnconfirmedPolicyDoesNotDiagnoseAConnectivityFailure(string state)
    {
        NetworkAccessPolicyResponse policy = Active(); policy.ActivationState = state;
        Assert.Null(ProviderNetworkAccessCheck.Evaluate(policy, "api.openai.com", 443));
    }

    [Fact]
    public void DifferentIdentityCannotAuthorizeGatewayAndIpFallbackIsConservative()
    {
        NetworkAccessPolicyResponse policy = Active();
        NetworkAccessIdentityMessage other = Gateway(); other.ComponentId = "channelhost";
        policy.Rules.Add(new NetworkAccessRuleMessage { Identity = other, Destination = "api.openai.com", Port = 443, Allowed = true });
        Assert.NotNull(ProviderNetworkAccessCheck.Evaluate(policy, "api.openai.com", 443));
        policy.Rules.Add(new NetworkAccessRuleMessage { Identity = Gateway(), Destination = "192.0.2.3", Port = 443, Allowed = true, AllowPrivateNetwork = true });
        Assert.Null(ProviderNetworkAccessCheck.Evaluate(policy, "api.openai.com", 443));
    }

    [Fact]
    public async Task LocalhostExplainsContainerAddressWithoutOfferingAllow()
    {
        GatewayProviderTestResult? result = await ProviderNetworkAccessCheck.CheckAsync((_, _) => Task.FromResult(Active()),
            new JsonObject { ["base_url"] = "http://localhost:11434" }, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Contains("inside the Gateway container", result.Message, StringComparison.Ordinal);
        Assert.Null(result.NetworkBlock);
    }

    [Fact]
    public async Task LaterPageGrantIsReadBeforeDeclaringDeny()
    {
        NetworkAccessPolicyResponse first = Active(); first.NextOffset = 50;
        NetworkAccessPolicyResponse last = Active();
        last.Rules.Add(new NetworkAccessRuleMessage { Identity = Gateway(), Destination = "192.0.2.4", Port = 11434, Allowed = true, AllowPrivateNetwork = true });
        int reads = 0;
        GatewayProviderTestResult? result = await ProviderNetworkAccessCheck.CheckAsync((request, _) =>
        {
            reads++;
            if (reads == 1) return Task.FromResult(first);
            Assert.True(request.EnforceRevision); Assert.Equal(2, request.ExpectedRevision); Assert.Equal(50, request.Offset);
            return Task.FromResult(last);
        }, new JsonObject { ["base_url"] = "http://192.0.2.4:11434" }, TestContext.Current.CancellationToken);
        Assert.Equal(2, reads); Assert.Null(result);
    }

    [Fact]
    public async Task ManagerUnavailableDoesNotBecomeNetworkBlock()
    {
        GatewayProviderTestResult? result = await ProviderNetworkAccessCheck.CheckAsync((_, _) =>
            Task.FromException<NetworkAccessPolicyResponse>(new RpcException(new Status(StatusCode.Unavailable, "offline"))),
            new JsonObject { ["base_url"] = "http://192.0.2.4:11434" }, TestContext.Current.CancellationToken);
        Assert.Null(result);
    }
}
