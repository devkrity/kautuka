using Kautuka.NetworkAccessController;

using Microsoft.Extensions.Logging;

using System.Collections.Concurrent;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class U2_PolicyStoreTests : IDisposable
{
    [Fact]
    public void UserCanOnlyTightenAdminRuleWithoutRemovingOrRelaxingIt()
    {
        using var store = new NetworkAccessPolicyStore(Path.Combine(tempDir, "admin-rules.json"));
        var admin = new NetworkAccessActor("admin", true);
        var alice = new NetworkAccessActor("alice", false);
        store.SetUserPrivileges(admin, "alice", canApproveOwn: true);
        var allow = new RuleWriteSpec("agent", "alice", "secretary", null, "example.com", "host", 443, Allowed: true);
        var adminRule = store.SetRule(admin, allow, store.Revision).Rule.Rule;
        var registry = new WorkloadRegistry();
        registry.Register(new RegistryEntry("10.247.1.10", "alice", "secretary", "container", "agent-sandbox", null, "isolated", "normal", "user-agent", null, DateTimeOffset.UtcNow));
        var controller = new Kautuka.NetworkAccessController.NetworkAccessController(store, registry);
        var connection = new ContainerConnectionInput("10.247.1.10", "93.184.216.34", 443, "example.com");
        Assert.True(controller.EvaluateContainerConnection(connection).Allowed);
        Assert.Throws<UnauthorizedAccessException>(() => store.DeleteRule(alice, adminRule.Id, store.Revision));
        Assert.Throws<UnauthorizedAccessException>(() => store.SetRule(alice, allow, store.Revision, adminRule.Id));
        Assert.Throws<UnauthorizedAccessException>(() => store.SetRule(alice, allow with { Destination = "other.example", Allowed = false }, store.Revision, adminRule.Id));
        var block = store.SetRule(alice, allow with { Allowed = false }, store.Revision).Rule.Rule;
        Assert.NotEqual(adminRule.Id, block.Id);
        Assert.False(controller.EvaluateContainerConnection(connection).Allowed);
        Assert.Contains(store.ListRules(admin).Rules, r => r.Rule.Id == adminRule.Id && r.Rule.Allowed && r.Rule.DecidedByAdmin);
        using (var reopened = new NetworkAccessPolicyStore(Path.Combine(tempDir, "admin-rules.json")))
        {
            Assert.False(new Kautuka.NetworkAccessController.NetworkAccessController(reopened, registry).EvaluateContainerConnection(connection).Allowed);
            Assert.False(Assert.Single(reopened.ListRules(admin).Rules, r => r.Rule.Id == block.Id).Rule.DecidedByAdmin);
        }
        var userAllow = store.SetRule(alice, allow, store.Revision, block.Id).Rule.Rule;
        Assert.Equal(block.Id, userAllow.Id);
        Assert.True(userAllow.Allowed);
        Assert.False(userAllow.DecidedByAdmin);
        store.DeleteRule(alice, userAllow.Id, store.Revision);
        Assert.True(controller.EvaluateContainerConnection(connection).Allowed);
        Assert.Contains(store.ListRules(admin).Rules, r => r.Rule.Id == adminRule.Id && r.Rule.Allowed);
        var secondBlock = store.SetRule(alice, allow with { Allowed = false }, store.Revision).Rule.Rule;
        store.DeleteRule(alice, secondBlock.Id, store.Revision);
        Assert.True(controller.EvaluateContainerConnection(connection).Allowed);
    }

    [Fact]
    public void UserCannotOverrideAdministratorBlockThroughApproval()
    {
        using var store = new NetworkAccessPolicyStore(Path.Combine(tempDir, "admin-block.json"));
        var admin = new NetworkAccessActor("admin", true);
        var alice = new NetworkAccessActor("alice", false);
        store.SetUserPrivileges(admin, "alice", canApproveOwn: true);
        var spec = new RuleWriteSpec("agent", "alice", "secretary", null, "example.com", "host", 443, Allowed: false);
        var adminRule = store.SetRule(admin, spec, store.Revision).Rule.Rule;
        var ownBlock = store.SetRule(alice, spec, store.Revision).Rule.Rule;
        Assert.NotEqual(adminRule.Id, ownBlock.Id);
        store.DeleteRule(alice, ownBlock.Id, store.Revision);
        Assert.Throws<UnauthorizedAccessException>(() => store.SetRule(alice, spec with { Allowed = true }, store.Revision));
        var (requestId, _) = store.RecordOrUpdatePendingRequest("alice", "secretary", null, "user-agent", "host", "example.com", 443, "agent", "container");
        Assert.NotNull(requestId);
        Assert.Throws<UnauthorizedAccessException>(() => store.DecideRequest(alice, requestId, true, "agent", store.Revision));
        Assert.Contains(store.ListRules(admin).Rules, r => r.Rule.Id == adminRule.Id && !r.Rule.Allowed && r.Rule.DecidedByAdmin);
    }

    [Fact]
    public void BlockingApprovalKeepsTheAdministratorAllow()
    {
        using var store = new NetworkAccessPolicyStore(Path.Combine(tempDir, "admin-allow-approval.json"));
        var admin = new NetworkAccessActor("admin", true);
        var alice = new NetworkAccessActor("alice", false);
        store.SetUserPrivileges(admin, "alice", canApproveOwn: true);
        var adminRule = store.SetRule(admin, new RuleWriteSpec("agent", "alice", "secretary", null, "example.com", "host", 443, true), store.Revision).Rule.Rule;
        var (requestId, _) = store.RecordOrUpdatePendingRequest("alice", "secretary", null, "user-agent", "host", "example.com", 443, "agent", "container");
        var decision = store.DecideRequest(alice, requestId!, false, "agent", store.Revision);
        Assert.NotNull(decision.Rule);
        Assert.NotEqual(adminRule.Id, decision.Rule.Id);
        Assert.False(decision.Rule.Allowed);
        Assert.Contains(store.ListRules(admin).Rules, r => r.Rule.Id == adminRule.Id && r.Rule.Allowed);
        store.DeleteRule(alice, decision.Rule.Id, store.Revision);
        Assert.Contains(store.ListRules(admin).Rules, r => r.Rule.Id == adminRule.Id && r.Rule.Allowed);
    }

    [Theory]
    [InlineData("alice::web", "web")]
    [InlineData("web", "alice::web")]
    public void ConnectorAliasCannotBypassAdministratorProtection(string adminConnector, string userConnector)
    {
        using var store = new NetworkAccessPolicyStore(Path.Combine(tempDir, "connector-alias.json"));
        var admin = new NetworkAccessActor("admin", true);
        var alice = new NetworkAccessActor("alice", false);
        store.SetUserPrivileges(admin, "alice", canApproveOwn: true);
        var adminRule = store.SetRule(admin, new RuleWriteSpec("user", "alice", null, adminConnector, "example.com", "host", 443, true), store.Revision).Rule.Rule;
        var userSpec = new RuleWriteSpec("user", "alice", null, userConnector, "example.com", "host", 443, false);
        var block = store.SetRule(alice, userSpec, store.Revision).Rule.Rule;
        Assert.Equal("user", block.Source);
        store.DeleteRule(alice, block.Id, store.Revision);
        Assert.Contains(store.ListRules(admin).Rules, r => r.Rule.Id == adminRule.Id && r.Rule.Allowed);
    }
    [Fact]
    public void ConfiguredPerUserLimitAlsoAppliesToSingleProbe()
    {
        using var store = new NetworkAccessPolicyStore(Path.Combine(tempDir, "probe-configured-limit.json"));
        store.SetApprovalLimits(new NetworkAccessActor("admin", true), global: 100, perUser: 70);
        for (int index = 0; index < 70; index++)
        {
            Assert.NotNull(store.RecordOrUpdatePendingProbeRequest("probe-a", "alice", null, "web", "user", "host", $"probe-{index}.example", 443, "connector", "container"));
        }
        Assert.Null(store.RecordOrUpdatePendingProbeRequest("probe-a", "alice", null, "web", "user", "host", "overflow.example", 443, "connector", "container"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"schemaVersion\":2}")]
    [InlineData("{\"schemaVersion\":2,\"rules\":[],\"requests\":[],\"userPrivileges\":[],\"brake\":{\"system\":true}}")]
    [InlineData("{\"schemaVersion\":2,\"rules\":[],\"requests\":[],\"userPrivileges\":[],\"brake\":{\"users\":[]}}")]
    [InlineData("{\"schemaVersion\":1,\"rules\":[],\"requests\":[],\"userPrivileges\":[]}")]
    public void CorruptOrIncompletePolicyNeverResetsBrakes(string content)
    {
        string path = Path.Combine(tempDir, "invalid", "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        using var store = new NetworkAccessPolicyStore(path);
        Assert.Throws<InvalidDataException>(() => store.EnsureLoaded());
    }
    [Fact]
    public void ConfiguredPendingLimitsProtectOtherUsersAndSurviveRestart()
    {
        string path = Path.Combine(tempDir, "limits.json");
        File.WriteAllText(path, """
            {"schemaVersion":2,"revision":0,"rules":[],"requests":[],"userPrivileges":[],"brake":{"system":false,"users":[]},"approvalLimits":{"global":3,"perUser":2}}
            """);
        using (var store = new NetworkAccessPolicyStore(path))
        {
            (string? Id, bool Overflow) Add(string user, string destination) => store.RecordOrUpdatePendingRequest(user, "agent", null, "user-agent", "host", destination, 443, "agent", "container");
            Assert.False(Add("alice", "one.example").Overflow);
            Assert.False(Add("alice", "two.example").Overflow);
            Assert.True(Add("alice", "three.example").Overflow);
            Assert.False(Add("bob", "three.example").Overflow);
            Assert.True(Add("carol", "four.example").Overflow);
        }
        using var reopened = new NetworkAccessPolicyStore(path);
        Assert.True(reopened.RecordOrUpdatePendingRequest("alice", "agent", null, "user-agent", "host", "five.example", 443, "agent", "container").Overflow);
    }

    [Fact]
    public void ProbeAndOrdinaryRequestsShareConfiguredPendingLimits()
    {
        string path = Path.Combine(tempDir, "mixed-limits.json");
        File.WriteAllText(path, """
            {"schemaVersion":2,"revision":0,"rules":[],"requests":[],"userPrivileges":[],"brake":{"system":false,"users":[]},"approvalLimits":{"global":3,"perUser":2}}
            """);
        using var store = new NetworkAccessPolicyStore(path);
        Assert.False(store.RecordOrUpdatePendingRequest("alice", "agent", null, "user-agent", "host", "one.example", 443, "agent", "container").Overflow);
        Assert.NotNull(store.RecordOrUpdatePendingProbeRequest("probe-a", "alice", null, "web", "user", "host", "two.example", 443, "connector", "container"));
        Assert.Null(store.RecordOrUpdatePendingProbeRequest("probe-b", "alice", null, "web", "user", "host", "three.example", 443, "connector", "container"));
        Assert.NotNull(store.RecordOrUpdatePendingProbeRequest("probe-c", "bob", null, "web", "user", "host", "four.example", 443, "connector", "container"));
        Assert.True(store.RecordOrUpdatePendingRequest("carol", "agent", null, "user-agent", "host", "five.example", 443, "agent", "container").Overflow);
    }

    [Fact]
    public void PendingLimitsCanOnlyBeChangedByAnAdministrator()
    {
        string path = Path.Combine(tempDir, "settings", "state.json");
        var admin = new NetworkAccessActor("admin", true);
        var member = new NetworkAccessActor("alice", false);
        using (var store = new NetworkAccessPolicyStore(path))
        {
            Assert.Equal(new NetworkAccessApprovalLimits(500, 64), store.GetApprovalLimits(admin));
            Assert.Throws<UnauthorizedAccessException>(() => store.SetApprovalLimits(member, 3, 2));
            Assert.Throws<ArgumentException>(() => store.SetApprovalLimits(admin, 2, 3));
            Assert.Equal(new NetworkAccessApprovalLimits(3, 2), store.SetApprovalLimits(admin, 3, 2));
        }
        using var reopened = new NetworkAccessPolicyStore(path);
        Assert.Equal(new NetworkAccessApprovalLimits(3, 2), reopened.GetApprovalLimits(admin));
    }

    [Theory]
    [InlineData("ip", "100.64.0.1")]
    [InlineData("ip", "192.168.1.5")]
    [InlineData("host", "x.localhost")]
    [InlineData("host", "metadata.google.internal")]
    public void ProtectedPendingDestinationsCanBeBlockedButNotAllowed(string kind, string destination)
    {
        using var store = new NetworkAccessPolicyStore(Path.Combine(tempDir, Guid.NewGuid().ToString("N"), "state.json"));
        var (id, overflow) = store.RecordOrUpdatePendingRequest("user-a", "agent-a", null, "user-agent", kind, destination, 443, "agent", "container");
        Assert.False(overflow);
        var admin = new NetworkAccessActor("admin", true);
        Assert.Throws<ArgumentException>(() => store.DecideRequest(admin, id!, true, "agent", store.Revision));
        var decision = store.DecideRequest(admin, id!, false, "agent", store.Revision);
        Assert.NotNull(decision.Rule);
        Assert.False(decision.Rule.Allowed);
    }

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

        using var initialStore = new NetworkAccessPolicyStore(statePath);
        initialStore.SetRule(
            admin,
            new RuleWriteSpec("agent", "user-1", "agent-1", null, "first.example.com", "host", 443, Allowed: true),
            expectedRevision: 0);
        Assert.Equal(1, initialStore.Revision);

        // Simulate a kill/crash after the temp file is flushed but BEFORE File.Move renames it over state.json
        using var crashingStore = new NetworkAccessPolicyStore(
            statePath,
            NetworkAccessPolicyStore.FlushDirectory,
            beforeRenameHook: _ => throw new IOException("Simulated crash before rename"));

        Assert.Throws<IOException>(() =>
            crashingStore.SetRule(
                admin,
                new RuleWriteSpec("agent", "user-1", "agent-1", null, "second.example.com", "host", 443, Allowed: true),
                expectedRevision: 1));

        // Reload from disk: must have the intact old state (revision 1, only first.example.com)
        using var reloadedStore = new NetworkAccessPolicyStore(statePath);
        (long rev, IReadOnlyList<NetworkAccessRuleView> userRules) = reloadedStore.ListRules(new NetworkAccessActor("user-1", IsAdmin: false));
        Assert.Equal(1, rev);
        Assert.Single(userRules);
        Assert.Equal("first.example.com", userRules[0].Rule.Destination);

        // Negative control (.NC): without the crash hook, the second write succeeds and persists revision 2
        reloadedStore.SetRule(
            admin,
            new RuleWriteSpec("agent", "user-1", "agent-1", null, "second.example.com", "host", 443, Allowed: true),
            expectedRevision: 1);
        using var verifyStore = new NetworkAccessPolicyStore(statePath);
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

        using var corruptStore = new NetworkAccessPolicyStore(statePath);
        Assert.Throws<InvalidDataException>(() => corruptStore.EnsureLoaded());

        // Negative control (.NC): replace with valid JSON -> loads cleanly
        File.Delete(statePath);
        using var cleanStore = new NetworkAccessPolicyStore(statePath);
        cleanStore.EnsureLoaded();
        Assert.Equal(0, cleanStore.Revision);
    }

    [Fact]
    public async Task U2_RequestDedupe_100AttemptsProduceOneRequestAndOneDiskWrite_AndCapBehaviour_WithNegativeControl()
    {
        string statePath = Path.Combine(tempDir, "dedupe", "state.json");
        using var releaseWriter = new ManualResetEventSlim(false);
        using var store = new NetworkAccessPolicyStore(
            statePath,
            _ => { },
            beforeBackgroundDrainHook: () => Assert.True(releaseWriter.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)));
        var user = new NetworkAccessActor("user-1", IsAdmin: false);

        for (int i = 0; i < 100; i++)
        {
            (string? reqId, bool overflow) = store.RecordOrUpdatePendingRequest(
                "user-1",
                "agent-1",
                connectorId: null,
                attribution: "user-agent",
                destKind: "host",
                destination: "dedupe.example.com",
                port: 443,
                actorKind: "agent",
                origin: "container");
            Assert.False(overflow);
            Assert.NotNull(reqId);
        }

        // Hot-path pending request writes coalesce asynchronously; while background writer hasn't flushed, zero disk writes occurred.
        Assert.Equal(0, store.DiskWriteCount);
        releaseWriter.Set();
        await store.FlushPendingWritesAsync();
        Assert.Equal(1, store.DiskWriteCount);
        IReadOnlyList<NetworkAccessRequestView> requests = store.ListRequests(user).Requests;
        Assert.Single(requests);
        Assert.Equal(100, requests[0].Request.Attempts);

        // Negative control (.NC): a different destination triggers a 2nd coalesced write and a 2nd request;
        // both the new request and 100 attempts survive reopening from disk.
        store.RecordOrUpdatePendingRequest(
            "user-1",
            "agent-1",
            connectorId: null,
            attribution: "user-agent",
            destKind: "host",
            destination: "second.example.com",
            port: 443,
            actorKind: "agent",
            origin: "container");
        await store.FlushPendingWritesAsync();
        Assert.Equal(2, store.DiskWriteCount);
        Assert.Equal(2, store.ListRequests(user).Requests.Count);
        using (var reopened = new NetworkAccessPolicyStore(statePath))
        {
            Assert.Equal(100, reopened.ListRequests(user).Requests.Single(r => r.Request.Destination == "dedupe.example.com").Request.Attempts);
        }

        // Fill remaining 498 slots to reach the 500 pending request cap
        for (int i = 2; i < NetworkAccessPolicyStore.MaxPendingRequests; i++)
        {
            (_, bool overflow) = store.RecordOrUpdatePendingRequest(
                $"fill-user-{i / 64}",
                "agent-1",
                connectorId: null,
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
        using var store = new NetworkAccessPolicyStore(statePath);
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
        var loggedErrors = new ConcurrentQueue<string>();

        using var store = new NetworkAccessPolicyStore(
            statePath,
            _ => { },
            beforeRenameHook: _ =>
            {
                if (Interlocked.Decrement(ref failRemaining) >= 0)
                {
                    throw new IOException("Simulated transient disk failure");
                }
            },
            logger: new CapturingLogger<NetworkAccessPolicyStore>(loggedErrors));

        // 1. First background write hits a transient error, logs it, retries after backoff, and cleans .tmp files.
        store.RecordOrUpdatePendingRequest(
            "user-1",
            "agent-1",
            connectorId: null,
            attribution: "user-agent",
            destKind: "host",
            destination: "err1.example.com",
            port: 443,
            actorKind: "agent",
            origin: "container");
        await store.FlushPendingWritesAsync();
        Assert.Equal(1, store.BackgroundWriteErrorCount);
        Assert.Equal(1, store.DiskWriteCount);
        Assert.Contains(loggedErrors, message => message.Contains("retrying in", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp"));

        // 2. A later write continues normally after the retry and is also persisted.
        store.RecordOrUpdatePendingRequest(
            "user-1",
            "agent-1",
            connectorId: null,
            attribution: "user-agent",
            destKind: "host",
            destination: "err2.example.com",
            port: 443,
            actorKind: "agent",
            origin: "container");
        await store.FlushPendingWritesAsync();
        Assert.Equal(2, store.DiskWriteCount);
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

    [Fact]
    public void V7_27_SyncSaveFailureDoesNotPublishUncommittedState_WithNegativeControl()
    {
        string statePath = Path.Combine(tempDir, "v7-27-sync-failure", "state.json");
        var admin = new NetworkAccessActor("admin-1", IsAdmin: true);
        int failNextRename = 1;
        using var store = new NetworkAccessPolicyStore(
            statePath,
            _ => { },
            _ =>
            {
                if (Interlocked.Exchange(ref failNextRename, 0) == 1)
                {
                    throw new IOException("Simulated synchronous persistence failure");
                }
            });

        Assert.Throws<IOException>(() => store.SetBrake(admin, scope: "system", targetUserId: null, on: true));

        Assert.False(store.GetBrakeSnapshot().System, "A failed durable write must not publish the brake in memory.");
        using (var reopened = new NetworkAccessPolicyStore(statePath))
        {
            Assert.False(reopened.GetBrakeSnapshot().System, "A failed durable write must not appear after reopening the store.");
        }

        // Negative control (.NC): after the transient failure, a successful sync write publishes and persists.
        store.SetBrake(admin, scope: "system", targetUserId: null, on: true);
        Assert.True(store.GetBrakeSnapshot().System);
        using var reopenedAfterSuccess = new NetworkAccessPolicyStore(statePath);
        Assert.True(reopenedAfterSuccess.GetBrakeSnapshot().System);
    }

    [Fact]
    public async Task V7_27_BackgroundRetryStopsPromptlyOnDispose_WithNegativeControl()
    {
        string statePath = Path.Combine(tempDir, "v7-27-dispose-retry", "state.json");
        var store = new NetworkAccessPolicyStore(
            statePath,
            _ => { },
            beforeRenameHook: _ => throw new IOException("Simulated persistent disk failure"));

        store.RecordOrUpdatePendingRequest(
            "user-1",
            "agent-1",
            connectorId: null,
            attribution: "user-agent",
            destKind: "host",
            destination: "dispose-retry.example.com",
            port: 443,
            actorKind: "agent",
            origin: "container");
        for (int attempt = 0; attempt < 100 && store.BackgroundWriteErrorCount == 0; attempt++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(store.BackgroundWriteErrorCount > 0);
        try
        {
            await store.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            await Task.Run(store.Dispose, TestContext.Current.CancellationToken);
            throw;
        }
    }

    private sealed class CapturingLogger<T>(ConcurrentQueue<string> messages) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
            {
                messages.Enqueue(formatter(state, exception));
            }
        }
    }
}
