using Kautuka.NetworkAccessController;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class NetworkDomainApprovalTests
{
    [Theory]
    [InlineData("ediTion.cnn.com.", "cnn.com")]
    [InlineData("images.example.co.uk", "example.co.uk")]
    [InlineData("api.example.github.io", "example.github.io")]
    [InlineData("sub.www.ck", "www.ck")]
    [InlineData("api.bücher.de", "xn--bcher-kva.de")]
    [InlineData("co.uk", null)]
    [InlineData("github.io", null)]
    [InlineData("example.ck", null)]
    [InlineData("mcp.unknowntesttld", null)]
    [InlineData("101.0.192.in-addr.arpa", null)]
    [InlineData("printer.home.arpa", null)]
    [InlineData("service.example.onion", null)]
    [InlineData("192.168.1.2", null)]
    public void BaseDomainUsesKnownPublicAndPrivateSuffixes(string host, string? expected)
    {
        Assert.Equal(expected is not null, NetworkRegistrableDomain.TryGet(host, out string actual));
        Assert.Equal(expected ?? string.Empty, actual);
    }

    [Theory]
    [InlineData("gateway")]
    [InlineData("system")]
    public void ScopedDomainBlockOverridesAnAllowedFirstPartyDestination(string systemAgent)
    {
        DirectoryInfo root = Directory.CreateTempSubdirectory("kautuka-domain-system-");
        try
        {
            using var policy = new NetworkAccessPolicyStore(Path.Combine(root.FullName, "policy.json"));
            var controller = new Kautuka.NetworkAccessController.NetworkAccessController(policy, new WorkloadRegistry());
            var input = new OutboundCheckInput("system", "gateway", null, "api.anthropic.com", 443, "model", null);
            Assert.True(controller.EvaluateOutboundCheck(input).Allowed);
            policy.SetRule(new NetworkAccessActor("admin", IsAdmin: true), new RuleWriteSpec("agent", "system", systemAgent,
                null, "anthropic.com", "domain", 443, false), policy.Revision);
            Assert.Equal("blocked", controller.EvaluateOutboundCheck(input).Reason);
        }
        finally { root.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DomainApprovalKeepsTheAgentPortAndExactBlocks(bool includeSubdomains)
    {
        DirectoryInfo root = Directory.CreateTempSubdirectory("kautuka-domain-approval-");
        try
        {
            using var policy = new NetworkAccessPolicyStore(Path.Combine(root.FullName, "policy.json"), []);
            var controller = new Kautuka.NetworkAccessController.NetworkAccessController(policy, new WorkloadRegistry());
            var actor = new NetworkAccessActor("owner", IsAdmin: true);
            var input = new OutboundCheckInput("owner", "agent-a", null, "www.cnn.com", 443, "agent-run", null);
            string requestId = Assert.IsType<string>(controller.EvaluateOutboundCheck(input).RequestId);
            DecideRequestResult decision = policy.DecideRequest(actor, requestId, true, "agent", policy.Revision,
                includeSubdomains: includeSubdomains);
            NetworkAccessRule rule = Assert.IsType<NetworkAccessRule>(decision.Rule);
            Assert.Equal(includeSubdomains ? "domain" : "host", rule.DestKind);
            Assert.Equal(includeSubdomains ? "cnn.com" : "www.cnn.com", rule.Destination);
            Assert.True(controller.EvaluateOutboundCheck(input).Allowed);
            Assert.Equal(includeSubdomains, controller.EvaluateOutboundCheck(input with { Host = "edition.cnn.com" }).Allowed);
            Assert.False(controller.EvaluateOutboundCheck(input with { Host = "fakecnn.com" }).Allowed);
            Assert.False(controller.EvaluateOutboundCheck(input with { Host = "cnn.com.example.net" }).Allowed);
            Assert.False(controller.EvaluateOutboundCheck(input with { AgentId = "agent-b" }).Allowed);
            Assert.False(controller.EvaluateOutboundCheck(input with { Port = 8443 }).Allowed);

            policy.SetRule(actor, new RuleWriteSpec("agent", "owner", "agent-a", null,
                "www.cnn.com", "host", 443, false), policy.Revision);
            Assert.Equal("blocked", controller.EvaluateOutboundCheck(input).Reason);
        }
        finally { root.Delete(recursive: true); }
    }
}
