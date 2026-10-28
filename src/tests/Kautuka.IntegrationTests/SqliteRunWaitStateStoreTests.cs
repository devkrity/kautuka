using System.Globalization;

using Kautuka.Gateway.Domain.Stores;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Kautuka.IntegrationTests;

public sealed class SqliteRunWaitStateStoreTests
{
    [Theory]
    [InlineData(RunWaitStatuses.Completed)]
    [InlineData(RunWaitStatuses.Failed)]
    [InlineData(RunWaitStatuses.PausedAgain)]
    public void RepeatedTerminalSettlementPreservesTheFirstClosedState(string firstStatus)
    {
        using TemporaryDatabase temp = TemporaryDatabase.Create();
        temp.EnsureReady();
        var store = new SqliteRunWaitStateStore(temp.Database);
        RunWaitState wait = store.PauseRunForApproval(CreateApprovalWaitRequest("approval-a", "approval-a", "event-a"));
        Assert.True(store.TryClaimExecution(wait.WaitId, "owner", TimeSpan.FromMinutes(2)));
        if (firstStatus == RunWaitStatuses.Completed) store.MarkCompleted(wait.WaitId);
        else if (firstStatus == RunWaitStatuses.Failed) store.MarkFailed(wait.WaitId, "original_failure");
        else store.MarkPausedAgain(wait.WaitId);
        RunWaitState closed = store.GetByWaitId(wait.WaitId)!;
        store.MarkCompleted(wait.WaitId);
        store.MarkFailed(wait.WaitId, "late_cleanup");
        Assert.Equal(closed, store.GetByWaitId(wait.WaitId));
        Assert.Throws<InvalidOperationException>(() => store.ReleaseExecutingWait(wait.WaitId));
    }

    [Fact]
    public void TerminalCleanupAfterSessionDeletionDoesNotRecreateWait()
    {
        using TemporaryDatabase temp = TemporaryDatabase.Create();
        temp.EnsureReady();
        var store = new SqliteRunWaitStateStore(temp.Database);
        RunWaitState wait = store.PauseRunForApproval(CreateApprovalWaitRequest("approval-a", "approval-a", "event-a"));
        store.DeleteBySession(wait.SessionId!);
        store.MarkCompleted(wait.WaitId);
        store.MarkFailed(wait.WaitId, "late_cleanup");
        Assert.Null(store.GetByWaitId(wait.WaitId));
    }

    [Fact]
    public void EnsureReadyCreatesCurrentRunWaitStateSchema()
    {
        using TemporaryDatabase temp = TemporaryDatabase.Create();

        temp.EnsureReady();

        Assert.True(temp.ColumnExists("run_wait_states", "canonical_event_id"));
        Assert.True(temp.ColumnExists("run_wait_states", "root_run_id"));

        var store = new SqliteRunWaitStateStore(temp.Database);
        RunWaitState state = store.PauseRunForApproval(new RunWaitStateCreateRequest(
            ContinuationRunId: "continuation-run-1",
            RunId: "run-1",
            TargetType: "agent",
            TargetId: "agent-1",
            SessionId: "session-1",
            AgentId: "agent-1",
            UserId: "user-1",
            CorrelationId: "approval-1",
            SessionStateJson: "{}",
            BehaviorJson: "{}",
            ArtifactManifestJson: "[]",
            CanonicalEventId: "event-approval-1",
            ToolCallId: "call-1",
            ToolName: "tool-1",
            ApprovalRequestId: "approval-1"));

        string canonicalEventId = temp.QueryString($"SELECT canonical_event_id FROM run_wait_states WHERE wait_id = '{state.WaitId}'");
        Assert.Equal("event-approval-1", canonicalEventId);
    }

    [Fact]
    public void ActiveChildPauseCanBeLoadedForItsCaller()
    {
        using TemporaryDatabase temp = TemporaryDatabase.Create();
        temp.EnsureReady();
        var store = new SqliteRunWaitStateStore(temp.Database);
        RunWaitState paused = store.PauseRunForChild(new RunWaitStateCreateRequest(
            ContinuationRunId: "continuation-run-1",
            RunId: "run-1",
            TargetType: "agent",
            TargetId: "agent-1",
            SessionId: "caller-session-1",
            AgentId: "agent-1",
            UserId: "user-1",
            CorrelationId: "child-run-1",
            SessionStateJson: "{}",
            BehaviorJson: "{}",
            ArtifactManifestJson: "[]",
            CanonicalEventId: "event-child-1",
            ToolCallId: "invoke-agent-1",
            ToolName: "invoke_agent_child",
            ChildSessionId: "child-session-1",
            ChildRunId: "child-run-1"));

        RunWaitState? loaded = store.GetActiveChildPauseForCaller(
            "caller-session-1",
            "invoke-agent-1");

        Assert.NotNull(loaded);
        Assert.Equal(paused.WaitId, loaded.WaitId);
        Assert.Equal(RunWaitReasons.Child, loaded.WaitReason);
    }

