using System.Text.Json;

using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class CoreServiceUpdateShutdownTests
{
    [Fact]
    public async Task ShutdownClosesProxyBeforeRetiringExactCoreIdsWithoutBootstrap()
    {
        var fixture = new ShutdownFixture();
        await fixture.Supervisor.StopForUpdateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "stop:" + fixture.Ids["envoy"], "rm:" + fixture.Ids["webhost"],
            "rm:" + fixture.Ids["gateway"], "rm:" + fixture.Ids["channelhost"], "rm:" + fixture.Ids["credentialshost"] }, fixture.Mutations);
        Assert.DoesNotContain(fixture.Containers, pair => pair.Key != "envoy");
        Assert.False(fixture.ProxyRunning);
        Assert.DoesNotContain(fixture.Calls, args => args[0] is "start" or "create" or "pull" or "load" or "network" or "image");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UncertainOrForeignProxyPreventsCoreRemoval(bool foreign)
    {
        var fixture = new ShutdownFixture { ForeignProxy = foreign, ProxyRefusesStop = !foreign };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Supervisor.StopForUpdateAsync(TestContext.Current.CancellationToken));
        Assert.DoesNotContain(fixture.Mutations, mutation => mutation.StartsWith("rm:", StringComparison.Ordinal));
        if (foreign) Assert.Empty(fixture.Mutations);
    }

    [Fact]
    public async Task MissingProxyAndCoreContainersAreAnIdempotentOfflineStop()
    {
        var fixture = new ShutdownFixture();
        fixture.Containers.Clear();
        await fixture.Supervisor.StopForUpdateAsync(TestContext.Current.CancellationToken);
        Assert.Empty(fixture.Mutations);
        Assert.All(fixture.Calls, args => Assert.Contains(args[0], new[] { "inspect", "container" }));
    }

    [Fact]
    public async Task ForeignCoreContainerIsPreservedWithProxyStopped()
    {
        var fixture = new ShutdownFixture { ForeignCore = "gateway" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Supervisor.StopForUpdateAsync(TestContext.Current.CancellationToken));
        Assert.False(fixture.ProxyRunning);
        Assert.DoesNotContain("rm:" + fixture.Ids["gateway"], fixture.Mutations);
        Assert.DoesNotContain("rm:" + fixture.Ids["credentialshost"], fixture.Mutations);
    }

    private sealed class ShutdownFixture
    {
        internal Dictionary<string, string> Ids { get; } = new[] { "envoy", "webhost", "gateway", "channelhost", "credentialshost" }
            .Select((name, index) => (name, id: new string((char)('a' + index), 64))).ToDictionary(item => item.name, item => item.id);
        internal Dictionary<string, string> Containers { get; }
        internal List<string[]> Calls { get; } = [];
        internal List<string> Mutations { get; } = [];
        internal bool ProxyRunning { get; private set; } = true;
        internal bool ForeignProxy { get; init; }
        internal bool ProxyRefusesStop { get; init; }
        internal string? ForeignCore { get; init; }
        internal CoreServiceSupervisor Supervisor { get; }

        internal ShutdownFixture()
        {
            Containers = new(Ids);
            var runner = new PodmanCommandRunner((args, _) =>
            {
                Calls.Add([.. args]);
                string? service = Containers.FirstOrDefault(pair => pair.Value == args[^1]
                    || (pair.Key == "envoy" ? "kautuka-egress-envoy" : "kautuka-" + pair.Key) == args[^1]).Key;
                if (args[0] == "container" && args[1] == "exists")
                    return Task.FromResult(new PodmanCommandResult(service is null ? 1 : 0, "", ""));
                if (args[0] == "inspect")
                {
                    if (service is null) return Task.FromResult(new PodmanCommandResult(1, "", "absent"));
                    var labels = service == "envoy"
                        ? new Dictionary<string, string> { ["kautuka.egress"] = ForeignProxy ? "false" : "true" }
                        : new Dictionary<string, string>
                        {
                            ["kautuka.managed"] = "manager",
                            ["kautuka.role"] = "core",
                            ["kautuka.core-service"] = service == ForeignCore ? "unrecognized" : service
                        };
                    string json = JsonSerializer.Serialize(new[] { new { Id = Ids[service], Config = new { Labels = labels },
                        HostConfig = new { }, State = new { Status = "running", Running = service != "envoy" || ProxyRunning } } });
                    return Task.FromResult(new PodmanCommandResult(0, json, ""));
                }
                if (service is null) throw new InvalidOperationException("Unexpected missing mutation target.");
                if (args[0] == "stop") { Mutations.Add("stop:" + args[^1]); if (!ProxyRefusesStop) ProxyRunning = false; }
                else if (args[0] == "rm") { Mutations.Add("rm:" + args[^1]); Containers.Remove(service); }
                else throw new InvalidOperationException("Unexpected native operation: " + args[0]);
                return Task.FromResult(new PodmanCommandResult(0, "", ""));
            });
            var egress = new ContainerEgressRuntime(runner, new ContainerEgressOptions(), RuntimeImageAllowlist.Empty, "unused");
            Supervisor = new CoreServiceSupervisor(runner, egress, RuntimeImageAllowlist.Empty,
                new CoreServicePaths("unused", "unused", "unused", "unused", "unused", "unused"), new CoreServiceOptions());
        }
    }
}
