using Kautuka.NetworkAccessController;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class ApplianceChannelProviderNetworkPathTests
{
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
        foreach (string host in new[] { "api.telegram.org", "slack.com", "wss-primary.slack.com", "files.slack.com" })
        {
            var decision = controller.EvaluateContainerConnection(source, sinkhole, 443, true, host);
            Assert.True(decision.Allowed);
            Assert.Equal(Kautuka.NetworkAccessController.NetworkAccessController.ClusterDfpPublic, decision.Cluster);
        }
        Assert.False(controller.EvaluateContainerConnection(source, sinkhole, 443, true, "slack.com.example.test").Allowed);
        Assert.False(controller.EvaluateContainerConnection(source, sinkhole, 8443, true, "slack.com").Allowed);
        Assert.False(controller.EvaluateContainerConnection(source, sinkhole, 443, false, null).Allowed);
        Assert.False(controller.EvaluateContainerConnection(source, "192.0.2.2", 443, true, "slack.com").Allowed);
        policy.SetBrake(new NetworkAccessActor("admin", IsAdmin: true), "system", null, on: true);
        Assert.Equal("brake_system", controller.EvaluateContainerConnection(source, sinkhole, 443, true, "slack.com").Reason);
    }
}