    [Fact]
    public void ApprovalWaitRequiresMatchingImmutableApprovalCorrelationAndAllowsFreshLaterCycle()
    {
        using TemporaryDatabase temp = TemporaryDatabase.Create();
        temp.EnsureReady();
        var store = new SqliteRunWaitStateStore(temp.Database);

        Assert.Throws<InvalidOperationException>(() => store.PauseRunForApproval(CreateApprovalWaitRequest(
            correlationId: "other-approval", approvalRequestId: "approval-a", canonicalEventId: "event-a")));

        RunWaitState first = store.PauseRunForApproval(CreateApprovalWaitRequest(
            correlationId: "approval-a", approvalRequestId: "approval-a", canonicalEventId: "event-a"));
        Assert.Equal("approval-a", first.CorrelationId);
        Assert.Equal("approval-a", first.ApprovalRequestId);
        Assert.Throws<SqliteException>(() => store.PauseRunForApproval(CreateApprovalWaitRequest(
            correlationId: "approval-a", approvalRequestId: "approval-a", canonicalEventId: "event-duplicate")));

        store.MarkPausedAgain(first.WaitId);
        RunWaitState second = store.PauseRunForApproval(CreateApprovalWaitRequest(
            correlationId: "approval-b", approvalRequestId: "approval-b", canonicalEventId: "event-b"));
        Assert.NotEqual(first.WaitId, second.WaitId);
        Assert.NotEqual(first.ApprovalRequestId, second.ApprovalRequestId);
    }

    private static RunWaitStateCreateRequest CreateApprovalWaitRequest(
        string correlationId,
        string approvalRequestId,
        string canonicalEventId)
        => new(
            ContinuationRunId: "continuation-run-1",
            RunId: "run-1",
            TargetType: "agent",
            TargetId: "agent-1",
            SessionId: "session-1",
            AgentId: "agent-1",
            UserId: "user-1",
            CorrelationId: correlationId,
            SessionStateJson: "{}",
            BehaviorJson: "{}",
            ArtifactManifestJson: "[]",
            CanonicalEventId: canonicalEventId,
            ApprovalRequestId: approvalRequestId);

    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string directoryPath;
        private readonly IConfiguration configuration;

        private TemporaryDatabase(string directoryPath, IConfiguration configuration)
        {
            this.directoryPath = directoryPath;
            this.configuration = configuration;
            Database = new KautukaDatabase(configuration);
        }

        public KautukaDatabase Database { get; }

        private string ConnectionString => Database.ConnectionString;

        public static TemporaryDatabase Create()
        {
            string directoryPath = CreateDirectory();
            IConfiguration configuration = CreateConfiguration(directoryPath);
            return new TemporaryDatabase(directoryPath, configuration);
        }

        public void EnsureReady() => Database.EnsureReady();

        public bool ColumnExists(string tableName, string columnName)
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Test-only fixed table identifiers.
            command.CommandText = $"PRAGMA table_info({tableName})";
#pragma warning restore CA2100
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public long QueryInt64(string sql)
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Test helper receives fixed SQL literals from test methods.
            command.CommandText = sql;
#pragma warning restore CA2100
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        public string QueryString(string sql)
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Test helper receives fixed SQL literals from test methods.
            command.CommandText = sql;
#pragma warning restore CA2100
            return (string?)command.ExecuteScalar()
                ?? throw new InvalidOperationException("Expected query to return a string value.");
        }

        private static IConfiguration CreateConfiguration(string directoryPath)
            => new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Kautuka:ContainerStateRoot"] = directoryPath,
                })
                .Build();

        private static string CreateDirectory()
        {
            string directoryPath = Path.Combine(Path.GetTempPath(), "kautuka-run-wait-schema-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directoryPath);
            return directoryPath;
        }

        public void Dispose()
        {
            SqliteTestPoolCleanup.ClearPoolsUnderDirectory(directoryPath);
            if (Directory.Exists(directoryPath))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }
    }
}
