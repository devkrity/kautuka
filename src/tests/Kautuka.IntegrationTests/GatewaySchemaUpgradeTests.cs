using System.Text.Json;

using Kautuka.Gateway.Domain.Autopilot;
using Kautuka.Gateway.Domain.Memory;
using Kautuka.Gateway.Domain.Sandbox;
using Kautuka.Gateway.Domain.Stores;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class GatewaySchemaUpgradeTests
{
    private const string fixtureFileName = "gateway-2026-10-10.1.sql";

    [Fact]
    public void PreviousCurrentSchemaInvalidatesInvitesWithoutEmailAndExpirySnapshots()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        KautukaDatabase database = CreateDatabase(temp.SqlitePath);
        database.EnsureReady();
        using (SqliteConnection connection = new(database.ConnectionString))
        {
            connection.Open();
            connection.ExecuteScript(
                "ALTER TABLE external_login_invites DROP COLUMN expires_at; " +
                "ALTER TABLE external_login_invites DROP COLUMN email; " +
                "INSERT INTO external_login_invites(code_hash,user_id,created_at) VALUES ('legacy-hash','user-1','created'); " +
                "UPDATE gateway_schema SET schema_id='gateway-2026-10-23.2';");
        }

        database.EnsureReady();
        using SqliteConnection migrated = new(database.ConnectionString);
        migrated.Open();
        SqliteGatewaySchemaBootstrapper.ValidateExisting(migrated);
        Assert.Equal(SqliteGatewaySchemaBootstrapper.SchemaId, migrated.ExecuteScalar<string>("SELECT schema_id FROM gateway_schema"));
        Assert.Equal(0L, migrated.ExecuteScalar<long>("SELECT COUNT(*) FROM external_login_invites"));
        database.EnsureReady();
    }

    [Theory]
    [InlineData("gateway-2026-10-10.1")]
    [InlineData("gateway-2026-10-19.1")]
    [InlineData("gateway-2026-10-23.1")]
    public void ProductionBootstrapMigratesKnownLegacyMarkersAndPreservesForeignKeyChildren(string sourceSchemaId)
    {
        using var temp = new TemporaryGatewayStateDirectory();
        string fixture = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureFileName));
        using (var connection = new SqliteConnection($"Data Source={temp.SqlitePath}"))
        {
            connection.Open();
            connection.ExecuteScript("PRAGMA foreign_keys=ON;");
            connection.ExecuteScript(fixture);
            connection.ExecuteScript($"UPDATE gateway_schema SET schema_id='{sourceSchemaId}';");
            connection.ExecuteScript(
                """
                INSERT INTO autopilot_schedules(
                    id, owner_user_id, target_type, target_id, cron_expression, timezone_id, mode,
                    notify_telegram, created_at, updated_at)
                VALUES ('schedule-1', 'user-1', 'agent', 'agent-1', '* * * * *', 'UTC', 'prompt', 1, 'created', 'updated');
                INSERT INTO runtime_channel_interactions(
                    id, registration_id, chat_id, prompt, created_at, expires_at, telegram_message_id, provider_message_id)
                VALUES ('interaction-1', 'telegram-1', 'chat-1', 'approve?', 'created', 'expires', 481, 'provider-481');
                INSERT INTO runtime_users(username, user_id, created_at, updated_at, telegramuser_idsJson, telegram_bot_ids_json)
                VALUES ('amit', 'user-1', 'created', 'updated', '[]', '[]');
                INSERT INTO tool_approval_requests(id, tool_call_id, tool_name, tool_type, requested_at)
                VALUES ('approval-1', 'call-1', 'shell', 'mcp', 'requested');
                INSERT INTO agent_tool_invocations(
                    invocation_id, parent_run_id, parent_session_id, parent_tool_call_id, parent_tool_name,
                    child_run_id, child_session_id, child_agent_id, root_run_id, state, created_at, updated_at)
                VALUES ('invocation-1', 'parent-run', 'parent-session', 'call-1', 'invoke_agent',
                    'child-run', 'child-session', 'child-agent', 'root-run', 'waiting_on_child', 'created', 'updated');
                INSERT INTO agent_tool_invocation_links(
                    approval_request_id, invocation_id, caller_session_id, caller_tool_call_id, caller_tool_name,
                    child_session_id, caller_run_id, child_run_id, child_agent_id, root_run_id, canonical_event_id,
                    created_at, updated_at)
                VALUES ('approval-1', 'invocation-1', 'parent-session', 'call-1', 'invoke_agent',
                    'child-session', 'parent-run', 'child-run', 'child-agent', 'root-run', 'event-1', 'created', 'updated');
                INSERT INTO approval_continuation_jobs(
                    request_id, session_id, target_type, target_id, approved, resolution_source, stop_run,
                    status, available_at, created_at, updated_at)
                VALUES ('approval-1', 'parent-session', 'agent', 'agent-1', 1, 0, 0,
                    'pending', 'now', 'created', 'updated');
                INSERT INTO approval_resolution_events(
                    event_id, request_id, payload_json, agent_id, session_id, user_id, run_id, root_run_id,
                    status, available_at, created_at, updated_at)
                VALUES ('resolution-1', 'approval-1', '{}', 'agent-1', 'parent-session', 'user-1', 'parent-run',
                    'root-run', 'pending', 'now', 'created', 'updated');
                """);
        }

        KautukaDatabase database = CreateDatabase(temp.SqlitePath);
        database.EnsureReady();

        using SqliteConnection migrated = new(database.ConnectionString);
        migrated.Open();
        SqliteGatewaySchemaBootstrapper.ValidateExisting(migrated);
        Assert.Equal(SqliteGatewaySchemaBootstrapper.SchemaId, migrated.ExecuteScalar<string>("SELECT schema_id FROM gateway_schema"));
        Assert.Equal(1L, migrated.ExecuteScalar<long>("SELECT notify_channels FROM autopilot_schedules WHERE id='schedule-1'"));
        Assert.Equal(481L, migrated.ExecuteScalar<long>("SELECT channel_message_id FROM runtime_channel_interactions WHERE id='interaction-1'"));
        Assert.Equal("user-1", migrated.ExecuteScalar<string>("SELECT user_id FROM runtime_users WHERE username='amit'"));
        Assert.Equal("single", migrated.ExecuteScalar<string>("SELECT channel_tether_mode FROM tool_approval_requests WHERE id='approval-1'"));
        Assert.Equal(1L, migrated.ExecuteScalar<long>("SELECT COUNT(*) FROM agent_tool_invocation_links WHERE approval_request_id='approval-1'"));
        Assert.Equal(1L, migrated.ExecuteScalar<long>("SELECT COUNT(*) FROM approval_continuation_jobs WHERE request_id='approval-1'"));
        Assert.Equal(1L, migrated.ExecuteScalar<long>("SELECT COUNT(*) FROM approval_resolution_events WHERE request_id='approval-1'"));
        using (SqliteCommand foreignKeyCheck = migrated.CreateCommand())
        {
            foreignKeyCheck.CommandText = "PRAGMA foreign_key_check";
            using SqliteDataReader reader = foreignKeyCheck.ExecuteReader();
            Assert.False(reader.Read());
        }
    }

    [Fact]
    public void FreshDatabaseBaselineSupportsGatewayStoresAndStateRoundTrips()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        IConfiguration configuration = CreateConfiguration(temp.SqlitePath);
        var database = new KautukaDatabase(configuration);
        database.EnsureReady();

        var autopilot = new GatewayAutopilotStore(database);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var schedule = new GatewayAutopilotSchedule(
            "schedule-smoke", "user-smoke", "agent", "agent-smoke", "0 * * * *", "UTC", "prompt",
            false, "context", [], true, true, "next_occurrence", null, "skip_and_notify", false,
            now.ToString("O"), now.ToString("O"), null, null, null, null);
        autopilot.CreateSchedule(schedule);
        GatewayAutopilotSchedule updatedSchedule = schedule with { TargetId = "agent-updated", NotifyChannels = false };
        Assert.True(autopilot.UpdateSchedule(updatedSchedule));
        Assert.Equal("agent-updated", Assert.Single(autopilot.ListSchedulesByOwner("user-smoke")).TargetId);

        GatewayState state = CreateState(database, configuration);
        var userCommands = new SqliteGatewayUserCommandRepository(database);
        var users = new GatewayUserStore(state, new NoOpTokenStore(), userCommands);
        using JsonDocument invitation = JsonDocument.Parse("""{"username":"smoke-user","password":"Password-123!","role":"user"}""");
        Assert.NotNull(users.InviteUser(invitation.RootElement, out string? inviteError));
        Assert.Null(inviteError);

        state.UpsertChannelInteraction(new GatewayChannelInteraction(
            "interaction-smoke", "telegram-smoke", "user-smoke", "agent", "agent-smoke", "session-smoke", null,
            "chat-smoke", null, "prompt", [], "pending", now.ToString("O"), now.AddMinutes(5).ToString("O"), null, null, null,
            SourceMessageId: 522, SourceTransportMessageId: "transport-522"));

        GatewayState reloaded = CreateState(database, configuration);
        Assert.True(reloaded.Users.ContainsKey("smoke-user"));
        Assert.Equal(522L, Assert.Single(reloaded.ChannelInteractions.Values).SourceMessageId);
        Assert.Equal("transport-522", Assert.Single(reloaded.ChannelInteractions.Values).SourceTransportMessageId);

        var approvals = new GatewayApprovalStore(database);
        approvals.CreateRequest(new GatewayToolApprovalRequest(
            "approval-smoke", "session-smoke", "run-smoke", null, "agent-smoke", "user-smoke", "call-smoke",
            "shell", "mcp", null, "{}", now.ToString("O"), null, null, null, null, null));
        Assert.Equal("mirror", approvals.GetById("approval-smoke")?.ChannelTetherMode);
    }

    private static KautukaDatabase CreateDatabase(string sqlitePath) => new(CreateConfiguration(sqlitePath));

    private static IConfiguration CreateConfiguration(string sqlitePath) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kautuka:ContainerStateRoot"] = Path.GetDirectoryName(sqlitePath),
            })
            .Build();

    private static GatewayState CreateState(KautukaDatabase database, IConfiguration configuration) =>
        GatewayState.FromDatabase(
            database,
            NullLogger<GatewayState>.Instance,
            new GatewayMemoryStore(),
            new SqliteAgentDefinitionStore(configuration),
            configuration,
            new NoOpWorkspaceRuntimeTool());

    private sealed class TemporaryGatewayStateDirectory : IDisposable
    {
        public TemporaryGatewayStateDirectory()
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"kautuka-schema-upgrade-{Guid.NewGuid():N}");
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }
        public string SqlitePath => Path.Combine(RootPath, "kautuka.db");

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }

    private sealed class NoOpTokenStore : IGatewayTokenStore
    {
        public string IssueAccessToken(string userId) => string.Empty;
        public string IssueRefreshToken(string userId) => string.Empty;
        public bool TryResolveAccessToken(string token, out string? userId) { userId = null; return false; }
        public bool TryResolveRefreshToken(string token, out string? userId) { userId = null; return false; }
        public void RevokeRefreshToken(string token) { }
        public bool TryRotateRefreshToken(string token, out string? userId, out string? replacementToken) { userId = null; replacementToken = null; return false; }
        public void RevokeUserSessions(string userId) { }
    }

    private sealed class NoOpWorkspaceRuntimeTool : IWorkspaceRuntimeTool
    {
        public WorkspaceRuntimeContext DescribeAgentWorkspace(string agentId, string? userId = null) => WorkspaceRuntimeContext.Unavailable;
        public WorkspaceRuntimeContext RebuildAgentWorkspace(string agentId, string? userId = null) => WorkspaceRuntimeContext.Unavailable;
        public bool DeleteAgentWorkspace(string agentId) => true;
        public Task<WorkspaceSandboxSession> SpawnAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default) => Task.FromResult(new WorkspaceSandboxSession(string.Empty, false, "agent", agentId, userId));
        public Task<WorkspaceCommandResult> RunAgentShellAsync(string agentId, string command, string? userId = null, int? timeoutSeconds = null, CancellationToken cancellationToken = default) => Task.FromResult(new WorkspaceCommandResult("completed", 0, string.Empty, string.Empty));
        public Task<WorkspaceSandboxSession> InspectAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default) => Task.FromResult(new WorkspaceSandboxSession(string.Empty, false, "agent", agentId, userId));
        public Task CloseAgentSandboxAsync(string agentId, string? userId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

internal static class SqliteSchemaTestExtensions
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "This helper is used only with fixed SQL text and the frozen schema fixture in this test file.")]
    public static void ExecuteScript(this SqliteConnection connection, string commandText)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        command.ExecuteNonQuery();
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "This helper is used only with fixed SQL text in this test file.")]
    public static T ExecuteScalar<T>(this SqliteConnection connection, string commandText)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        object? value = command.ExecuteScalar();
        if (value is null or DBNull)
        {
            throw new InvalidOperationException("Expected a non-null scalar result.");
        }

        return (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture)!;
    }
}
