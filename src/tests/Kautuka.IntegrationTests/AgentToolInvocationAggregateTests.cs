using Kautuka.Gateway.Domain.Stores;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Kautuka.IntegrationTests;

public sealed class AgentToolInvocationAggregateTests
{
    [Fact]
    public void ExecutionQuarantinePersistsOperatorRetryAndTermination()
    {
        using var database = new TemporaryDatabase();
        var firstProcess = new ExecutionQuarantineStore(database.Database);
        ExecutionQuarantineItem item = firstProcess.Quarantine(
            "aaat_invocation",
            "invocation-missing-lineage",
            "parent_wait_missing",
            "The invocation has no recoverable parent wait.");

        var restarted = new ExecutionQuarantineStore(database.Database);
        Assert.Equal(item.QuarantineId, Assert.Single(restarted.ListActive()).QuarantineId);
        Assert.True(restarted.RequestRetry(item.QuarantineId));
        ExecutionQuarantineItem retry = Assert.IsType<ExecutionQuarantineItem>(restarted.Get(item.QuarantineId));
        Assert.Equal(ExecutionQuarantineStatuses.RetryRequested, retry.Status);
        Assert.Equal(1, retry.RetryCount);
        Assert.True(restarted.Terminate(item.QuarantineId));
        Assert.Empty(restarted.ListActive());
        Assert.Equal(ExecutionQuarantineStatuses.Terminated, restarted.Get(item.QuarantineId)?.Status);
    }

    [Fact]
    public void RecreatedStoresPreserveTwoApprovalCyclesAndCompleteParentExactlyOnce()
    {
        using var database = new TemporaryDatabase();
        var firstProcess = new GatewayAgentToolInvocationStore(database.Database);
        AgentToolInvocation invocation = firstProcess.CreateWaiting(CreateRequest());
        InsertApproval(database.Database, "approval-1", "child-run-1");
        firstProcess.RecordApprovalLink(CreateLink(invocation.InvocationId, "approval-1", "child-run-1", "event-1"));

        var afterFirstRestart = new GatewayAgentToolInvocationStore(database.Database);
        AgentToolInvocation recoveredAfterFirst = Assert.IsType<AgentToolInvocation>(afterFirstRestart.Get(invocation.InvocationId));
        Assert.Equal(AgentToolInvocationStates.WaitingOnChild, recoveredAfterFirst.State);
        InsertApproval(database.Database, "approval-2", "child-run-2");
        afterFirstRestart.RecordApprovalLink(CreateLink(invocation.InvocationId, "approval-2", "child-run-2", "event-2"));

        var afterSecondRestart = new GatewayAgentToolInvocationStore(database.Database);
        AgentToolInvocation recoveredAfterSecond = Assert.IsType<AgentToolInvocation>(afterSecondRestart.Get(invocation.InvocationId));
        Assert.Equal("child-run-2", recoveredAfterSecond.ChildRunId);
        Assert.Equal(AgentToolInvocationStates.WaitingOnChild, recoveredAfterSecond.State);
        afterSecondRestart.RecordChildCompletion(invocation.InvocationId, "{\"result\":\"73\"}", "completed");
        afterSecondRestart.RecordChildCompletion(invocation.InvocationId, "{\"result\":\"73\"}", "completed");
        Assert.Throws<InvalidOperationException>(() =>
            afterSecondRestart.RecordChildCompletion(invocation.InvocationId, "{\"result\":\"74\"}", "completed"));
        var afterChildCompletionRestart = new GatewayAgentToolInvocationStore(database.Database);
        AgentToolInvocation completionPending = Assert.IsType<AgentToolInvocation>(afterChildCompletionRestart.Get(invocation.InvocationId));
        Assert.Equal(AgentToolInvocationStates.ParentCompletionPending, completionPending.State);
        Assert.Equal("{\"result\":\"73\"}", completionPending.ChildResultJson);
        Assert.Equal("completed", completionPending.ChildOutcome);
        Assert.True(afterChildCompletionRestart.TryClaimParentCompletion(invocation.InvocationId, "worker-1", TimeSpan.FromMinutes(1)));
        Assert.False(afterChildCompletionRestart.TryClaimParentCompletion(invocation.InvocationId, "worker-2", TimeSpan.FromMinutes(1)));
        afterChildCompletionRestart.MarkCompleted(invocation.InvocationId, "worker-1");

        var finalRestart = new GatewayAgentToolInvocationStore(database.Database);
        AgentToolInvocation completed = Assert.IsType<AgentToolInvocation>(finalRestart.Get(invocation.InvocationId));
        Assert.Equal(AgentToolInvocationStates.Completed, completed.State);
        Assert.NotNull(completed.CompletedAt);
        Assert.False(finalRestart.TryClaimParentCompletion(invocation.InvocationId, "worker-3", TimeSpan.FromMinutes(1)));

        var links = new GatewayAgentToolInvocationLinkStore(database.Database);
        Assert.Equal(invocation.InvocationId, links.GetByApprovalRequestId("approval-1")?.InvocationId);
        Assert.Equal(invocation.InvocationId, links.GetByApprovalRequestId("approval-2")?.InvocationId);
    }

