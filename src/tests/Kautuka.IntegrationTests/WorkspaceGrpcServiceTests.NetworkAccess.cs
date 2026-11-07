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
