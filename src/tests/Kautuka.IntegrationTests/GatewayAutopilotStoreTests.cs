using Kautuka.Gateway.Domain.Autopilot;
using Kautuka.Gateway.Domain.Stores;

using Microsoft.Extensions.Configuration;

namespace Kautuka.IntegrationTests;

public sealed class GatewayAutopilotStoreTests
{
    [Fact]
    public void LatestNonTerminalRunIsNotHiddenByNewerSkippedOccurrence()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        var store = new GatewayAutopilotStore(CreateDatabase(temp.SqlitePath));
        DateTimeOffset now = DateTimeOffset.UtcNow;

        store.InsertRun(CreateRun("pending-run", AutopilotRunStatuses.AwaitingPreRunApproval, now));
        store.InsertRun(CreateRun("skipped-run", AutopilotRunStatuses.Skipped, now.AddSeconds(1)));

        GatewayScheduledRun? blocker = store.GetLatestNonTerminalRunBySchedule("schedule-1");
        Assert.Equal("pending-run", blocker?.Id);

        bool inserted = store.TryInsertRunUnlessNonTerminal(
            CreateRun("next-run", AutopilotRunStatuses.Scheduled, now.AddSeconds(2)),
            out GatewayScheduledRun? concurrentBlocker);

        Assert.False(inserted);
        Assert.Equal("pending-run", concurrentBlocker?.Id);
    }

    [Fact]
    public void SetRunSkipReasonPersistsOnTheSqliteRun()
    {
        using var temp = new TemporaryGatewayStateDirectory();
        var store = new GatewayAutopilotStore(CreateDatabase(temp.SqlitePath));
        GatewayScheduledRun run = CreateRun("scheduled-run", AutopilotRunStatuses.Scheduled, DateTimeOffset.UtcNow);
        store.InsertRun(run);

        Assert.True(store.SetRunSkipReason(run.Id, "overlap_skipped", DateTimeOffset.UtcNow));

        Assert.Equal("overlap_skipped", store.GetRunById(run.Id)?.SkipReason);
    }

    private static GatewayScheduledRun CreateRun(string id, string status, DateTimeOffset createdAt) =>
        new(
            Id: id,
            ScheduleId: "schedule-1",
            OwnerUserId: "user-1",
            TargetType: "agent",
            TargetId: "agent-1",
            Status: status,
            TriggerType: "cron",
            ScheduledFor: createdAt.ToString("O"),
            SessionId: null,
            RuntimeRunId: null,
            PreRunApprovalRequestId: status == AutopilotRunStatuses.AwaitingPreRunApproval ? "approval-1" : null,
            InputJson: "{}",
            OutputJson: null,
            Error: null,
            StateCategory: AutopilotRunStatuses.GetStateCategory(status),
            SkipReason: status == AutopilotRunStatuses.Skipped ? "blocked_by_non_terminal_run" : null,
            BlockingRunId: status == AutopilotRunStatuses.Skipped ? "pending-run" : null,
            BlockingRunStatusSnapshot: status == AutopilotRunStatuses.Skipped ? AutopilotRunStatuses.AwaitingPreRunApproval : null,
            BlockingRunStateCategory: status == AutopilotRunStatuses.Skipped ? AutopilotRunStateCategories.PendingApproval : null,
            BlockedAt: status == AutopilotRunStatuses.Skipped ? createdAt.ToString("O") : null,
            CreatedAt: createdAt.ToString("O"),
            UpdatedAt: createdAt.ToString("O"),
            StartedAt: null,
            EndedAt: AutopilotRunStatuses.IsTerminal(status) ? createdAt.ToString("O") : null);

    private static KautukaDatabase CreateDatabase(string sqlitePath)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Kautuka:ContainerStateRoot"] = Path.GetDirectoryName(sqlitePath)
                    ?? throw new InvalidOperationException("Expected sqlite path to include a parent directory."),
            })
            .Build();

        var database = new KautukaDatabase(configuration);
        database.EnsureReady();
        return database;
    }

    private sealed class TemporaryGatewayStateDirectory : IDisposable
    {
        public TemporaryGatewayStateDirectory()
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"kautuka-autopilot-store-{Guid.NewGuid():N}");
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }

        public string SqlitePath => Path.Combine(RootPath, "kautuka.db");

        public void Dispose()
        {
            SqliteTestPoolCleanup.ClearPoolsUnderDirectory(RootPath);
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}
