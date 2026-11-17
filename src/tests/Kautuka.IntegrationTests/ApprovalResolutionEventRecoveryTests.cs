using System.Text.Json;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Sandbox;
using Kautuka.Gateway.Domain.Skills;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.NetworkAccess.Client;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using static Kautuka.IntegrationTests.ThreadCommandTestSupport;

namespace Kautuka.IntegrationTests;

public sealed class ApprovalResolutionEventRecoveryTests
{
    [Fact]
    public async Task PreflightFailureIsSavedAndSurvivesRestartWithoutClaimingUnknownOutcome()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IGatewayApprovalStore store = CreateGatewayApprovalStore(CreateDatabase(temp.SqlitePath));
        GatewayToolApprovalRequest request = MakeRequest();
        store.CreateRequest(request);
        Assert.True(store.Resolve(request.Id, true, null, "user-1", "chat"));
        var invoker = new PackedApprovedToolInvoker(new EmptyConfigurationProvider(), null!, null!, null!, NullLogger<PackedApprovedToolInvoker>.Instance);
        int calls = 0;
        Task<string> Invoke()
        {
            calls++;
            return invoker.InvokePinnedAsync(request.AgentId!, request.SessionId!, request.UserId, request.ToolName,
                request.ArgumentsJson, null, CancellationToken.None).AsTask();
        }

