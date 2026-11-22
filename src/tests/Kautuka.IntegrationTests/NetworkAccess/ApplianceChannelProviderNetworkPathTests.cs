using System.Net;
using System.Net.WebSockets;

using Kautuka.NetworkAccess.Client;
using Kautuka.NetworkAccessController;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class ApplianceChannelProviderNetworkPathTests
{
    [Fact]
    public async Task PausingSlackBlocksReconnectionWhileAnOpenWebSocketCanStillExchangeFrames()
    {
        DirectoryInfo directory = Directory.CreateTempSubdirectory("kautuka-slack-pause-");
        try { await VerifySlackPauseAsync(directory.FullName); }
        finally { directory.Delete(recursive: true); }
    }

    private static async Task VerifySlackPauseAsync(string directory)
    {
        using var policy = new NetworkAccessPolicyStore(Path.Combine(directory, "policy.json"));
        var controller = new Kautuka.NetworkAccessController.NetworkAccessController(policy, new WorkloadRegistry());
        var admin = new NetworkAccessActor("admin", IsAdmin: true);
        var rule = new RuleWriteSpec("agent", "system", "channelhost", null, "slack.com", "host", 443, true);
        policy.SetRule(admin, rule, policy.Revision);
        var checker = new NetworkAccessWebSocketChecker(new LoopbackSlackPolicy(controller));

        WebApplicationBuilder builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.UseKestrelCore().UseSockets().ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using WebApplication server = builder.Build();
        server.UseWebSockets();
        server.Run(async context =>
        {
            using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
            var buffer = new byte[8];
            ValueWebSocketReceiveResult frame = await socket.ReceiveAsync(buffer.AsMemory(), context.RequestAborted);
            await socket.SendAsync(buffer.AsMemory(0, frame.Count), frame.MessageType, frame.EndOfMessage, context.RequestAborted);
        });

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        CancellationToken token = timeout.Token;
        await server.StartAsync(token);
        var endpoint = new UriBuilder(server.Urls.Single()) { Scheme = "ws" }.Uri;
        using var established = new ClientWebSocket();
        await checker.ConnectAsync(established, endpoint, token);

        policy.SetRule(admin, rule with { Allowed = false }, policy.Revision);
        using var reconnect = new ClientWebSocket();
        await Assert.ThrowsAsync<NetworkAccessDeniedException>(() => checker.ConnectAsync(reconnect, endpoint, token));
        await established.SendAsync(new byte[] { 42 }.AsMemory(), WebSocketMessageType.Binary, true, token);
        var reply = new byte[8];
        ValueWebSocketReceiveResult received = await established.ReceiveAsync(reply.AsMemory(), token);
        Assert.Equal(WebSocketMessageType.Binary, received.MessageType);
        Assert.Equal(1, received.Count);
        Assert.Equal(42, reply[0]);
    }

    private sealed class LoopbackSlackPolicy(Kautuka.NetworkAccessController.NetworkAccessController controller) : INetworkAccessChecker
    {
        public Task<NetworkAccessCheckResult> CheckAsync(OutboundTarget target, CancellationToken cancellationToken = default)
        {
            // The WebSocket server is local. Apply production ChannelHost policy to the Slack endpoint it represents.
            var decision = controller.EvaluateOutboundCheck(new OutboundCheckInput(
                "system", "channelhost", null, "wss-primary.slack.com", 443, null, null));
            return Task.FromResult(new NetworkAccessCheckResult(decision.Allowed, decision.Reason));
        }
    }

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
