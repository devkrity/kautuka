using Kautuka.NetworkAccessController;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class U2_PolicyStoreTests : IDisposable
{
    private readonly string tempDir;

    public U2_PolicyStoreTests()
    {
        tempDir = Path.Combine(Path.GetTempPath(), "kautuka-nac-u2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(tempDir))
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void U2_AtomicWrite_SurvivesCrashBetweenTempWriteAndRename_WithNegativeControl()
    {
        string statePath = Path.Combine(tempDir, "atomic", "state.json");
        var admin = new NetworkAccessActor("admin-1", IsAdmin: true);

        var initialStore = new NetworkAccessPolicyStore(statePath);
        initialStore.SetRule(
            admin,
            new RuleWriteSpec("agent", "user-1", "agent-1", null, "first.example.com", "host", 443, Allowed: true),
            expectedRevision: 0);
        Assert.Equal(1, initialStore.Revision);

        // Simulate a kill/crash after the temp file is flushed but BEFORE File.Move renames it over state.json
        var crashingStore = new NetworkAccessPolicyStore(
            statePath,
            NetworkAccessPolicyStore.FlushDirectory,
            beforeRenameHook: _ => throw new IOException("Simulated crash before rename"));

        Assert.Throws<IOException>(() =>
            crashingStore.SetRule(
                admin,
                new RuleWriteSpec("agent", "user-1", "agent-1", null, "second.example.com", "host", 443, Allowed: true),
                expectedRevision: 1));

        // Reload from disk: must have the intact old state (revision 1, only first.example.com)
        var reloadedStore = new NetworkAccessPolicyStore(statePath);
        (long rev, IReadOnlyList<NetworkAccessRuleView> userRules) = reloadedStore.ListRules(new NetworkAccessActor("user-1", IsAdmin: false));
        Assert.Equal(1, rev);
        Assert.Single(userRules);
        Assert.Equal("first.example.com", userRules[0].Rule.Destination);

        // Negative control (.NC): without the crash hook, the second write succeeds and persists revision 2
        reloadedStore.SetRule(
            admin,
            new RuleWriteSpec("agent", "user-1", "agent-1", null, "second.example.com", "host", 443, Allowed: true),
            expectedRevision: 1);
        var verifyStore = new NetworkAccessPolicyStore(statePath);
        (long ncRev, IReadOnlyList<NetworkAccessRuleView> ncRules) = verifyStore.ListRules(new NetworkAccessActor("user-1", IsAdmin: false));
        Assert.Equal(2, ncRev);
        Assert.Equal(2, ncRules.Count);
    }

    [Fact]
    public void U2_MalformedFile_RefusesToLoad_WithNegativeControl()
    {
        string statePath = Path.Combine(tempDir, "corrupt", "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        File.WriteAllText(statePath, "{ \"schemaVersion\": 1, \"revision\": \"not-a-number\" ");

        var corruptStore = new NetworkAccessPolicyStore(statePath);
        Assert.Throws<InvalidDataException>(() => corruptStore.EnsureLoaded());

        // Negative control (.NC): replace with valid JSON -> loads cleanly
        File.Delete(statePath);
        var cleanStore = new NetworkAccessPolicyStore(statePath);
        cleanStore.EnsureLoaded();
        Assert.Equal(0, cleanStore.Revision);
    }

    [Fact]
    public void U2_RequestDedupe_100AttemptsProduceOneRequestAndOneDiskWrite_AndCapBehaviour_WithNegativeControl()
    {
        string statePath = Path.Combine(tempDir, "dedupe", "state.json");
        var store = new NetworkAccessPolicyStore(statePath);
        var user = new NetworkAccessActor("user-1", IsAdmin: false);

        for (int i = 0; i < 100; i++)
        {
            (string? reqId, bool overflow) = store.RecordOrUpdatePendingRequest(
                "user-1",
                "agent-1",
                connectorId: null,
                systemwide: false,
                attribution: "user-agent",
                destKind: "host",
                destination: "dedupe.example.com",
                port: 443,
                actorKind: "agent",
                origin: "container");
            Assert.False(overflow);
            Assert.NotNull(reqId);
        }

        Assert.Equal(1, store.DiskWriteCount);
        IReadOnlyList<NetworkAccessRequestView> requests = store.ListRequests(user).Requests;
        Assert.Single(requests);
        Assert.Equal(100, requests[0].Request.Attempts);

        // Negative control (.NC): a different destination triggers a 2nd disk write and a 2nd request
        store.RecordOrUpdatePendingRequest(
            "user-1",
            "agent-1",
            connectorId: null,
            systemwide: false,
            attribution: "user-agent",
            destKind: "host",
            destination: "second.example.com",
            port: 443,
            actorKind: "agent",
            origin: "container");
        Assert.Equal(2, store.DiskWriteCount);
        Assert.Equal(2, store.ListRequests(user).Requests.Count);

        // Fill remaining 498 slots to reach the 500 pending request cap
        for (int i = 2; i < NetworkAccessPolicyStore.MaxPendingRequests; i++)
        {
            (_, bool overflow) = store.RecordOrUpdatePendingRequest(
                "user-1",
                "agent-1",
                connectorId: null,
                systemwide: false,
                attribution: "user-agent",
                destKind: "host",
                destination: $"host-{i}.example.com",
                port: 443,
                actorKind: "agent",
                origin: "container");
            Assert.False(overflow);
        }

        // 501st distinct pending request overflows
        (string? overflowId, bool isOverflow) = store.RecordOrUpdatePendingRequest(
            "user-1",
            "agent-1",
            connectorId: null,
            systemwide: false,
            attribution: "user-agent",
            destKind: "host",
            destination: "overflow.example.com",
            port: 443,
            actorKind: "agent",
            origin: "container");
        Assert.True(isOverflow);
        Assert.Null(overflowId);

        // Negative control (.NC): resolve one pending request -> 501st can now evict the resolved request and succeed
        var admin = new NetworkAccessActor("admin-1", IsAdmin: true);
        store.DecideRequest(admin, requests[0].Request.Id, allow: true, scope: "agent", expectedRevision: store.Revision);
        (string? afterResolveId, bool afterResolveOverflow) = store.RecordOrUpdatePendingRequest(
            "user-1",
            "agent-1",
            connectorId: null,
            systemwide: false,
            attribution: "user-agent",
            destKind: "host",
            destination: "overflow.example.com",
            port: 443,
            actorKind: "agent",
            origin: "container");
        Assert.False(afterResolveOverflow);
        Assert.NotNull(afterResolveId);
    }

    [Fact]
    public void U2_RevisionConflict_AndRuleDestinationValidation_WithNegativeControl()
    {
        string statePath = Path.Combine(tempDir, "validation", "state.json");
        var store = new NetworkAccessPolicyStore(statePath);
        var admin = new NetworkAccessActor("admin-1", IsAdmin: true);

        // Stale expectedRevision fails
        Assert.Throws<InvalidOperationException>(() =>
            store.SetRule(
                admin,
                new RuleWriteSpec("agent", "user-1", "agent-1", null, "valid.example.com", "host", 443, Allowed: true),
                expectedRevision: 99));

        // Negative control (.NC): matching expectedRevision (0) succeeds
        store.SetRule(
            admin,
            new RuleWriteSpec("agent", "user-1", "agent-1", null, "valid.example.com", "host", 443, Allowed: true),
            expectedRevision: 0);

        // Rule validation refuses metadata, workload subnet 10.247.x, sinkhole 198.18.x, and RFC1918 when allowLan == false
        (string DestKind, string Dest, bool AllowLan)[] forbidden =
        [
            ("ip", "169.254.169.254", true),
            ("ip", "168.63.129.16", true),
            ("ip", "100.100.100.200", true),
            ("host", "metadata.google.internal", true),
            ("ip", "10.247.1.5", true),
            ("ip", "198.18.250.250", true),
            ("ip", "198.19.0.1", true),
            ("ip", "127.0.0.1", true),
            ("ip", "192.168.1.10", false),
            ("ip", "10.0.0.5", false),
            ("ip", "172.16.0.5", false),
        ];

        foreach ((string kind, string dest, bool allowLan) in forbidden)
        {
            Assert.Throws<ArgumentException>(() =>
                store.SetRule(
                    admin,
                    new RuleWriteSpec("agent", "user-1", "agent-1", null, dest, kind, 443, Allowed: true, AllowLan: allowLan),
                    store.Revision));
        }

        // Negative control (.NC): RFC1918 with allowLan = true by admin succeeds, and public IP succeeds
        store.SetRule(
            admin,
            new RuleWriteSpec("agent", "user-1", "agent-1", null, "192.168.1.10", "ip", 443, Allowed: true, AllowLan: true),
            store.Revision);
        store.SetRule(
            admin,
            new RuleWriteSpec("agent", "user-1", "agent-1", null, "93.184.216.34", "ip", 443, Allowed: true, AllowLan: false),
            store.Revision);
    }
}
