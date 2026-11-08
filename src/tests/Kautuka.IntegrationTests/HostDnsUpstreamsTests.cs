using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class HostDnsUpstreamsTests
{
    [Fact]
    public void FollowsUsableHostIpv4AndPreservesOrderWithoutDuplicates()
        => Assert.Equal(["10.0.0.53", "169.254.169.254"], HostDnsUpstreams.Resolve(
            "nameserver 10.0.0.53\nnameserver 10.0.0.53 # duplicate\nnameserver 2001:db8::1\nnameserver 169.254.169.254\n", null));

    [Fact]
    public void UsesSystemdUpstreamsWhenOnlyTheLoopbackStubIsPresent()
        => Assert.Equal(["192.168.1.1"], HostDnsUpstreams.Resolve("nameserver 127.0.0.53\n", "nameserver 192.168.1.1\n"));

    [Fact]
    public void DoesNotUseAStaleSystemdFileForAnIpv6OnlyResolver()
        => Assert.Throws<InvalidOperationException>(() => HostDnsUpstreams.Resolve("nameserver 2001:db8::1\n", "nameserver 192.168.1.1\n"));

    [Theory]
    [InlineData("")]
    [InlineData("nameserver 2001:db8::1\n")]
    [InlineData("nameserver not-an-address\n")]
    [InlineData("nameserver 10.0.0.1 extra\n")]
    public void InvalidOrIpv6OnlyResolutionDoesNotFallBackToPublicDns(string content)
        => Assert.Throws<InvalidOperationException>(() => HostDnsUpstreams.Resolve(content, null));

    [Fact]
    public void ReloadCandidateHasAnIsolatedUniqueCanaryAndRequestedUpstream()
    {
        CoreDnsConfiguration first = CoreDnsConfiguration.Create(["10.0.0.53"]);
        CoreDnsConfiguration second = CoreDnsConfiguration.Create(["10.0.0.54"]);
        Assert.NotEqual(first.CanaryName, second.CanaryName);
        Assert.Contains("kautuka-dns-readiness.invalid:53 {", first.Corefile, StringComparison.Ordinal);
        Assert.Contains(first.CanaryAddress + " " + first.CanaryName, first.Corefile, StringComparison.Ordinal);
        Assert.Contains("ttl 1", first.Corefile, StringComparison.Ordinal);
        Assert.Contains("forward . 10.0.0.53", first.Corefile, StringComparison.Ordinal);
        Assert.DoesNotContain("fallthrough", first.Corefile, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => CoreDnsConfiguration.Create([]));
    }

    [Fact]
    public void TransactionStateRetainsExactLastGoodAndPendingConfiguration()
    {
        CoreDnsConfiguration old = CoreDnsConfiguration.Create(["10.0.0.53"]);
        CoreDnsConfiguration candidate = CoreDnsConfiguration.Create(["10.0.0.54"]);
        var state = new DnsTransactionState(new DnsRevision(old.Corefile, old.CanaryName, old.CanaryAddress, ["10.0.0.53"]),
            new DnsRevision(candidate.Corefile, candidate.CanaryName, candidate.CanaryAddress, ["10.0.0.54"]));
        DnsTransactionState restored = DnsTransactionState.Parse(state.Serialize());
        Assert.Equal(old.Corefile, restored.LastGood!.Corefile);
        Assert.Equal(old.CanaryName, restored.LastGood.CanaryName);
        Assert.Equal(candidate.Corefile, restored.Pending!.Corefile);
        Assert.Equal(["10.0.0.54"], restored.Pending.Upstreams);
        Assert.Throws<InvalidDataException>(() => DnsTransactionState.Parse(state.Serialize().Replace("kautuka-dns-readiness.invalid", "other.invalid", StringComparison.Ordinal)));
        Assert.Throws<InvalidDataException>(() => DnsTransactionState.Parse(state.Serialize().Replace("forward . 10.0.0.54", "forward . 10.0.0.55", StringComparison.Ordinal)));
    }

    [Fact]
    public void TransactionStorePersistsPendingAndThenAcceptedState()
    {
        string directory = Path.Combine(Path.GetTempPath(), "kautuka-dns-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new DnsTransactionStore(directory);
            Assert.Null(store.Read());
            CoreDnsConfiguration config = CoreDnsConfiguration.Create(["10.0.0.53"]);
            var revision = new DnsRevision(config.Corefile, config.CanaryName, config.CanaryAddress, ["10.0.0.53"]);
            store.Write(new DnsTransactionState(null, revision));
            Assert.Equal(config.Corefile, store.Read()!.Pending!.Corefile);
            store.Write(new DnsTransactionState(revision, null));
            Assert.Equal(config.CanaryName, store.Read()!.LastGood!.CanaryName);
            Assert.Null(store.Read()!.Pending);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void ResolverDetailsAreHiddenFromOrdinaryUsers()
    {
        const string detail = "host resolver 192.0.2.53 failed";
        Assert.Equal(detail, ContainerEgressRuntime.DnsErrorForActor(detail, true));
        string message = Assert.IsType<string>(ContainerEgressRuntime.DnsErrorForActor(detail, false));
        Assert.DoesNotContain("192.0.2.53", message, StringComparison.Ordinal);
        Assert.Contains("may be impaired", message, StringComparison.Ordinal);
        Assert.Null(ContainerEgressRuntime.DnsErrorForActor(null, false));
    }
}
