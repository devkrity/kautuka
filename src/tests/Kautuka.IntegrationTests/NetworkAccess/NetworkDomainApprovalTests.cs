using Kautuka.NetworkAccessController;
using Kautuka.UpdateService.Hosting;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class NetworkDomainApprovalTests
{
    private static readonly string fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "public-suffix-test.dat");

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
        var domains = new NetworkRegistrableDomain(fixture, minimumRuleCount: 1);
        Assert.Equal(expected is not null, domains.TryGet(host, out string actual));
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
            using var policy = new NetworkAccessPolicyStore(Path.Combine(root.FullName, "policy.json"),
                registrableDomain: new NetworkRegistrableDomain(fixture, minimumRuleCount: 1));
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
            using var policy = new NetworkAccessPolicyStore(Path.Combine(root.FullName, "policy.json"), [],
                registrableDomain: new NetworkRegistrableDomain(fixture, minimumRuleCount: 1));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnavailableListNarrowsAllowsPreservesBlocksAndOffersExactApproval(bool corrupt)
    {
        DirectoryInfo root = Directory.CreateTempSubdirectory("kautuka-domain-fallback-");
        try
        {
            string path = Path.Combine(root.FullName, NetworkRegistrableDomain.ListFileName);
            File.Copy(fixture, path);
            var domains = new NetworkRegistrableDomain(path, minimumRuleCount: 1);
            using var policy = new NetworkAccessPolicyStore(Path.Combine(root.FullName, "policy.json"), [], registrableDomain: domains);
            var controller = new Kautuka.NetworkAccessController.NetworkAccessController(policy, new WorkloadRegistry());
            var owner = new NetworkAccessActor("owner", IsAdmin: true);
            policy.SetRule(owner, new RuleWriteSpec("agent", "owner", "agent-a", null, "cnn.com", "domain", 443, true), policy.Revision);
            policy.SetRule(owner, new RuleWriteSpec("agent", "owner", "agent-a", null, "example.com", "domain", 443, false), policy.Revision);
            policy.SetRule(owner, new RuleWriteSpec("system", null, null, null, "www.example.com", "host", 443, true), policy.Revision);
            var input = new OutboundCheckInput("owner", "agent-a", null, "edition.cnn.com", 443, "agent-run", null);
            Assert.True(controller.EvaluateOutboundCheck(input).Allowed);
            Assert.Equal("blocked", controller.EvaluateOutboundCheck(input with { Host = "www.example.com" }).Reason);

            if (corrupt) File.WriteAllText(path, "<html>invalid</html>");
            else File.Delete(path);

            Assert.False(domains.TryGet("edition.cnn.com", out _));
            Assert.True(controller.EvaluateOutboundCheck(input with { Host = "cnn.com" }).Allowed);
            Assert.Equal("blocked", controller.EvaluateOutboundCheck(input with { Host = "www.example.com" }).Reason);
            string requestId = Assert.IsType<string>(controller.EvaluateOutboundCheck(input).RequestId);
            Assert.Throws<ArgumentException>(() =>
                policy.DecideRequest(owner, requestId, true, "agent", policy.Revision, includeSubdomains: true));
            DecideRequestResult result = policy.DecideRequest(owner, requestId, true, "agent", policy.Revision);
            Assert.Equal("host", Assert.IsType<NetworkAccessRule>(result.Rule).DestKind);
            Assert.True(controller.EvaluateOutboundCheck(input).Allowed);
            Assert.False(controller.EvaluateOutboundCheck(input with { Host = "other.cnn.com" }).Allowed);
        }
        finally { root.Delete(recursive: true); }
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("shrunken")]
    [InlineData("valid")]
    public async Task RefreshReplacesOnlyAValidListWithoutExcessiveRuleLoss(string candidate)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        DirectoryInfo root = Directory.CreateTempSubdirectory("kautuka-domain-refresh-");
        try
        {
            string path = Path.Combine(root.FullName, NetworkRegistrableDomain.ListFileName);
            string previous = (await File.ReadAllTextAsync(fixture, cancellationToken)).ReplaceLineEndings("\n");
            await File.WriteAllTextAsync(path, previous, cancellationToken);
            var domains = new NetworkRegistrableDomain(path, minimumRuleCount: 1);
            Assert.True(domains.TryGet("www.cnn.com", out string before));
            Assert.Equal("cnn.com", before);
            string content = candidate switch
            {
                "corrupt" => previous.Replace("// ===END PRIVATE DOMAINS===", "<html>truncated</html>", StringComparison.Ordinal),
                "shrunken" => previous.Replace("de\nio\nck\n*.ck\n!www.ck\nuk\nco.uk\n", "", StringComparison.Ordinal),
                _ => previous.Replace("github.io", "github.io\ncnn.com", StringComparison.Ordinal),
            };
            using var client = new HttpClient(new SuffixListResponse(content));
            using var reader = new StringReader(previous);
            int currentCount = NetworkRegistrableDomain.CountRules(reader);
            var options = new UpdateOptions { PublicSuffixMinimumRuleCount = 1 };
            bool replaced = await PublicSuffixRefreshWorker.TryRefreshAsync(client, path, currentCount, options, cancellationToken);
            if (candidate == "valid")
            {
                Assert.True(replaced);
                Assert.Equal(content, await File.ReadAllTextAsync(path, cancellationToken));
                Assert.True(domains.TryGet("www.cnn.com", out string after));
                Assert.Equal("www.cnn.com", after);
            }
            else
            {
                Assert.False(replaced);
                Assert.Equal(previous, await File.ReadAllTextAsync(path, cancellationToken));
            }
            Assert.Equal(path, Assert.Single(Directory.GetFiles(root.FullName)));
        }
        finally { root.Delete(recursive: true); }
    }

    [Fact]
    public async Task FirstRefreshRejectsAWellFormedListBelowTheMinimum()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var client = new HttpClient(new SuffixListResponse(await File.ReadAllTextAsync(fixture, cancellationToken)));
        DirectoryInfo root = Directory.CreateTempSubdirectory("kautuka-domain-first-refresh-");
        try
        {
            string path = Path.Combine(root.FullName, NetworkRegistrableDomain.ListFileName);
            Assert.False(await PublicSuffixRefreshWorker.TryRefreshAsync(client, path, 0, new UpdateOptions(), cancellationToken));
            Assert.False(File.Exists(path));
            var options = new UpdateOptions { PublicSuffixMinimumRuleCount = 1 };
            Assert.True(await PublicSuffixRefreshWorker.TryRefreshAsync(client, path, 0, options, cancellationToken));
            Assert.True(new NetworkRegistrableDomain(path, minimumRuleCount: 1).TryGet("www.cnn.com", out _));
            Assert.False(new NetworkRegistrableDomain(path).TryGet("www.cnn.com", out _));
        }
        finally { root.Delete(recursive: true); }
    }

    private sealed class SuffixListResponse(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(new Uri("https://publicsuffix.org/list/public_suffix_list.dat"), request.RequestUri);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(content) });
        }
    }
}
