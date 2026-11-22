using Kautuka.NetworkAccessController;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class ApplianceChannelProviderNetworkPathTests
{
    [Fact]
    public void VerifiedUpdateServiceCanReachOnlyReleaseProviderTlsThroughTheDnsSinkhole()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("kautuka-update-policy-");
        try { VerifyUpdatePolicy(directory.FullName); }
        finally { directory.Delete(recursive: true); }
    }

    private static void VerifyUpdatePolicy(string directory)
    {
        using var policy = new NetworkAccessPolicyStore(Path.Combine(directory, "policy.json"));
        var registry = new WorkloadRegistry();
        const string source = NetworkAddressClassifier.ApplianceUpdateServiceAddress;
        const string sinkhole = NetworkAddressClassifier.SinkholeAddress;
        var controller = new Kautuka.NetworkAccessController.NetworkAccessController(policy, registry);

        Assert.False(controller.EvaluateContainerConnection(source, sinkhole, 443, true, "github.com").Allowed);
        registry.Register(new RegistryEntry(source, "system", "updateservice", "fixture", "agent-sandbox", null,
            "agent-sandbox", "normal", "user-agent", null, DateTimeOffset.UtcNow, Purpose: "appliance-updateservice"));
        foreach (string host in new[] { "api.github.com", "github.com", "release-assets.githubusercontent.com" })
        {
            var decision = controller.EvaluateContainerConnection(source, sinkhole, 443, true, host);
            Assert.True(decision.Allowed);
            Assert.Equal(Kautuka.NetworkAccessController.NetworkAccessController.ClusterDfpPublic, decision.Cluster);
        }

        Assert.False(controller.EvaluateContainerConnection(source, sinkhole, 443, true, "api.github.com.example.test").Allowed);
        Assert.False(controller.EvaluateContainerConnection(source, sinkhole, 443, true, "api.telegram.org").Allowed);
        Assert.False(controller.EvaluateContainerConnection(source, sinkhole, 8443, true, "github.com").Allowed);
        Assert.False(controller.EvaluateContainerConnection(source, sinkhole, 443, false, null).Allowed);
        Assert.False(controller.EvaluateContainerConnection(source, "192.0.2.2", 443, true, "github.com").Allowed);
        policy.SetBrake(new NetworkAccessActor("admin", IsAdmin: true), "system", null, on: true);
        Assert.Equal("brake_system", controller.EvaluateContainerConnection(source, sinkhole, 443, true, "github.com").Reason);
    }

    [Fact]
    public void VerifiedChannelHostCanReachOnlyProviderTlsThroughTheDnsSinkhole()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("kautuka-channel-policy-");
        try
        {
            VerifyPolicy(directory.FullName);
        }
        finally { directory.Delete(recursive: true); }
    }

    private static void VerifyPolicy(string directory)
    {
        using var policy = new NetworkAccessPolicyStore(Path.Combine(directory, "policy.json"));
        var registry = new WorkloadRegistry();
        const string source = NetworkAddressClassifier.ApplianceChannelHostAddress;
        const string sinkhole = NetworkAddressClassifier.SinkholeAddress;
        var controller = new Kautuka.NetworkAccessController.NetworkAccessController(policy, registry);

        Assert.False(controller.EvaluateContainerConnection(source, sinkhole, 443, true, "slack.com").Allowed);

        registry.Register(new RegistryEntry(source, "system", "channelhost", "fixture", "agent-sandbox", null,
            "agent-sandbox", "normal", "user-agent", null, DateTimeOffset.UtcNow, Purpose: "appliance-channelhost"));
        bool CheckApp(string host, string agent = "channelhost")
            => controller.EvaluateOutboundCheck(new OutboundCheckInput("system", agent, null, host, 443, null, null)).Allowed;
        Assert.False(CheckApp("api.telegram.org"));
        Assert.False(controller.EvaluateContainerConnection(source, sinkhole, 443, true, "api.telegram.org").Allowed);
        Assert.False(CheckApp("wss-primary.slack.com"));
        Assert.False(controller.EvaluateContainerConnection(source, sinkhole, 443, true, "wss-primary.slack.com").Allowed);

        var admin = new NetworkAccessActor("admin", IsAdmin: true);
        policy.SetRule(admin, new RuleWriteSpec("agent", "system", "channelhost", null, "slack.com", "host", 443, true), policy.Revision);
        policy.SetRule(admin, new RuleWriteSpec("agent", "system", "channelhost", null, "api.telegram.org", "host", 443, true), policy.Revision);
        foreach (string host in new[] { "api.telegram.org", "slack.com", "wss-primary.slack.com", "files.slack.com" })
        {
            var decision = controller.EvaluateContainerConnection(source, sinkhole, 443, true, host);
            Assert.True(decision.Allowed);
            Assert.True(CheckApp(host));
            Assert.False(CheckApp(host, "gateway"));
            Assert.Equal(Kautuka.NetworkAccessController.NetworkAccessController.ClusterDfpPublic, decision.Cluster);
        }
        policy.SetRule(admin, new RuleWriteSpec("agent", "system", "channelhost", null, "files.slack.com", "host", 443, false), policy.Revision);
        Assert.False(CheckApp("files.slack.com"));
        Assert.False(controller.EvaluateContainerConnection(source, sinkhole, 443, true, "files.slack.com").Allowed);
        policy.SetRule(admin, new RuleWriteSpec("agent", "system", "channelhost", null, "wss-primary.slack.com", "host", 443, true), policy.Revision);
        policy.SetRule(admin, new RuleWriteSpec("agent", "system", "channelhost", null, "slack.com", "host", 443, false), policy.Revision);
        Assert.False(CheckApp("wss-primary.slack.com"));
        Assert.False(controller.EvaluateContainerConnection(source, sinkhole, 443, true, "wss-primary.slack.com").Allowed);
        Assert.False(controller.EvaluateContainerConnection(source, sinkhole, 443, true, "slack.com.example.test").Allowed);
        Assert.False(controller.EvaluateContainerConnection(source, sinkhole, 8443, true, "slack.com").Allowed);
        Assert.False(controller.EvaluateContainerConnection(source, sinkhole, 443, false, null).Allowed);
        Assert.False(controller.EvaluateContainerConnection(source, "192.0.2.2", 443, true, "slack.com").Allowed);
        policy.SetBrake(admin, "system", null, on: true);
        Assert.Equal("brake_system", controller.EvaluateContainerConnection(source, sinkhole, 443, true, "slack.com").Reason);
    }
}
