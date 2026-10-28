using System.Diagnostics.CodeAnalysis;

using Kautuka.Gateway.Domain.Autopilot;
using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Stores;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

using static Kautuka.IntegrationTests.ThreadCommandTestSupport;

namespace Kautuka.IntegrationTests;

public sealed class GatewayApprovalStoreTests
{
    [Theory]
    [InlineData("stale", true)]
    [InlineData("stale", false)]
    [InlineData("awaiting_pre_run_approval", true)]
    [InlineData("awaiting_pre_run_approval", false)]
    [InlineData("scheduled", true)]
    [InlineData("missing", false)]
    public void ObsoleteAutopilotApprovalExpiresWithoutWaitingForItsDeadline(string status, bool hasDeadline)
    {
        using var temp = new TemporaryGatewayStateDirectory();
        KautukaDatabase database = CreateDatabase(temp.SqlitePath);
        var store = CreateApprovalStore(database);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (status != "missing")
        {
            new GatewayAutopilotStore(database).InsertRun(new GatewayScheduledRun(
                Id: "srun-1", ScheduleId: "schedule-1", OwnerUserId: "user-1", TargetType: "agent", TargetId: "agent-1",
                Status: status, TriggerType: "cron", ScheduledFor: now.ToString("O"), SessionId: null, RuntimeRunId: null,
                PreRunApprovalRequestId: "apr-1", InputJson: "{}", OutputJson: null, Error: null,
                StateCategory: AutopilotRunStatuses.GetStateCategory(status), SkipReason: null, BlockingRunId: null,
                BlockingRunStatusSnapshot: null, BlockingRunStateCategory: null, BlockedAt: null,
                CreatedAt: now.ToString("O"), UpdatedAt: now.ToString("O"), StartedAt: null,
                EndedAt: AutopilotRunStatuses.IsTerminal(status) ? now.ToString("O") : null));
        }
        store.CreateRequest(MakeRequest("apr-1", "session-1", "srun-1", "user-1") with
        {
            SessionId = null,
            ToolType = "autopilot_prerun",
            ToolName = "autopilot_pre_run",
            ExpiresAt = hasDeadline ? now.AddHours(1).ToString("O") : null,
        });
        bool obsolete = status is "stale" or "missing";
        Assert.Equal(obsolete, store.ListDueForExpiry(now).Any(item => item.Id == "apr-1"));
        Assert.Equal(obsolete, store.Expire("apr-1", now, "Scheduled run is no longer available."));
        Assert.Equal(obsolete ? "expired" : null, store.GetById("apr-1")!.Decision);
        Assert.Equal(obsolete, store.ListPending("user-1", false).Count == 0);
        if (obsolete)
        {
            Assert.False(store.Resolve("apr-1", true, "late", "user-1", "approvals"));
            Assert.False(store.Expire("apr-1", now, "duplicate"));
        }
        // The same future deadline on an ordinary tool request must still be respected.
        store.CreateRequest(MakeRequest("ordinary", "session-1", "ordinary-run", "user-1") with
        { ExpiresAt = hasDeadline ? now.AddHours(1).ToString("O") : null });
        Assert.False(store.Expire("ordinary", now, "not due"));
        Assert.DoesNotContain(store.ListDueForExpiry(now), item => item.Id == "ordinary");
    }

    [Fact]
    public void ApprovalStoreCreateListResolveWorksForSession()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        KautukaDatabase database = CreateDatabase(temp.SqlitePath);
        var store = CreateApprovalStore(database);

        string requestedAt = DateTimeOffset.UtcNow.ToString("O");
        store.CreateRequest(new GatewayToolApprovalRequest(
            Id: "apr-1",
            SessionId: "sess-1",
            RunId: "run-1",
            CheckpointId: null,
            AgentId: "agent-1",
            UserId: "user-1",
            ToolCallId: "call-1",
            ToolName: "tool-a",
            ToolType: "mcp",
            McpServerId: "mcp-1",
            ArgumentsJson: "{}",
            RequestedAt: requestedAt,
            Decision: null,
            DecidedBy: null,
            DecidedAt: null,
            Reason: null,
            ExpiresAt: null));

        IReadOnlyList<GatewayToolApprovalRequest> pendingForUser = store.ListPending("user-1", false);
        Assert.Single(pendingForUser);
        Assert.Null(pendingForUser[0].Decision);

        bool resolved = store.Resolve("apr-1", approved: true, reason: "ok", decidedBy: "user-1", resolutionSource: "approvals");
        Assert.True(resolved);

