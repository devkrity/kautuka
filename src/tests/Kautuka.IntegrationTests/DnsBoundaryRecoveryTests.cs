using System.Reflection;

using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class DnsBoundaryRecoveryTests
{
    [Fact]
    public async Task RejectedOverlappingResolverDoesNotPoisonLaterBoundaryUpdate()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-dns-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "egress", "config"));
        int applied = 0;
        var runner = new PodmanCommandRunner((command, _) =>
        {
            string output = "";
            if (command[0] == "inspect")
                output = """[{"NetworkSettings":{"Networks":{"kautuka-outside":{"IPAddress":"10.247.0.3"}}}}]""";
            else if (command.Contains("/usr/sbin/nft") && command.Contains("-j")) output = """{"nftables":[]}""";
            else if (command.Contains("/usr/sbin/nft") && command.Contains("-f")) applied++;
            else if (command.Contains("/usr/sbin/ip") && command.Contains("route")) output = """[{"gateway":"10.247.0.1"}]""";
            else if (command.Contains("/usr/sbin/ip") && command.Contains("show")) output = "[]";
            else if (command.Contains("/usr/bin/cat")) output = "0\n";
            return Task.FromResult(new PodmanCommandResult(0, output, "", ProcessTerminationReason.Completed));
        });
        try
        {
            using var runtime = new ContainerEgressRuntime(runner, new ContainerEgressOptions { Enabled = true, FollowHostDns = true }, RuntimeImageAllowlist.Empty, root);
            CoreDnsConfiguration config = CoreDnsConfiguration.Create(["10.0.0.53"]);
            var old = new DnsRevision(config.Corefile, config.CanaryName, config.CanaryAddress, ["10.0.0.53"]);
            Set(runtime, "ready", true);
            Set(runtime, "dnsLastGood", old);
            Set(runtime, "dnsTransaction", new DnsTransactionState(old, null));
            Set(runtime, "dnsBoundaryUpstreams", new[] { "10.0.0.53" });
            Set(runtime, "helperImage", new ResolvedRuntimeImage("localhost/fixture", "linux/amd64", "sha256:" + new string('a', 64)));
            Set(runtime, "infraId", new string('a', 64));
            Set(runtime, "outsideDefaultGateway", "10.247.0.1");
            Set(runtime, "outsideSourceAddress", "10.247.0.3");
            runtime.HostDnsReaderForTests = _ => Task.FromResult(new[] { "10.247.20.20" });

            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.RefreshHostDnsAsync(TestContext.Current.CancellationToken));
            Assert.Equal(new[] { "10.0.0.53" }, (string[])Get(runtime, "dnsBoundaryUpstreams")!);
            MethodInfo apply = typeof(ContainerEgressRuntime).GetMethod("ApplyBoundaryAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)apply.Invoke(runtime, [TestContext.Current.CancellationToken])!;
            Assert.Equal(1, applied);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void Set(ContainerEgressRuntime runtime, string name, object value)
        => typeof(ContainerEgressRuntime).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, value);

    private static object? Get(ContainerEgressRuntime runtime, string name)
        => typeof(ContainerEgressRuntime).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime);
}
