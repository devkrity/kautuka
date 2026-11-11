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
        try
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best effort on Windows
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
    public async Task U2_RequestDedupe_100AttemptsProduceOneRequestAndOneDiskWrite_AndCapBehaviour_WithNegativeControl()
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

        // Hot-path pending request writes coalesce asynchronously; no synchronous blocking write occurred yet.
        Assert.Equal(0, store.DiskWriteCount);
        await store.FlushPendingWritesAsync();
        Assert.Equal(1, store.DiskWriteCount);
        IReadOnlyList<NetworkAccessRequestView> requests = store.ListRequests(user).Requests;
        Assert.Single(requests);
        Assert.Equal(100, requests[0].Request.Attempts);

        // Negative control (.NC): a different destination triggers a 2nd coalesced write and a 2nd request
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
        await store.FlushPendingWritesAsync();
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

    [Fact]
    public async Task V7_27_BackgroundWriter_NeverOverwritesNewerSyncSave_WithNegativeControl()
    {
        string statePath = Path.Combine(tempDir, "v7-27-race", "state.json");
        var admin = new NetworkAccessActor("admin-1", IsAdmin: true);
        using var enteredHook = new ManualResetEventSlim(false);
        using var releaseBackground = new ManualResetEventSlim(false);
        int hookCalls = 0;

        using var store = new NetworkAccessPolicyStore(
            statePath,
            _ => { },
            beforeBackgroundDiskLockHook: () =>
            {
                if (Interlocked.Increment(ref hookCalls) == 1)
                {
                    enteredHook.Set();
                    Assert.True(releaseBackground.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
                }
            });

        // 1. Trigger a background pending-request write (captures seq = 1 and pauses before diskWriteLock).
        store.RecordOrUpdatePendingRequest(
            "user-1",
            "agent-1",
            connectorId: null,
            systemwide: false,
            attribution: "user-agent",
            destKind: "host",
            destination: "race1.example.com",
            port: 443,
            actorKind: "agent",
            origin: "container");

        Assert.True(enteredHook.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        // 2. While the stale seq=1 background write waits to acquire diskWriteLock, perform sync writes (brake + rule).
        store.SetBrake(admin, scope: "system", targetUserId: null, on: true);
        store.SetRule(
            admin,
            new RuleWriteSpec("agent", "user-1", "agent-1", null, "approved.example.com", "host", 443, Allowed: true),
            store.Revision);
        long writesAfterSync = store.DiskWriteCount;
        Assert.Equal(2, writesAfterSync);

        // 3. Release the older background write; seq guard must drop the stale write so DiskWriteCount stays at 2.
        releaseBackground.Set();
        await store.FlushPendingWritesAsync();
        Assert.Equal(writesAfterSync, store.DiskWriteCount);

        using (var reopened = new NetworkAccessPolicyStore(statePath))
        {
            Assert.True(reopened.GetBrake(admin).System);
            Assert.Contains(
                reopened.ListRules(admin).Rules,
                r => r.Rule.Destination == "approved.example.com" && r.Rule.Allowed);
        }

        // Negative control (.NC): background saving is not suppressed afterward — a newer request DOES persist cleanly.
        store.RecordOrUpdatePendingRequest(
            "user-1",
            "agent-1",
            connectorId: null,
            systemwide: false,
            attribution: "user-agent",
            destKind: "host",
            destination: "race2.example.com",
            port: 443,
            actorKind: "agent",
            origin: "container");
        await store.FlushPendingWritesAsync();
        Assert.Equal(writesAfterSync + 1, store.DiskWriteCount);

        using var reopenedAfterNext = new NetworkAccessPolicyStore(statePath);
        Assert.True(reopenedAfterNext.GetBrake(admin).System);
        Assert.Contains(
            reopenedAfterNext.ListRequests(admin).Requests,
            r => r.Request.Destination == "race2.example.com");
    }

    [Fact]
    public async Task V7_27_BackgroundWriter_SurvivesDiskErrorAndCleansTempFiles_WithNegativeControl()
    {
        string dir = Path.Combine(tempDir, "v7-27-error");
        string statePath = Path.Combine(dir, "state.json");
        var admin = new NetworkAccessActor("admin-1", IsAdmin: true);
        int failRemaining = 1;

        using var store = new NetworkAccessPolicyStore(
            statePath,
            _ => { },
            beforeRenameHook: _ =>
            {
                if (Interlocked.Decrement(ref failRemaining) >= 0)
                {
                    throw new IOException("Simulated transient disk failure");
                }
            });

        // 1. First background write hits transient error; error is counted and .tmp file is cleaned up.
        store.RecordOrUpdatePendingRequest(
            "user-1",
            "agent-1",
            connectorId: null,
            systemwide: false,
            attribution: "user-agent",
            destKind: "host",
            destination: "err1.example.com",
            port: 443,
            actorKind: "agent",
            origin: "container");
        await store.FlushPendingWritesAsync();
        Assert.Equal(1, store.BackgroundWriteErrorCount);
        Assert.Equal(0, store.DiskWriteCount);
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));

        // 2. Subsequent background write continues working and flushes both err1 and err2 to disk.
        store.RecordOrUpdatePendingRequest(
            "user-1",
            "agent-1",
            connectorId: null,
            systemwide: false,
            attribution: "user-agent",
            destKind: "host",
            destination: "err2.example.com",
            port: 443,
            actorKind: "agent",
            origin: "container");
        await store.FlushPendingWritesAsync();
        Assert.Equal(1, store.DiskWriteCount);
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));

        using (var reopened = new NetworkAccessPolicyStore(statePath))
        {
            var destinations = reopened.ListRequests(admin).Requests.Select(r => r.Request.Destination).ToHashSet();
            Assert.Contains("err1.example.com", destinations);
            Assert.Contains("err2.example.com", destinations);
        }

        // Negative control (.NC): synchronous durability writes (SetBrake) fail closed by throwing on disk error while still cleaning .tmp files.
        Interlocked.Exchange(ref failRemaining, 1);
        Assert.Throws<IOException>(() => store.SetBrake(admin, scope: "system", targetUserId: null, on: true));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
    }
}