        GatewayToolApprovalRequest? request = store.GetById("apr-1");
        Assert.NotNull(request);
        Assert.Equal("approved", request!.Decision);
        Assert.Equal("ok", request.Reason);
        Assert.Equal("user-1", request.DecidedBy);

        IReadOnlyList<GatewayToolApprovalRequest> bySession = store.ListBySession("sess-1");
        Assert.Single(bySession);
        Assert.Equal("apr-1", bySession[0].Id);

        IReadOnlyList<GatewayToolApprovalRequest> nonePending = store.ListPending("user-1", false);
        Assert.Empty(nonePending);
    }

    [Fact]
    public void ListHitlStatsCountsHumanDecisionsAndGroupsSkillsBySkillId()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        KautukaDatabase database = CreateDatabase(temp.SqlitePath);
        var store = CreateApprovalStore(database);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        CreateDecided(database, store, "apr-ok-1", "fetch", "connector", "github", "{}", "approved", now, "agent-1");
        CreateDecided(database, store, "apr-ok-2", "fetch", "connector", "github", "{}", "approved", now, "agent-1");
        CreateDecided(database, store, "apr-no-1", "fetch", "connector", "github", """{"url":"https://evil.example"}""", "denied", now, "agent-1");
        CreateDecided(database, store, "apr-skill-1", "run_skill_script", "skill", null, """{"skillId":"repo-inspector"}""", "approved", now, "agent-1");
        CreateDecided(database, store, "apr-skill-2", "read_skill_resource", "skill", null, """{"skillId":"repo-inspector"}""", "denied", now, "agent-1");
        CreateDecided(database, store, "apr-old", "fetch", "connector", "github", "{}", "approved", now.AddDays(-40), "agent-1");
        CreateDecided(database, store, "apr-stop", "fetch", "connector", "github", "{}", "stopped", now, "agent-1");
        CreateDecided(database, store, "apr-exp", "fetch", "connector", "github", "{}", "expired", now, "agent-1");
        InsertAutoApproved(database, "apr-auto", now);

        IReadOnlyList<HitlApprovalStatRow> agentStats = store.ListHitlStats(new HitlApprovalStatsQuery(
            UserId: "user-1",
            AdminAll: false,
            Since: now.AddDays(-30).ToString("O"),
            AgentId: "agent-1",
            McpServerId: null,
            SkillId: null));

        HitlApprovalStatRow fetch = Assert.Single(agentStats, row => row.ToolName == "fetch");
        Assert.Equal(2, fetch.Approved);
        Assert.Equal(1, fetch.Denied);
        Assert.Equal("github", fetch.McpServerId);
        Assert.NotNull(fetch.LastDeniedAt);

        HitlApprovalStatRow skill = Assert.Single(agentStats, row => row.ToolName == "repo-inspector");
        Assert.Equal("skill", skill.ToolType);
        Assert.Equal("repo-inspector", skill.SkillId);
        Assert.Equal(1, skill.Approved);
        Assert.Equal(1, skill.Denied);

        IReadOnlyList<GatewayToolApprovalRequest> denied = store.ListDeniedHistory(
            new HitlApprovalStatsQuery(
                UserId: "user-1",
                AdminAll: false,
                Since: now.AddDays(-30).ToString("O"),
                AgentId: "agent-1",
                McpServerId: "github",
                SkillId: null,
                ToolName: "fetch"),
            limit: 10);
        Assert.Single(denied);
        Assert.Contains("evil.example", denied[0].ArgumentsJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ListHitlStatsCatalogConnectorIsAcrossAgents()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        KautukaDatabase database = CreateDatabase(temp.SqlitePath);
        var store = CreateApprovalStore(database);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CreateDecided(database, store, "apr-a", "fetch", "connector", "github", "{}", "approved", now, "agent-1");
        CreateDecided(database, store, "apr-b", "fetch", "connector", "github", "{}", "denied", now, "agent-2");

        HitlApprovalStatRow row = Assert.Single(store.ListHitlStats(new HitlApprovalStatsQuery(
            UserId: "user-1",
            AdminAll: true,
            Since: now.AddDays(-30).ToString("O"),
            AgentId: null,
            McpServerId: "github",
            SkillId: null)));
        Assert.Equal("fetch", row.ToolName);
        Assert.Equal(1, row.Approved);
        Assert.Equal(1, row.Denied);
        Assert.Equal(2, row.AgentCount);
    }

    [Fact]
    public void ListHitlStatsConnectorMatchesLegacyNullMcpServerIdByToolName()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        KautukaDatabase database = CreateDatabase(temp.SqlitePath);
        var store = CreateApprovalStore(database);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CreateDecided(database, store, "apr-fetch", "kautuka_web_fetch", "connector", null, "{}", "approved", now, "agent-1");
        CreateDecided(database, store, "apr-fetch-d", "kautuka_web_fetch", "connector", null, "{}", "denied", now, "agent-1");

        HitlApprovalStatRow row = Assert.Single(store.ListHitlStats(new HitlApprovalStatsQuery(
            UserId: "user-1",
            AdminAll: true,
            Since: now.AddDays(-30).ToString("O"),
            AgentId: null,
            McpServerId: "kautuka-web-tools",
            SkillId: null,
            ConnectorToolNames: ["kautuka_web_fetch"])));
        Assert.Equal("kautuka_web_fetch", row.ToolName);
        Assert.Equal(1, row.Approved);
        Assert.Equal(1, row.Denied);
    }

    [Fact]
    public void ListHitlStatsForAgentIncludesNestedChildApprovals()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        KautukaDatabase database = CreateDatabase(temp.SqlitePath);
        var store = CreateApprovalStore(database);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CreateDecided(
            database,
            store,
            "apr-nested-skill",
            "run_skill_script",
            "skill",
            null,
            """{"skillId":"roller"}""",
            "approved",
            now,
            "agent-child",
            outerAgentId: "agent-1");

        HitlApprovalStatRow row = Assert.Single(store.ListHitlStats(new HitlApprovalStatsQuery(
            UserId: "user-1",
            AdminAll: false,
            Since: now.AddDays(-30).ToString("O"),
            AgentId: "agent-1",
            McpServerId: null,
            SkillId: null)));
        Assert.Equal("roller", row.ToolName);
        Assert.Equal(1, row.Approved);
    }

    [Fact]
    public void ListHitlStatsGroupsByAgentDefinitionRevisionAndSupportsAllTime()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        KautukaDatabase database = CreateDatabase(temp.SqlitePath);
        var store = CreateApprovalStore(database);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        CreateDecided(database, store, "apr-v3-a", "fetch", "connector", "github", "{}", "approved", now, "agent-1", agentDefinitionRevision: 3);
        CreateDecided(database, store, "apr-v3-d", "fetch", "connector", "github", "{}", "denied", now, "agent-1", agentDefinitionRevision: 3);
        CreateDecided(database, store, "apr-v2", "fetch", "connector", "github", "{}", "approved", now, "agent-1", agentDefinitionRevision: 2);
        CreateDecided(database, store, "apr-old-rev", "fetch", "connector", "github", "{}", "approved", now.AddDays(-40), "agent-1", agentDefinitionRevision: 1);
        CreateDecided(database, store, "apr-legacy", "shell", "builtin", null, "{}", "denied", now, "agent-1");

        IReadOnlyList<HitlApprovalStatRow> windowed = store.ListHitlStats(new HitlApprovalStatsQuery(
            UserId: "user-1",
            AdminAll: false,
            Since: now.AddDays(-30).ToString("O"),
            AgentId: "agent-1",
            McpServerId: null,
            SkillId: null));
        Assert.DoesNotContain(windowed, row => row.AgentDefinitionRevision == 1);
        HitlApprovalStatRow v3 = Assert.Single(windowed, row => row.ToolName == "fetch" && row.AgentDefinitionRevision == 3);
        Assert.Equal(1, v3.Approved);
        Assert.Equal(1, v3.Denied);
        Assert.Equal(1, Assert.Single(windowed, row => row.ToolName == "fetch" && row.AgentDefinitionRevision == 2).Approved);
        Assert.Null(Assert.Single(windowed, row => row.ToolName == "shell").AgentDefinitionRevision);

        IReadOnlyList<HitlApprovalStatRow> allTime = store.ListHitlStats(new HitlApprovalStatsQuery(
            UserId: "user-1",
            AdminAll: false,
            Since: null,
            AgentId: "agent-1",
            McpServerId: null,
            SkillId: null));
        Assert.Equal(1, Assert.Single(allTime, row => row.AgentDefinitionRevision == 1).Approved);

        IReadOnlyList<GatewayToolApprovalRequest> deniedV3 = store.ListDeniedHistory(
            new HitlApprovalStatsQuery(
                UserId: "user-1",
                AdminAll: false,
                Since: null,
                AgentId: "agent-1",
                McpServerId: null,
                SkillId: null,
                ToolName: "fetch",
                AgentDefinitionRevision: 3),
            limit: 10);
        Assert.Single(deniedV3);
        Assert.Equal(3, deniedV3[0].AgentDefinitionRevision);

        IReadOnlyList<GatewayToolApprovalRequest> deniedLegacy = store.ListDeniedHistory(
            new HitlApprovalStatsQuery(
                UserId: "user-1",
                AdminAll: false,
                Since: null,
                AgentId: "agent-1",
                McpServerId: null,
                SkillId: null,
                ToolName: "shell",
                UngroupedRevisionOnly: true),
            limit: 10);
        Assert.Single(deniedLegacy);
        Assert.Null(deniedLegacy[0].AgentDefinitionRevision);
    }

    [Fact]
    public void ResolveReturnsFalseWhenAlreadyResolved()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        KautukaDatabase database = CreateDatabase(temp.SqlitePath);
        var store = CreateApprovalStore(database);

        store.CreateRequest(MakeRequest("apr-2", "sess-2", "run-2", "user-1"));

        // First resolve succeeds.
        bool first = store.Resolve("apr-2", approved: true, reason: "ok", decidedBy: "user-1", resolutionSource: "approvals");
        Assert.True(first);

        // Second resolve on same request must return false (idempotency guard).
        bool second = store.Resolve("apr-2", approved: false, reason: "changed mind", decidedBy: "user-1", resolutionSource: "approvals");
        Assert.False(second);

        // The decision stored must still be the first one.
        GatewayToolApprovalRequest? stored = store.GetById("apr-2");
        Assert.Equal("approved", stored?.Decision);
    }

    [Fact]
    public async Task ResolveCommitsContinuationJobAtomicallyAndJobSurvivesQueueRecreation()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        KautukaDatabase database = CreateDatabase(temp.SqlitePath);
        var store = CreateApprovalStore(database);
        store.CreateRequest(MakeRequest("apr-durable", "sess-durable", "run-durable", "user-1"));
        var work = new ApprovalContinuationWork(
            "apr-durable",
            "sess-durable",
            "agent",
            "agent-1",
            "user-1",
            Approved: true,
            HitlResolutionSource.ApprovalApi,
            "approved",
            "user-1");

        Assert.True(store.Resolve(
            "apr-durable",
            approved: true,
            reason: "approved",
            decidedBy: "user-1",
            resolutionSource: "approvals",
            continuation: work));

        var restartedQueue = new ApprovalContinuationQueue(database, TimeProvider.System);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using IAsyncEnumerator<ApprovalContinuationLease> leases = restartedQueue
            .LeaseAllAsync("restarted-worker", timeout.Token)
            .GetAsyncEnumerator(timeout.Token);
        Assert.True(await leases.MoveNextAsync().ConfigureAwait(true));
        Assert.Equal("apr-durable", leases.Current.Work.RequestId);
        Assert.Equal("restarted-worker", leases.Current.LeaseOwner);
        Assert.Equal(1, leases.Current.AttemptCount);
        await restartedQueue.CompleteAsync(leases.Current, CancellationToken.None).ConfigureAwait(true);
    }

    [Fact]
    public async Task ContinuationQueueWakesOnEnqueueWithoutIdlePoll()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        KautukaDatabase database = CreateDatabase(temp.SqlitePath);
        var store = CreateApprovalStore(database);
        store.CreateRequest(MakeRequest("apr-wake", "sess-wake", "run-wake", "user-1"));
        var queue = new ApprovalContinuationQueue(database, TimeProvider.System);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        ValueTask<bool> waitForLease = LeaseFirstAsync(queue, "wake-worker", timeout.Token);

        await Task.Delay(50, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.False(waitForLease.IsCompleted);

        await queue.EnqueueAsync(new ApprovalContinuationWork(
            "apr-wake",
            "sess-wake",
            "agent",
            "agent-1",
            "user-1",
            Approved: true,
            HitlResolutionSource.ApprovalApi,
            "approved",
            "user-1"), CancellationToken.None).ConfigureAwait(true);

        Assert.True(await waitForLease.ConfigureAwait(true));
    }

    private static async ValueTask<bool> LeaseFirstAsync(
        ApprovalContinuationQueue queue,
        string owner,
        CancellationToken cancellationToken)
    {
        await foreach (ApprovalContinuationLease lease in queue.LeaseAllAsync(owner, cancellationToken).ConfigureAwait(false))
        {
            Assert.Equal("apr-wake", lease.Work.RequestId);
            await queue.CompleteAsync(lease, CancellationToken.None).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    [Fact]
    public void ResolutionEventStagingFailureRollsBackDecisionAndContinuation()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        KautukaDatabase database = CreateDatabase(temp.SqlitePath);
        var store = CreateApprovalStore(database);
        GatewayToolApprovalRequest request = MakeRequest("apr-atomic", "sess-atomic", "run-atomic", "user-1");
        store.CreateRequest(request);
        using (var connection = new SqliteConnection(database.ConnectionString))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_resolution_event BEFORE INSERT ON approval_resolution_events BEGIN SELECT RAISE(ABORT, 'injected pre-commit failure'); END;";
            command.ExecuteNonQuery();
        }

        ApprovalContinuationWork continuation = MakeContinuation(request, HitlResolutionSource.ApprovalApi, approved: true);
        ApprovalResolutionEventWork resolutionEvent = MakeResolutionEvent(request, approved: true);

        Assert.Throws<SqliteException>(() => store.Resolve(
            request.Id,
            approved: true,
            reason: "approved",
            decidedBy: "user-1",
            resolutionSource: "approvals",
            continuation,
            resolutionEvent));

        Assert.Null(store.GetById(request.Id)?.Decision);
        using var verification = new SqliteConnection(database.ConnectionString);
        verification.Open();
        Assert.Equal(0L, CountByRequestId(verification, resolutionEvents: false, request.Id));
        Assert.Equal(0L, CountByRequestId(verification, resolutionEvents: true, request.Id));
    }

    [Fact]
    public void ExpiryCommitsDecisionContinuationAndResolutionEventTogether()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        KautukaDatabase database = CreateDatabase(temp.SqlitePath);
        var store = CreateApprovalStore(database);
        GatewayToolApprovalRequest request = MakeRequest("apr-expiry-atomic", "sess-expiry", "run-expiry", "user-1") with
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"),
        };
        store.CreateRequest(request);

        Assert.True(store.Expire(
            request.Id,
            DateTimeOffset.UtcNow,
            "Approval expired.",
            MakeContinuation(request, HitlResolutionSource.SystemTimeout, approved: false),
            MakeResolutionEvent(request, approved: false)));

        Assert.Equal("expired", store.GetById(request.Id)?.Decision);
        using var verification = new SqliteConnection(database.ConnectionString);
        verification.Open();
        Assert.Equal(1L, CountByRequestId(verification, resolutionEvents: false, request.Id));
        Assert.Equal(1L, CountByRequestId(verification, resolutionEvents: true, request.Id, pendingOnly: true));
    }

    [Fact]
    public void ResolutionEventFailuresBackOffThenBecomeOperatorVisibleDeadLetters()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        KautukaDatabase database = CreateDatabase(temp.SqlitePath);
        var store = CreateApprovalStore(database);
        GatewayToolApprovalRequest request = MakeRequest("apr-dead-letter", "sess-dead-letter", "run-dead-letter", "user-1");
        store.CreateRequest(request);
        Assert.True(store.Resolve(
            request.Id,
            approved: false,
            reason: "denied",
            decidedBy: "user-1",
            resolutionSource: "approvals",
            continuation: null,
            MakeResolutionEvent(request, approved: false)));

        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (int attempt = 1; attempt <= 8; attempt++)
        {
            ApprovalResolutionEventLease lease = Assert.IsType<ApprovalResolutionEventLease>(
                store.TryLeaseResolutionEvent("worker", now, now.AddMinutes(2), request.Id));
            Assert.Equal(attempt, lease.AttemptCount);
            ApprovalResolutionEventFailureDisposition disposition = store.FailResolutionEvent(lease, "injected", now);
            Assert.Equal(
                attempt == 8 ? ApprovalResolutionEventFailureDisposition.DeadLettered : ApprovalResolutionEventFailureDisposition.RetryScheduled,
                disposition);
            now = now.AddMinutes(2);
        }

        ApprovalResolutionEventDeadLetter deadLetter = Assert.Single(store.ListResolutionEventDeadLetters(10));
        Assert.Equal("approval-resolution:apr-dead-letter", deadLetter.EventId);
        Assert.Equal(8, deadLetter.AttemptCount);
        Assert.Equal("injected", deadLetter.LastError);
        Assert.Null(store.TryLeaseResolutionEvent("worker-2", now, now.AddMinutes(2), request.Id));
    }

    [Fact]
    public void ExpireWinsAtDeadlineAndPreventsLaterResolution()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        KautukaDatabase database = CreateDatabase(temp.SqlitePath);
        var store = CreateApprovalStore(database);
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(-1);
        store.CreateRequest(MakeRequest("apr-expired", "sess-expired", "run-expired", "user-1") with
        {
            ExpiresAt = deadline.ToString("O"),
        });

        Assert.True(store.Expire("apr-expired", DateTimeOffset.UtcNow, "Approval expired."));
        Assert.False(store.Resolve("apr-expired", true, "too late", "user-1", "approvals"));

        GatewayToolApprovalRequest? stored = store.GetById("apr-expired");
        Assert.Equal("expired", stored?.Decision);
        Assert.Equal("system_timeout", stored?.ResolutionSource);
    }

    [Fact]
    public void ListPendingAdminAllReturnsAllUsersRequests()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        KautukaDatabase database = CreateDatabase(temp.SqlitePath);
        var store = CreateApprovalStore(database);

        store.CreateRequest(MakeRequest("apr-a", "sess-a", "run-a", "user-alice"));
        store.CreateRequest(MakeRequest("apr-b", "sess-b", "run-b", "user-bob"));

        // Scoped to alice — should see only her request.
        IReadOnlyList<GatewayToolApprovalRequest> aliceOnly = store.ListPending("user-alice", adminAll: false);
        Assert.Single(aliceOnly);
        Assert.Equal("apr-a", aliceOnly[0].Id);

        // Admin all — should see both.
        IReadOnlyList<GatewayToolApprovalRequest> all = store.ListPending(null, adminAll: true);
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public void ListPendingNullUserIdIsVisibleToAnyUser()
    {
        // A request created with no userId (e.g. via Telegram channel) should be visible
        // to any authenticated user querying their own pending list.
        using var temp = new TemporaryGatewayStateDirectory();
        KautukaDatabase database = CreateDatabase(temp.SqlitePath);
        var store = CreateApprovalStore(database);

        var req = MakeRequest("apr-null-user", "sess-x", "run-x", userId: null);
        store.CreateRequest(req);

        IReadOnlyList<GatewayToolApprovalRequest> result = store.ListPending("any-user", adminAll: false);
        Assert.Single(result);
        Assert.Equal("apr-null-user", result[0].Id);
    }

    [Fact]
    public void ListPendingExcludesClockExpiredRequests()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        KautukaDatabase database = CreateDatabase(temp.SqlitePath);
        var store = CreateApprovalStore(database);

        store.CreateRequest(MakeRequest("apr-live", "sess-live", "run-live", "user-1"));
        store.CreateRequest(MakeRequest("apr-expired", "sess-expired", "run-expired", "user-1") with
        {
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"),
        });

        IReadOnlyList<GatewayToolApprovalRequest> pending = store.ListPending("user-1", adminAll: false);
        Assert.Equal("apr-live", Assert.Single(pending).Id);
        Assert.Equal("apr-live", Assert.Single(store.ListPending(null, adminAll: true)).Id);
    }

    [Fact]
    public void PackedStopRunStampsRemainingMembersAndKeepsResolutionEvent()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        KautukaDatabase database = CreateDatabase(temp.SqlitePath);
        var store = CreateApprovalStore(database);
        GatewayToolApprovalRequest head = MakeRequest("apr-pack-head", "sess-pack", "run-pack", "user-1");
        GatewayToolApprovalRequest leftover = MakeRequest("apr-pack-leftover", "sess-pack", "run-pack", "user-1");
        store.CreateRequest(head);
        store.CreateRequest(leftover);

        ApprovalResolutionEventWork resolutionEvent = MakeResolutionEvent(head, approved: false) with
        {
            PackedRequestIds = [head.Id, leftover.Id],
        };

        Assert.True(store.Resolve(
            head.Id,
            "stopped",
            reason: "stopped",
            decidedBy: "user-1",
            resolutionSource: "chat",
            continuation: null,
            resolutionEvent));

        Assert.Equal("stopped", store.GetById(head.Id)?.Decision);
        Assert.Equal("stopped", store.GetById(leftover.Id)?.Decision);
        using var verification = new SqliteConnection(database.ConnectionString);
        verification.Open();
        Assert.Equal(1L, CountByRequestId(verification, resolutionEvents: true, head.Id, pendingOnly: true));
    }

    [Fact]
    public void ListHistoryReturnsOnlyResolvedRequests()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        KautukaDatabase database = CreateDatabase(temp.SqlitePath);
        var store = CreateApprovalStore(database);

        store.CreateRequest(MakeRequest("apr-pending", "sess-1", "run-1", "user-1"));
        store.CreateRequest(MakeRequest("apr-resolved", "sess-2", "run-2", "user-1"));
        store.Resolve("apr-resolved", approved: true, reason: "ok", decidedBy: "user-1", resolutionSource: "approvals");

        IReadOnlyList<GatewayToolApprovalRequest> history = store.ListHistory("user-1", adminAll: false, limit: 50, offset: 0);
        Assert.Single(history);
        Assert.Equal("apr-resolved", history[0].Id);
        Assert.Equal("approved", history[0].Decision);
    }

    [Fact]
    public void SetTelegramTetherModePersistsOnApprovalRequest()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        KautukaDatabase database = CreateDatabase(temp.SqlitePath);
        var store = CreateApprovalStore(database);

        store.CreateRequest(MakeRequest("apr-tether", "sess-tether", "run-tether", "user-1"));

        store.SetChannelTetherMode("apr-tether", ChannelHitlTetherMode.Mirror);

        GatewayToolApprovalRequest? stored = store.GetById("apr-tether");
        Assert.NotNull(stored);
        Assert.Equal("mirror", stored!.ChannelTetherMode);

        IReadOnlyList<GatewayToolApprovalRequest> bySession = store.ListBySession("sess-tether");
        Assert.Single(bySession);
        Assert.Equal("mirror", bySession[0].ChannelTetherMode);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static GatewayToolApprovalRequest MakeRequest(
        string id, string sessionId, string runId, string? userId) =>
        new(
            Id: id,
            SessionId: sessionId,
            RunId: runId,
            CheckpointId: null,
            AgentId: "agent-1",
            UserId: userId,
            ToolCallId: $"call-{id}",
            ToolName: "tool-a",
            ToolType: "mcp",
            McpServerId: "mcp-1",
            ArgumentsJson: "{}",
            RequestedAt: DateTimeOffset.UtcNow.ToString("O"),
            Decision: null,
            DecidedBy: null,
            DecidedAt: null,
            Reason: null,
            ExpiresAt: null);

    private static void CreateDecided(
        KautukaDatabase database,
        GatewayApprovalStore store,
        string id,
        string toolName,
        string toolType,
        string? mcpServerId,
        string argumentsJson,
        string decision,
        DateTimeOffset decidedAt,
        string agentId,
        string? outerAgentId = null,
        long? agentDefinitionRevision = null)
    {
        store.CreateRequest(new GatewayToolApprovalRequest(
            Id: id,
            SessionId: "sess-stats",
            RunId: "run-stats",
            CheckpointId: null,
            AgentId: agentId,
            UserId: "user-1",
            ToolCallId: $"call-{id}",
            ToolName: toolName,
            ToolType: toolType,
            McpServerId: mcpServerId,
            ArgumentsJson: argumentsJson,
            RequestedAt: decidedAt.ToString("O"),
            Decision: null,
            DecidedBy: null,
            DecidedAt: null,
            Reason: null,
            ExpiresAt: null,
            OuterAgentId: outerAgentId,
            AgentDefinitionRevision: agentDefinitionRevision));
        bool resolved = store.Resolve(
            id,
            decision,
            reason: decision == "denied" ? "blocked" : "ok",
            decidedBy: "user-1",
            resolutionSource: "approvals",
            continuation: null,
            resolutionEvent: null);
        Assert.True(resolved);
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE tool_approval_requests SET decided_at = $decidedAt WHERE id = $id";
        command.Parameters.AddWithValue("$decidedAt", decidedAt.ToString("O"));
        command.Parameters.AddWithValue("$id", id);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static void InsertAutoApproved(KautukaDatabase database, string id, DateTimeOffset decidedAt)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO tool_approval_requests(
                id, session_id, run_id, agent_id, user_id, tool_call_id, tool_name, tool_type,
                mcp_server_id, arguments_json, requested_at, decision, decided_by, decided_at, channel_tether_mode)
            VALUES (
                $id, 'sess-stats', 'run-stats', 'agent-1', 'user-1', $callId, 'fetch', 'connector',
                'github', '{}', $at, 'auto_approved', 'policy:auto', $at, 'single')
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$callId", $"call-{id}");
        command.Parameters.AddWithValue("$at", decidedAt.ToString("O"));
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static ApprovalContinuationWork MakeContinuation(
        GatewayToolApprovalRequest request,
        HitlResolutionSource source,
        bool approved)
        => new(request.Id, request.SessionId!, "agent", request.AgentId!, request.UserId, approved, source, null, request.UserId);

    private static ApprovalResolutionEventWork MakeResolutionEvent(GatewayToolApprovalRequest request, bool approved)
        => new(
            $"approval-resolution:{request.Id}",
            request.Id,
            System.Text.Json.JsonSerializer.SerializeToElement(new { approvalRequestId = request.Id, approved }),
            new NaradEventContext(
                request.AgentId!,
                request.SessionId!,
                request.UserId!,
                request.RunId!,
                request.RunId!),
            request.ToolCallId,
            request.CheckpointId);

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Test-only helper selects one of three closed constant SQL statements; request ID remains parameterized.")]
    private static long CountByRequestId(
        SqliteConnection connection,
        bool resolutionEvents,
        string requestId,
        bool pendingOnly = false)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = (resolutionEvents, pendingOnly) switch
        {
            (false, false) => "SELECT COUNT(*) FROM approval_continuation_jobs WHERE request_id = $id",
            (true, false) => "SELECT COUNT(*) FROM approval_resolution_events WHERE request_id = $id",
            (true, true) => "SELECT COUNT(*) FROM approval_resolution_events WHERE request_id = $id AND status = 'pending'",
            _ => throw new ArgumentOutOfRangeException(nameof(pendingOnly)),
        };
        command.Parameters.AddWithValue("$id", requestId);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task ContinuationQueueReschedulesCompletedJobOnRetryEnqueue()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        KautukaDatabase database = CreateDatabase(temp.SqlitePath);
        var store = CreateApprovalStore(database);
        store.CreateRequest(MakeRequest("apr-retry", "sess-retry", "run-retry", "user-1"));
        var queue = new ApprovalContinuationQueue(database, TimeProvider.System);
        var work = new ApprovalContinuationWork(
            "apr-retry",
            "sess-retry",
            "agent",
            "agent-1",
            "user-1",
            Approved: true,
            HitlResolutionSource.Browser,
            "approved",
            "user-1");

        ApprovalContinuationEnqueueResult first = await queue.EnqueueAsync(work, CancellationToken.None).ConfigureAwait(true);
        Assert.Equal(ApprovalContinuationEnqueueResult.Inserted, first);

        await using IAsyncEnumerator<ApprovalContinuationLease> leases = queue
            .LeaseAllAsync("retry-worker", TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await leases.MoveNextAsync().ConfigureAwait(true));
        await queue.CompleteAsync(leases.Current, CancellationToken.None).ConfigureAwait(true);

        ApprovalContinuationEnqueueResult second = await queue.EnqueueAsync(work, CancellationToken.None).ConfigureAwait(true);
        Assert.Equal(ApprovalContinuationEnqueueResult.Rescheduled, second);

        ApprovalContinuationEnqueueResult third = await queue.EnqueueAsync(work, CancellationToken.None).ConfigureAwait(true);
        Assert.Equal(ApprovalContinuationEnqueueResult.AlreadyPending, third);
    }

    [Fact]
    public async Task ContinuationQueueReschedulesDeadLetterJobOnRetryEnqueue()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        KautukaDatabase database = CreateDatabase(temp.SqlitePath);
        var store = CreateApprovalStore(database);
        store.CreateRequest(MakeRequest("apr-dead", "sess-dead", "run-dead", "user-1"));
        var queue = new ApprovalContinuationQueue(database, TimeProvider.System);
        var work = new ApprovalContinuationWork(
            "apr-dead",
            "sess-dead",
            "agent",
            "agent-1",
            "user-1",
            Approved: true,
            HitlResolutionSource.Browser,
            "approved",
            "user-1");

        await queue.EnqueueAsync(work, CancellationToken.None).ConfigureAwait(true);
        await using IAsyncEnumerator<ApprovalContinuationLease> leases = queue
            .LeaseAllAsync("dead-worker", TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await leases.MoveNextAsync().ConfigureAwait(true));
        ApprovalContinuationLease lease = leases.Current with { AttemptCount = 8 };
        await queue.FailAsync(lease, "terminal failure", CancellationToken.None).ConfigureAwait(true);

        ApprovalContinuationEnqueueResult result = await queue.EnqueueAsync(work, CancellationToken.None).ConfigureAwait(true);
        Assert.Equal(ApprovalContinuationEnqueueResult.Rescheduled, result);
    }

    private static GatewayApprovalStore CreateApprovalStore(KautukaDatabase database)
        => CreateGatewayApprovalStore(database);

    private static KautukaDatabase CreateDatabase(string sqlitePath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kautuka:ContainerStateRoot"] = Path.GetDirectoryName(sqlitePath) ?? throw new InvalidOperationException("Expected sqlite path to include a parent directory."),
            })
            .Build();

        var database = new KautukaDatabase(configuration);
        database.EnsureReady();
        return database;
    }

    private static string QueryString(KautukaDatabase database, string sql)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Test helper receives fixed SQL literals from test methods.
        command.CommandText = sql;
#pragma warning restore CA2100
        return (string?)command.ExecuteScalar()
            ?? throw new InvalidOperationException("Expected query to return a string value.");
    }

    private sealed class TemporaryGatewayStateDirectory : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "kautuka-approval-tests", Guid.NewGuid().ToString("N"));

        public TemporaryGatewayStateDirectory()
        {
            Directory.CreateDirectory(root);
        }

        public string SqlitePath => Path.Combine(root, "gateway.db");

        public void Dispose()
        {
            SqliteTestPoolCleanup.ClearPoolsUnderDirectory(root);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
