using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using Grpc.Core;

using Kautuka.Manager.Grpc;
using Kautuka.Manager.Hosting;
using Kautuka.Manager.Protos;
using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;

using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed partial class WorkspaceGrpcServiceTests
{
    [Fact]
    public async Task ReadRejectsUnreleasedComponentScopedPolicy()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-network-conflict-rpc-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(root, "egress", "policy", "state.json");
            var store = new NetworkAccessPolicyStore(path);
            var actor = new NetworkAccessActor("owner", true);
            store.SetRule(actor, new("agent", "owner", "agent", "connector", "one"), "service.example.com", 443, true, 0);
            JsonObject state = JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))!.AsObject();
            // Add a pre-scope-normalization Block, retaining a valid legacy key.
            string blockId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                string.Join('\0', "agent", "owner", "agent", "remote-connector", "owner::legacy", "service.example.com", "443"))));
            state["rules"]!.AsArray().Add(new JsonObject
            {
                ["id"] = blockId,
                ["identity"] = new JsonObject { ["scopeType"] = "agent", ["userId"] = "owner", ["agentId"] = "agent", ["role"] = "remote-connector", ["componentId"] = "owner::legacy" },
                ["destination"] = "service.example.com",
                ["port"] = 443,
                ["allowed"] = false,
                ["updatedAt"] = "2026-01-01T00:00:00+00:00",
            });
            await File.WriteAllTextAsync(path, state.ToJsonString(), TestContext.Current.CancellationToken);

            var runner = new PodmanCommandRunner((_, _) => throw new InvalidOperationException("Read must not bootstrap Podman."));
            using var runtime = new ContainerEgressRuntime(runner, new ContainerEgressOptions { Enabled = true }, RuntimeImageAllowlist.Empty, root);
            var service = new WorkspaceGrpcService(null!, new WorkspaceDesiredStateStore(), new WorkspaceReadinessGate(), new WorkspaceExecTracker(),
                new StubRuntimeHealthProbe(true), null!, null!, null!, NullLogger<WorkspaceGrpcService>.Instance, runtime);
            await Assert.ThrowsAsync<InvalidDataException>(() => service.GetNetworkAccessPolicy(
                new() { Actor = new() { UserId = "owner", IsAdmin = true } }, TestServerCallContext.Instance));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ReadProjectsEffectiveGrantRatherThanLegacyPrivateAllowIntent()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-effective-network-rpc-" + Guid.NewGuid().ToString("N"));
        try
        {
            string path = Path.Combine(root, "egress", "policy", "state.json");
            var store = new NetworkAccessPolicyStore(path);
            var actor = new NetworkAccessActor("owner", true);
            var identity = new NetworkAccessIdentity("agent", "owner", "agent", "connector", "tools");
            store.SetRule(actor, identity, "10.0.0.7", 11434, true, 0, true);
            await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken)).Replace("\"privateNetworkApprovalExplicit\": true", "\"legacyField\": true", StringComparison.Ordinal), TestContext.Current.CancellationToken);
            store = new NetworkAccessPolicyStore(path);
            store.SetRule(actor, identity, "example.com", 443, true, 1);
            store.SetRule(actor, identity, "10.0.0.8", 11434, true, 2, true);
            var runner = new PodmanCommandRunner((_, _) => throw new InvalidOperationException("Read must not bootstrap Podman."));
            using var runtime = new ContainerEgressRuntime(runner, new ContainerEgressOptions { Enabled = true }, RuntimeImageAllowlist.Empty, root);
            var service = new WorkspaceGrpcService(null!, new WorkspaceDesiredStateStore(), new WorkspaceReadinessGate(), new WorkspaceExecTracker(),
                new StubRuntimeHealthProbe(true), null!, null!, null!, NullLogger<WorkspaceGrpcService>.Instance, runtime);
            NetworkAccessPolicyResponse response = await service.GetNetworkAccessPolicy(new() { Actor = new() { UserId = "owner", IsAdmin = true } }, TestServerCallContext.Instance);
            Assert.False(Assert.Single(response.Rules, rule => rule.Destination == "10.0.0.7").Allowed);
            Assert.True(Assert.Single(response.Rules, rule => rule.Destination == "example.com").Allowed);
            NetworkAccessRuleMessage approved = Assert.Single(response.Rules, rule => rule.Destination == "10.0.0.8");
            Assert.True(approved.Allowed);
            Assert.True(approved.AllowPrivateNetwork);
            Assert.True(Assert.Single(store.Read(actor).Rules, rule => rule.Destination == "10.0.0.7").Allowed);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task PrivateNetworkRequestChoiceIsAuthorizedAndServerDerived(bool admin, bool privateChoice, bool succeeds)
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-private-request-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runner = new PodmanCommandRunner((_, _) => throw new InvalidOperationException("Unready policy writes must not bootstrap Podman."));
            using var runtime = new ContainerEgressRuntime(runner, new ContainerEgressOptions { Enabled = true }, RuntimeImageAllowlist.Empty, root);
            var service = new WorkspaceGrpcService(null!, new WorkspaceDesiredStateStore(), new WorkspaceReadinessGate(), new WorkspaceExecTracker(),
                new StubRuntimeHealthProbe(true), null!, null!, null!, NullLogger<WorkspaceGrpcService>.Instance, runtime);
            var request = new NetworkAccessRuleRequest
            {
                Actor = new() { UserId = "owner", IsAdmin = admin },
                AllowPrivateNetwork = privateChoice,
                Rule = new() { Identity = new() { ScopeType = "agent", UserId = "owner", AgentId = "agent", Role = "connector", ComponentId = "tools" }, Destination = "example.com", Port = 443, Allowed = true },
            };
            if (!succeeds)
            {
                RpcException error = await Assert.ThrowsAsync<RpcException>(() => service.SetNetworkAccessRule(request, TestServerCallContext.Instance));
                Assert.Equal(StatusCode.PermissionDenied, error.StatusCode);
            }
            else
            {
                NetworkAccessPolicyResponse response = await service.SetNetworkAccessRule(request, TestServerCallContext.Instance);
                Assert.Equal(privateChoice, Assert.Single(response.Rules).AllowPrivateNetwork);
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ClientCannotForgePrivateNetworkEntitlement()
    {
        var service = new WorkspaceGrpcService(null!, new WorkspaceDesiredStateStore(), new WorkspaceReadinessGate(), new WorkspaceExecTracker(),
            new StubRuntimeHealthProbe(true), null!, null!, null!, NullLogger<WorkspaceGrpcService>.Instance);
        RpcException error = await Assert.ThrowsAsync<RpcException>(() => service.SetNetworkAccessRule(new()
        {
            Actor = new() { UserId = "owner" },
            Rule = new() { AllowPrivateNetwork = true, Destination = "10.0.0.1", Port = 443, Allowed = true },
        }, TestServerCallContext.Instance));
        Assert.Equal(StatusCode.InvalidArgument, error.StatusCode);
    }

    [Fact]
    public async Task NetworkPolicyPagesAreOwnerFilteredRevisionBoundAndIncludeSummaryCounts()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-network-rpc-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runner = new PodmanCommandRunner((_, _) => throw new InvalidOperationException("Policy reads must not bootstrap Podman."));
            using var runtime = new ContainerEgressRuntime(runner, new ContainerEgressOptions { Enabled = true }, RuntimeImageAllowlist.Empty, root);
            var actor = new NetworkAccessActor("owner", false);
            var identity = new NetworkAccessIdentity("agent", "owner", "agent", "connector", new string('界', 512));
            for (int index = 0; index < 55; index++)
                await runtime.SetNetworkAccessRuleAsync(actor, identity, $"host{index}.example.com", 443, true, index, TestContext.Current.CancellationToken);
            var service = new WorkspaceGrpcService(null!, new WorkspaceDesiredStateStore(), new WorkspaceReadinessGate(), new WorkspaceExecTracker(),
                new StubRuntimeHealthProbe(true), null!, null!, null!, NullLogger<WorkspaceGrpcService>.Instance, runtime);
            var actorMessage = new NetworkAccessActorMessage { UserId = actor.UserId };
            NetworkAccessPolicyResponse first = await service.GetNetworkAccessPolicy(new() { Actor = actorMessage }, TestServerCallContext.Instance);
            Assert.Equal(50, first.Rules.Count);
            Assert.Equal(55, first.RuleCount);
            Assert.Equal(50, first.NextOffset);
            Assert.True(first.CalculateSize() < 2 * 1024 * 1024);
            NetworkAccessPolicyResponse second = await service.GetNetworkAccessPolicy(new()
            {
                Actor = actorMessage,
                Offset = first.NextOffset,
                EnforceRevision = true,
                ExpectedRevision = first.Revision,
            }, TestServerCallContext.Instance);
            Assert.Equal(5, second.Rules.Count);
            Assert.Equal(55, first.Rules.Concat(second.Rules).Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
            NetworkAccessPolicyResponse summary = await service.GetNetworkAccessPolicy(new() { Actor = actorMessage, SummaryOnly = true }, TestServerCallContext.Instance);
            Assert.Empty(summary.Rules);
            Assert.Equal(55, summary.RuleCount);
            Assert.Equal(0, summary.NextOffset);
            NetworkAccessPolicyResponse other = await service.GetNetworkAccessPolicy(new()
            {
                Actor = new NetworkAccessActorMessage { UserId = "other" },
            }, TestServerCallContext.Instance);
            Assert.Empty(other.Rules);
            Assert.Equal(0, other.RuleCount);
            RpcException conflict = await Assert.ThrowsAsync<RpcException>(() => service.GetNetworkAccessPolicy(new()
            {
                Actor = actorMessage,
                Offset = 50,
                ExpectedRevision = 54,
                EnforceRevision = true,
            }, TestServerCallContext.Instance));
            Assert.Equal(StatusCode.Aborted, conflict.StatusCode);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