        string first = await ApprovedToolExecution.RunAsync(store, request.Id, request.AgentId!, request.SessionId!, request.UserId,
            request.ToolName, request.ArgumentsJson, Invoke, CancellationToken.None);
        IGatewayApprovalStore reopened = CreateGatewayApprovalStore(CreateDatabase(temp.SqlitePath));
        string afterRestart = await ApprovedToolExecution.RunAsync(reopened, request.Id, request.AgentId!, request.SessionId!, request.UserId,
            request.ToolName, request.ArgumentsJson, Invoke, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal(first, afterRestart);
        Assert.Contains("was not found", first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outcome is unknown", afterRestart, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class EmptyConfigurationProvider : IPlatformConfigurationProvider
    {
        public PlatformConfiguration GetCurrent() => new();
    }

    [Fact]
    public async Task ChangedPinnedDefinitionsReturnSavedPreflightFailureAfterRestart()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IGatewayApprovalStore store = CreateGatewayApprovalStore(CreateDatabase(temp.SqlitePath));
        GatewayToolApprovalRequest request = MakeRequest();
        store.CreateRequest(request);
        Assert.True(store.Resolve(request.Id, true, null, "user-1", "chat"));
        var configuration = new AgentConfigurationProvider(request.AgentId!);
        var catalog = new AgentToolCatalog([], null!, configuration);
        var invoker = new PackedApprovedToolInvoker(configuration, new PreflightWorkspace(), catalog, new EmptySkillManifest(), NullLogger<PackedApprovedToolInvoker>.Instance);
        var snapshot = new EffectiveToolingSnapshot([request.ToolName], "standard", false, Definitions: new AgentToolSurfaceCatalogue(), DefinitionsHash: "changed");
        int calls = 0;
        Task<string> Invoke()
        {
            calls++;
            return invoker.InvokePinnedAsync(request.AgentId!, request.SessionId!, request.UserId, request.ToolName,
                request.ArgumentsJson, snapshot, CancellationToken.None).AsTask();
        }

        string first = await ApprovedToolExecution.RunAsync(store, request.Id, request.AgentId!, request.SessionId!, request.UserId,
            request.ToolName, request.ArgumentsJson, Invoke, CancellationToken.None);
        IGatewayApprovalStore reopened = CreateGatewayApprovalStore(CreateDatabase(temp.SqlitePath));
        string afterRestart = await ApprovedToolExecution.RunAsync(reopened, request.Id, request.AgentId!, request.SessionId!, request.UserId,
            request.ToolName, request.ArgumentsJson, Invoke, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal(first, afterRestart);
        Assert.Contains("saved tool definitions changed", first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outcome is unknown", afterRestart, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WorkspacePreflightErrorIsSavedWithoutInvokingTheTool()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IGatewayApprovalStore store = CreateGatewayApprovalStore(CreateDatabase(temp.SqlitePath));
        GatewayToolApprovalRequest request = MakeRequest();
        store.CreateRequest(request);
        Assert.True(store.Resolve(request.Id, true, null, "user-1", "chat"));
        var configuration = new AgentConfigurationProvider(request.AgentId!);
        var invoker = new PackedApprovedToolInvoker(configuration, new PreflightWorkspace(new IOException("workspace unavailable")), null!, new EmptySkillManifest(), NullLogger<PackedApprovedToolInvoker>.Instance);
        int calls = 0;
        Task<string> Invoke()
        {
            calls++;
            return invoker.InvokePinnedAsync(request.AgentId!, request.SessionId!, request.UserId, request.ToolName,
                request.ArgumentsJson, null, CancellationToken.None).AsTask();
        }

        string first = await ApprovedToolExecution.RunAsync(store, request.Id, request.AgentId!, request.SessionId!, request.UserId,
            request.ToolName, request.ArgumentsJson, Invoke, CancellationToken.None);
        IGatewayApprovalStore reopened = CreateGatewayApprovalStore(CreateDatabase(temp.SqlitePath));
        string afterRestart = await ApprovedToolExecution.RunAsync(reopened, request.Id, request.AgentId!, request.SessionId!, request.UserId,
            request.ToolName, request.ArgumentsJson, Invoke, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal(first, afterRestart);
        Assert.Contains("workspace unavailable", first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("outcome is unknown", afterRestart, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NetworkDenialDuringPreflightRemainsVisibleToTheCaller()
    {
        var denial = new NetworkAccessDeniedException("approval required");
        var invoker = new PackedApprovedToolInvoker(new AgentConfigurationProvider("agent-1"), new PreflightWorkspace(denial), null!, new EmptySkillManifest(), NullLogger<PackedApprovedToolInvoker>.Instance);
        NetworkAccessDeniedException thrown = await Assert.ThrowsAsync<NetworkAccessDeniedException>(async () =>
            await invoker.InvokePinnedAsync("agent-1", "session-1", "user-1", "tool-1", "{}", null, CancellationToken.None));
        Assert.Same(denial, thrown);
    }

    private sealed class AgentConfigurationProvider(string agentId) : IPlatformConfigurationProvider
    {
        public PlatformConfiguration GetCurrent() => new() { Agents = [new AgentDefinition { Id = agentId }] };
    }

    private sealed class EmptySkillManifest : IEffectiveSkillManifestResolver
    {
        public IReadOnlyList<EffectiveSkillManifestEntry> Resolve(PlatformConfiguration configuration, AgentDefinition agentDefinition, string? actingUserId) => [];
    }

    private sealed class PreflightWorkspace(Exception? failure = null) : IWorkspaceRuntimeTool
    {
        public WorkspaceRuntimeContext DescribeAgentWorkspace(string agentId, string? userId = null) => failure is not null
            ? throw failure
            : WorkspaceRuntimeContext.Unavailable;
        public WorkspaceRuntimeContext RebuildAgentWorkspace(string agentId, string? userId = null) => throw new NotSupportedException();
        public bool DeleteAgentWorkspace(string agentId) => throw new NotSupportedException();
        public Task<WorkspaceSandboxSession> SpawnAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceCommandResult> RunAgentShellAsync(string agentId, string command, string? userId = null, int? timeoutSeconds = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceSandboxSession> InspectAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CloseAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    [Fact]
    public async Task ApprovedCallIsNotRepeatedAfterCrashBeforeResultIsSaved()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IGatewayApprovalStore store = CreateGatewayApprovalStore(CreateDatabase(temp.SqlitePath));
        GatewayToolApprovalRequest request = MakeRequest();
        store.CreateRequest(request);
        Assert.True(store.Resolve(request.Id, true, null, "user-1", "chat"));
        int calls = 0;
        Task<string> Invoke() { calls++; throw new InvalidOperationException("simulated crash"); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApprovedToolExecution.RunAsync(store, request.Id, request.AgentId!, request.SessionId!, request.UserId, request.ToolName, request.ArgumentsJson, Invoke, CancellationToken.None));
        IGatewayApprovalStore reopened = CreateGatewayApprovalStore(CreateDatabase(temp.SqlitePath));
        string result = await ApprovedToolExecution.RunAsync(reopened, request.Id, request.AgentId!, request.SessionId!, request.UserId, request.ToolName, request.ArgumentsJson, Invoke, CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.Contains("outcome is unknown", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentApprovedCallsInvokeTheToolOnce()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IGatewayApprovalStore store = CreateGatewayApprovalStore(CreateDatabase(temp.SqlitePath));
        GatewayToolApprovalRequest request = MakeRequest();
        store.CreateRequest(request);
        Assert.True(store.Resolve(request.Id, true, null, "user-1", "chat"));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        async Task<string> Invoke() { Interlocked.Increment(ref calls); started.SetResult(); await release.Task; return "{\"ok\":true}"; }
        Task<string> first = ApprovedToolExecution.RunAsync(store, request.Id, request.AgentId!, request.SessionId!, request.UserId, request.ToolName, request.ArgumentsJson, Invoke, CancellationToken.None);
        await started.Task;
        Task<string> second = ApprovedToolExecution.RunAsync(store, request.Id, request.AgentId!, request.SessionId!, request.UserId, request.ToolName, request.ArgumentsJson, Invoke, CancellationToken.None);
        release.SetResult();
        Assert.Equal(await first, await second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ApprovedCallRejectsChangedArgumentsBeforeClaimOrInvocation()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IGatewayApprovalStore store = CreateGatewayApprovalStore(CreateDatabase(temp.SqlitePath));
        GatewayToolApprovalRequest request = MakeRequest();
        store.CreateRequest(request);
        Assert.True(store.Resolve(request.Id, true, null, "user-1", "chat"));
        bool invoked = false;
        Task<string> Invoke() { invoked = true; return Task.FromResult("{}"); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApprovedToolExecution.RunAsync(store, request.Id, request.AgentId!, request.SessionId!, request.UserId, request.ToolName, "{\"changed\":true}", Invoke, CancellationToken.None));
        Assert.False(invoked);
        Assert.True(store.TryClaimToolExecution(request.Id));
    }
    [Fact]
    public async Task ApprovedToolClaimIsDurableAndConcurrentClaimsHaveOneWinner()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        KautukaDatabase database = CreateDatabase(temp.SqlitePath);
        IGatewayApprovalStore first = CreateGatewayApprovalStore(database);
        IGatewayApprovalStore second = CreateGatewayApprovalStore(database);
        GatewayToolApprovalRequest request = MakeRequest();
        first.CreateRequest(request);
        Assert.True(first.Resolve(request.Id, true, null, "user-1", "chat"));
        bool[] claims = await Task.WhenAll(
            Task.Run(() => first.TryClaimToolExecution(request.Id)),
            Task.Run(() => second.TryClaimToolExecution(request.Id)));
        Assert.Single(claims, static won => won);
        IGatewayApprovalStore reopened = CreateGatewayApprovalStore(CreateDatabase(temp.SqlitePath));
        Assert.False(reopened.TryClaimToolExecution(request.Id));
        Assert.Null(reopened.GetToolExecutionResult(request.Id));
        Assert.True(first.SaveToolExecutionResult(request.Id, "{\"ok\":true}"));
        Assert.Equal("{\"ok\":true}", reopened.GetToolExecutionResult(request.Id));
        Assert.False(second.SaveToolExecutionResult(request.Id, "different"));
    }
    [Fact]
    public void DuplicateApprovalIdIsIdempotentOnlyForTheSameImmutablePayload()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        var store = CreateGatewayApprovalStore(CreateDatabase(temp.SqlitePath));
        GatewayToolApprovalRequest request = MakeRequest();

        store.CreateRequest(request);
        store.CreateRequest(request);
        Assert.Throws<InvalidOperationException>(() => store.CreateRequest(request with
        {
            ArgumentsJson = "{\"different\":true}",
        }));
        Assert.Equal("{}", store.GetById(request.Id)?.ArgumentsJson);
    }

    [Fact]
    public async Task ConcurrentBrowserAndChannelDecisionsHaveOneFirstValidWinner()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        KautukaDatabase database = CreateDatabase(temp.SqlitePath);
        var browser = CreateGatewayApprovalStore(database);
        var channel = CreateGatewayApprovalStore(database);
        GatewayToolApprovalRequest request = MakeRequest();
        browser.CreateRequest(request);

        Task<bool> browserDecision = Task.Run(() => browser.Resolve(
            request.Id, approved: true, reason: "browser", decidedBy: "admin", resolutionSource: "chat"));
        Task<bool> channelDecision = Task.Run(() => channel.Resolve(
            request.Id, approved: false, reason: "channel", decidedBy: "telegram-user", resolutionSource: "telegram"));

        bool[] winners = await Task.WhenAll(browserDecision, channelDecision);

        Assert.Equal(1, winners.Count(static value => value));
        GatewayToolApprovalRequest committed = Assert.IsType<GatewayToolApprovalRequest>(browser.GetById(request.Id));
        Assert.Equal(winners[0] ? "approved" : "denied", committed.Decision);
        Assert.False(browser.Resolve(request.Id, approved: !winners[0], reason: "late", decidedBy: "late", resolutionSource: "channel"));
        Assert.Equal(winners[0] ? "approved" : "denied", browser.GetById(request.Id)?.Decision);
    }

    [Fact]
    public void LateResolutionForPriorApprovalCannotAffectFreshApprovalCycle()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        var store = CreateGatewayApprovalStore(CreateDatabase(temp.SqlitePath));
        GatewayToolApprovalRequest first = MakeRequest() with { Id = "approval-a" };
        GatewayToolApprovalRequest second = MakeRequest() with { Id = "approval-b" };
        store.CreateRequest(first);
        Assert.True(store.Resolve(first.Id, approved: true, reason: null, decidedBy: "admin", resolutionSource: "chat"));
        store.CreateRequest(second);

        Assert.False(store.Resolve(first.Id, approved: false, reason: "late", decidedBy: "channel-user", resolutionSource: "telegram"));
        Assert.Equal("approved", store.GetById(first.Id)?.Decision);
        Assert.Null(store.GetById(second.Id)?.Decision);
    }

    [Fact]
    public async Task CrashAfterCanonicalAppendRecoversWithoutVisibleDuplicate()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        KautukaDatabase database = CreateDatabase(temp.SqlitePath);
        var durableStore = CreateGatewayApprovalStore(database);
        GatewayToolApprovalRequest request = MakeRequest();
        durableStore.CreateRequest(request);
        ApprovalResolutionEventWork staged = MakeEvent(request);
        Assert.True(durableStore.Resolve(
            request.Id,
            approved: true,
            reason: null,
            decidedBy: "user-1",
            resolutionSource: "approvals",
            continuation: null,
            staged));

        var publisher = new IdempotentPublisher();
        DateTimeOffset crashTime = DateTimeOffset.UtcNow;
        ApprovalResolutionEventLease crashedLease = Assert.IsType<ApprovalResolutionEventLease>(
            durableStore.TryLeaseResolutionEvent("crashed-process", crashTime, crashTime.AddMinutes(2), request.Id));
        await ((IRuntimeEventPublisher)publisher).PublishWithIdAsync(
            crashedLease.Work.EventId,
            NaradEventTypes.InterruptResolved,
            crashedLease.Work.Payload,
            crashedLease.Work.Context,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, publisher.PublishAttempts);
        Assert.Single(publisher.VisibleEventIds);
        Assert.Null(durableStore.TryLeaseResolutionEvent("early-recovery", crashTime.AddMinutes(1), crashTime.AddMinutes(3), request.Id));

        var recoveredTime = new FixedTimeProvider(crashTime.AddMinutes(3));
        var recoveredProcess = new ApprovalResolutionEventDispatcher(
            durableStore,
            publisher,
            new ApprovalResolutionSideEffectApplier(new ThreadCommandTestSupport.NoOpRunStopCommandService()),
            recoveredTime,
            NullLogger<ApprovalResolutionEventDispatcher>.Instance);
        await recoveredProcess.DispatchAsync(request.Id, TestContext.Current.CancellationToken);

        Assert.Equal(2, publisher.PublishAttempts);
        Assert.Single(publisher.VisibleEventIds);
        Assert.Null(durableStore.TryLeaseResolutionEvent("later-process", recoveredTime.GetUtcNow(), recoveredTime.GetUtcNow().AddMinutes(2), request.Id));
        Assert.Equal(staged.EventId, Assert.Single(publisher.VisibleEventIds));
    }

    [Fact]
    public void LeasedStopRunResolutionKeepsStopRunForSideEffects()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        var store = CreateGatewayApprovalStore(CreateDatabase(temp.SqlitePath));
        GatewayToolApprovalRequest request = MakeRequest();
        store.CreateRequest(request);
        ApprovalResolutionEventWork staged = new(
            $"approval-resolution:{request.Id}",
            request.Id,
            NaradEventBodies.InterruptResolved(
                request.Id,
                NaradEventBodies.ApprovalWaitId(request.Id),
                ApprovalActionCatalog.StopRun),
            new NaradEventContext(
                request.AgentId!,
                request.SessionId!,
                request.UserId!,
                request.RunId!,
                request.RunId!,
                ParentSessionId: "session-1"),
            request.ToolCallId,
            request.CheckpointId,
            StopRun: true,
            StopThreadId: "session-1");
        Assert.True(store.Resolve(
            request.Id,
            "stopped",
            "stopped",
            "user-1",
            "chat",
            continuation: null,
            staged));

        DateTimeOffset now = DateTimeOffset.UtcNow;
        ApprovalResolutionEventLease lease = Assert.IsType<ApprovalResolutionEventLease>(
            store.TryLeaseResolutionEvent("lease-owner", now, now.AddMinutes(2), request.Id));
        Assert.True(lease.Work.StopRun);
        Assert.Equal("session-1", lease.Work.StopThreadId);
        Assert.Equal(request.RunId, lease.Work.Context.RootRunId);
    }

    private static KautukaDatabase CreateDatabase(string sqlitePath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kautuka:ContainerStateRoot"] = Path.GetDirectoryName(sqlitePath),
            })
            .Build();
        var database = new KautukaDatabase(configuration);
        database.EnsureReady();
        return database;
    }

    private static GatewayToolApprovalRequest MakeRequest() => new(
        "apr-recovery",
        "session-1",
        "run-1",
        null,
        "agent-1",
        "user-1",
        "tool-call-1",
        "tool-1",
        "mcp",
        null,
        "{}",
        DateTimeOffset.UtcNow.ToString("O"),
        null,
        null,
        null,
        null,
        null);

    private static ApprovalResolutionEventWork MakeEvent(GatewayToolApprovalRequest request) => new(
        $"approval-resolution:{request.Id}",
        request.Id,
        JsonSerializer.SerializeToElement(new { approvalRequestId = request.Id, approved = true }),
        new NaradEventContext(request.AgentId!, request.SessionId!, request.UserId!, request.RunId!, request.RunId!),
        request.ToolCallId,
        request.CheckpointId);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TemporaryGatewayStateDirectory : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "kautuka-approval-event-recovery", Guid.NewGuid().ToString("N"));

        public TemporaryGatewayStateDirectory() => Directory.CreateDirectory(root);

        public string SqlitePath => Path.Combine(root, "kautuka.db");

        public void Dispose()
        {
            SqliteTestPoolCleanup.ClearPoolsUnderDirectory(root);
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class IdempotentPublisher : IRuntimeEventPublisher
    {
        public int PublishAttempts { get; private set; }

        public HashSet<string> VisibleEventIds { get; } = new(StringComparer.Ordinal);

        public ValueTask<NaradPersistedEvent> PublishAsync(
            NaradAuthorityEventDraft draft,
            NaradEventContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PublishAttempts++;
            VisibleEventIds.Add(draft.EventId);
            return ValueTask.FromResult(NaradTestEventFactory.FromDraft(draft, context, PublishAttempts, PublishAttempts));
        }

        public ValueTask<NaradPersistedEvent> PublishWithIdAsync(
            string eventId,
            string eventType,
            JsonElement body,
            NaradEventContext context,
            CancellationToken cancellationToken = default)
            => TestRuntimeEventPublisherSupport.PublishWithIdAsync(PublishAsync, eventId, eventType, body, context, cancellationToken);
    }
}
