using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests;

public sealed class NetworkAccessApprovalPressureTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "nac-request-pressure-" + Guid.NewGuid().ToString("N"));
    private static readonly NetworkAccessActor owner = new("owner", false);
    private static readonly NetworkAccessIdentity identity = new("agent", "owner", "agent", "connector", "connector");
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact]
    public void PendingRetryFloodDoesNotRewriteDurableStateAndFirstRequestSurvivesRestart()
    {
        string path = Path.Combine(root, "state.json");
        int flushes = 0;
        var store = new NetworkAccessPolicyStore(path, _ => Interlocked.Increment(ref flushes));
        DateTimeOffset first = DateTimeOffset.UtcNow;
        store.ObserveDenied(identity, "example.com", 443, first);
        byte[] durable = File.ReadAllBytes(path);
        int firstFlushes = flushes;
        Assert.True(firstFlushes > 0);

        Parallel.For(0, 1000, index => store.ObserveDenied(identity, "EXAMPLE.COM.", 443, first.AddSeconds(index + 1)));

        Assert.Equal(firstFlushes, flushes);
        Assert.Equal(durable, File.ReadAllBytes(path));
        NetworkAccessRequest pending = Assert.Single(store.Read(owner).Requests);
        Assert.Equal(1001, pending.Attempts);
        Assert.Equal(first.AddSeconds(1000), pending.LastSeenAt);
        NetworkAccessRequest restarted = Assert.Single(new NetworkAccessPolicyStore(path).Read(owner).Requests);
        Assert.Equal(1, restarted.Attempts); // Retry counters are explicitly best-effort.
        Assert.Equal("pending", restarted.Status);
        Assert.Equal(pending.Id, restarted.Id);

        store.DecideRequest(owner, pending.Id, false, 0);
        NetworkAccessRequest blocked = Assert.Single(new NetworkAccessPolicyStore(path).Read(owner).Requests);
        Assert.Equal(1001, blocked.Attempts); // A real decision flushes the current counters.
        Assert.Equal("blocked", blocked.Status);
    }

    [Fact]
    public void DistinctDestinationFloodIsBoundedAndDoesNotLoseExistingPendingRequests()
    {
        string path = Path.Combine(root, "state.json");
        var store = new NetworkAccessPolicyStore(path, _ => { });
        DateTimeOffset first = DateTimeOffset.UtcNow;
        for (int index = 0; index < 500; index++)
            store.ObserveDenied(identity, $"host-{index}.example.com", 443, first);
        Assert.True(store.RequestCapacityReached);
        byte[] durable = File.ReadAllBytes(path);

        Assert.Throws<InvalidOperationException>(() => store.ObserveDenied(identity, "overflow.example.com", 443, first));

        Assert.Equal(durable, File.ReadAllBytes(path));
        Assert.Equal(500, new NetworkAccessPolicyStore(path).Read(owner).Requests.Count);
        store.ObserveDenied(identity, "host-0.example.com", 443, first.AddSeconds(1));
        Assert.Equal(2, store.Read(owner).Requests.Single(item => item.Destination == "host-0.example.com").Attempts);
    }
}