    [Fact]
    public void ExpiredCompletionLeaseIsRecoverableAfterRestart()
    {
        using var database = new TemporaryDatabase();
        var firstProcess = new GatewayAgentToolInvocationStore(database.Database);
        AgentToolInvocation invocation = firstProcess.CreateWaiting(CreateRequest());
        firstProcess.RecordChildCompletion(invocation.InvocationId, "{\"result\":\"73\"}", "completed");
        Assert.True(firstProcess.TryClaimParentCompletion(invocation.InvocationId, "crashed-worker", TimeSpan.FromMinutes(1)));

        using (var connection = new SqliteConnection(database.Database.ConnectionString))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE agent_tool_invocations SET completion_lease_expires_at = $expired WHERE invocation_id = $invocationId";
            command.Parameters.AddWithValue("$expired", DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"));
            command.Parameters.AddWithValue("$invocationId", invocation.InvocationId);
            command.ExecuteNonQuery();
        }

        var restarted = new GatewayAgentToolInvocationStore(database.Database);
        Assert.Contains(restarted.ListRecoverable(), item => item.InvocationId == invocation.InvocationId);
        Assert.True(restarted.TryClaimParentCompletion(invocation.InvocationId, "recovery-worker", TimeSpan.FromMinutes(1)));
        restarted.MarkCompleted(invocation.InvocationId, "recovery-worker");
    }

    [Fact]
    public void DurableChildCompletionDoesNotReopenCancelledInvocation()
    {
        using var database = new TemporaryDatabase();
        var store = new GatewayAgentToolInvocationStore(database.Database);
        AgentToolInvocation invocation = store.CreateWaiting(CreateRequest());
        store.MarkFailed(invocation.InvocationId, "cancelled");

        AgentToolInvocation cancelled = Assert.IsType<AgentToolInvocation>(store.Get(invocation.InvocationId));
        Assert.Equal(AgentToolInvocationStates.Failed, cancelled.State);
        Assert.Equal("cancelled", cancelled.FailureCode);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            store.RecordChildCompletion(invocation.InvocationId, "{\"result\":\"recovered\"}", "completed"));
        Assert.Contains("cannot record a child completion", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvocationLinkLookupCorrelatesExactCallerToolCallWithContinuation()
    {
        using var database = new TemporaryDatabase();
        var invocations = new GatewayAgentToolInvocationStore(database.Database);
        AgentToolInvocation firstInvocation = invocations.CreateWaiting(CreateRequest());
        AgentToolInvocation secondInvocation = invocations.CreateWaiting(CreateRequest() with
        {
            ParentToolCallId = "parent-tool-call-2",
            ChildRunId = "child-run-2",
            ChildSessionId = "child-session-2",
            ParentContinuationRunId = "parent-continuation-2",
        });
        InsertApproval(database.Database, "approval-1", "child-run-1");
        InsertApproval(database.Database, "approval-2", "child-run-2");
        var links = new GatewayAgentToolInvocationLinkStore(database.Database);
        AgentToolInvocationLink first = CreateLink(firstInvocation.InvocationId, "approval-1", "child-run-1", "event-1");
        AgentToolInvocationLink second = CreateLink(secondInvocation.InvocationId, "approval-2", "child-run-2", "event-2") with
        {
            CallerToolCallId = "parent-tool-call-2",
            CallerContinuationRunId = "parent-continuation-2",
            ChildSessionId = "child-session-2",
        };
        links.Upsert(first);
        links.Upsert(second);

        AgentToolInvocationLink resolved = Assert.IsType<AgentToolInvocationLink>(
            links.GetByCallerSessionAndToolCallId("parent-session-1", "parent-tool-call-1"));

        Assert.Equal("approval-1", resolved.ApprovalRequestId);
        Assert.Equal("parent-continuation-1", resolved.CallerContinuationRunId);
        Assert.Null(links.GetByCallerSessionAndToolCallId("parent-session-1", "missing-call"));
    }

    [Fact]
    public void ApprovalLinkIsImmutableAcrossRestartAndRejectsConflictingLineage()
    {
        using var database = new TemporaryDatabase();
        var firstProcess = new GatewayAgentToolInvocationStore(database.Database);
        AgentToolInvocation invocation = firstProcess.CreateWaiting(CreateRequest());
        InsertApproval(database.Database, "approval-immutable", "child-run-1");
        AgentToolInvocationLink original = CreateLink(
            invocation.InvocationId,
            "approval-immutable",
            "child-run-1",
            "event-original");
        firstProcess.RecordApprovalLink(original);

        var restarted = new GatewayAgentToolInvocationStore(database.Database);
        AgentToolInvocationLink conflicting = original with
        {
            ChildRunId = "child-run-other",
            RootRunId = "root-run-other",
            CanonicalEventId = "event-other",
        };

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => restarted.RecordApprovalLink(conflicting));
        Assert.Contains("different durable AAAT lineage", exception.Message, StringComparison.Ordinal);

        AgentToolInvocationLink persisted = Assert.IsType<AgentToolInvocationLink>(
            new GatewayAgentToolInvocationLinkStore(database.Database)
                .GetByApprovalRequestId("approval-immutable"));
        Assert.Equal("child-run-1", persisted.ChildRunId);
        Assert.Equal("root-run-1", persisted.RootRunId);
        Assert.Equal("event-original", persisted.CanonicalEventId);
    }

    [Fact]
    public void LegacyInvocationLinkStoreAlsoRejectsConflictingDuplicateApprovalLineage()
    {
        using var database = new TemporaryDatabase();
        AgentToolInvocation invocation = new GatewayAgentToolInvocationStore(database.Database).CreateWaiting(CreateRequest());
        InsertApproval(database.Database, "approval-immutable", "child-run-1");
        var store = new GatewayAgentToolInvocationLinkStore(database.Database);
        AgentToolInvocationLink original = CreateLink(invocation.InvocationId, "approval-immutable", "child-run-1", "event-original");
        store.Upsert(original);

        store.Upsert(original with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1).ToString("O") });
        Assert.Throws<InvalidOperationException>(() => store.Upsert(original with { ChildRunId = "child-run-other" }));

        Assert.Equal("child-run-1", store.GetByApprovalRequestId("approval-immutable")?.ChildRunId);
    }

    private static AgentToolInvocationCreateRequest CreateRequest()
        => new(
            "parent-run-1",
            "parent-session-1",
            "parent-tool-call-1",
            "invoke_agent_child",
            "child-run-1",
            "child-session-1",
            "child-agent-1",
            "root-run-1",
            "parent-continuation-1");

    private static AgentToolInvocationLink CreateLink(
        string invocationId,
        string approvalRequestId,
        string childRunId,
        string eventId)
    {
        string now = DateTimeOffset.UtcNow.ToString("O");
        return new AgentToolInvocationLink(
            approvalRequestId,
            "parent-session-1",
            now,
            now,
            eventId,
            "parent-tool-call-1",
            "invoke_agent_child",
            "parent-continuation-1",
            "child-session-1",
            "parent-run-1",
            childRunId,
            "child-agent-1",
            "root-run-1",
            invocationId);
    }

    private static void InsertApproval(KautukaDatabase database, string requestId, string runId)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO tool_approval_requests
                (id, session_id, run_id, tool_call_id, tool_name, requested_at)
            VALUES ($requestId, 'child-session-1', $runId, $toolCallId, 'run_shell', $now)
            """;
        command.Parameters.AddWithValue("$requestId", requestId);
        command.Parameters.AddWithValue("$runId", runId);
        command.Parameters.AddWithValue("$toolCallId", $"tool-{requestId}");
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "kautuka-aaat-aggregate-tests", Guid.NewGuid().ToString("N"));

        public TemporaryDatabase()
        {
            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Kautuka:ContainerStateRoot"] = root,
                })
                .Build();
            Database = new KautukaDatabase(configuration);
            Database.EnsureReady();
        }

        public KautukaDatabase Database { get; }

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
