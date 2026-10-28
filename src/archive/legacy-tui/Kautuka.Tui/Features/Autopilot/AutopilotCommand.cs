using System.Diagnostics.CodeAnalysis;

using Kautuka.Tui.Cli;
using Kautuka.Tui.Common;
using Kautuka.Tui.Infrastructure.Gateway;

using Spectre.Console;
using Spectre.Console.Rendering;

namespace Kautuka.Tui.Features.Autopilot;

public sealed class AutopilotCommand
{
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Shell command helpers intentionally keep an instance-based surface.")]
    public IRenderable BuildSchedulesRenderable(IReadOnlyList<GatewayAutopilotSchedule> schedules, IReadOnlyDictionary<string, string>? agentNames = null)
    {
        ArgumentNullException.ThrowIfNull(schedules);

        if (schedules.Count == 0)
        {
            return CreateEmptyPanel("Autopilot Schedules", "No schedules configured yet.");
        }

        Table table = StripedTable.CreateRounded();
        table.AddColumn("Target");
        table.AddColumn("Mode");
        table.AddColumn("Cron");
        table.AddColumn("State");
        table.AddColumn("Next Due");
        table.AddColumn("Notify");

        foreach (GatewayAutopilotSchedule schedule in schedules.OrderBy(item => ResolveTargetName(item.TargetId, agentNames), StringComparer.OrdinalIgnoreCase))
        {
            string notifications = schedule.NotifyUiInbox && schedule.NotifyChannels
                ? "ui, telegram"
                : schedule.NotifyUiInbox
                    ? "ui"
                    : schedule.NotifyChannels
                        ? "telegram"
                        : "-";

            string state = schedule.IsPaused
                ? "paused"
                : string.Equals(schedule.Mode, "off", StringComparison.OrdinalIgnoreCase)
                    ? "disabled"
                    : schedule.RequiresPreRunApproval
                        ? "approval"
                        : "active";

            table.AddStripedRow(
                ResolveTargetName(schedule.TargetId, agentNames),
                schedule.Mode,
                schedule.CronExpression,
                state,
                FormatTimestamp(schedule.NextDueAt),
                notifications);
        }

        return table;
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Shell command helpers intentionally keep an instance-based surface.")]
    public IRenderable BuildRunsRenderable(
        IReadOnlyList<GatewayScheduledRun> runs,
        IReadOnlyList<GatewayAutopilotSchedule> schedules,
        IReadOnlyDictionary<string, string>? agentNames = null)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(schedules);

        if (runs.Count == 0)
        {
            return CreateEmptyPanel("Autopilot Runs", "No scheduled runs recorded yet.");
        }

        Dictionary<string, GatewayAutopilotSchedule> schedulesById = schedules.ToDictionary(item => item.Id, StringComparer.Ordinal);

        Table table = StripedTable.CreateRounded();
        table.AddColumn("Target");
        table.AddColumn("Status");
        table.AddColumn("Trigger");
        table.AddColumn("Scheduled");
        table.AddColumn("Started");
        table.AddColumn("Ended");

        foreach (GatewayScheduledRun run in runs.OrderByDescending(item => ParseTimestamp(item.CreatedAt)))
        {
            string targetId = schedulesById.TryGetValue(run.ScheduleId, out GatewayAutopilotSchedule? schedule)
                ? schedule.TargetId
                : run.TargetId;

            table.AddStripedRow(
                ResolveTargetName(targetId, agentNames),
                run.Status,
                run.TriggerType,
                FormatTimestamp(run.ScheduledFor),
                FormatTimestamp(run.StartedAt),
                FormatTimestamp(run.EndedAt));
        }

        return table;
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Shell command helpers intentionally keep an instance-based surface.")]
    public IRenderable BuildInboxRenderable(
        IReadOnlyList<GatewayAutopilotNotification> notifications,
        IReadOnlyList<GatewayScheduledRun> runs,
        IReadOnlyList<GatewayAutopilotSchedule> schedules,
        IReadOnlyDictionary<string, string>? agentNames = null)
    {
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(schedules);

        if (notifications.Count == 0)
        {
            return CreateEmptyPanel("Autopilot Inbox", "No notifications recorded yet.");
        }

        Dictionary<string, GatewayScheduledRun> runsById = runs.ToDictionary(item => item.Id, StringComparer.Ordinal);
        Dictionary<string, GatewayAutopilotSchedule> schedulesById = schedules.ToDictionary(item => item.Id, StringComparer.Ordinal);

        Table table = StripedTable.CreateRounded();
        table.AddColumn("Target");
        table.AddColumn("Channel");
        table.AddColumn("Status");
        table.AddColumn("Read");
        table.AddColumn("Created");
        table.AddColumn("Sent");

        foreach (GatewayAutopilotNotification notification in notifications.OrderByDescending(item => ParseTimestamp(item.CreatedAt)))
        {
            string targetId = "-";
            if (runsById.TryGetValue(notification.ScheduledRunId, out GatewayScheduledRun? run)
                && schedulesById.TryGetValue(run.ScheduleId, out GatewayAutopilotSchedule? schedule))
            {
                targetId = schedule.TargetId;
            }

            table.AddStripedRow(
                ResolveTargetName(targetId, agentNames),
                string.IsNullOrWhiteSpace(notification.Destination)
                    ? notification.ChannelType
                    : $"{notification.ChannelType} | {notification.Destination}",
                notification.Status,
                notification.IsRead ? "yes" : "no",
                FormatTimestamp(notification.CreatedAt),
                FormatTimestamp(notification.SentAt));
        }

        return table;
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Commands keep a consistent instance-based surface across the TUI application.")]
    public async Task<Result> ExecuteAsync(TuiCommandOptions options, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(args);

        if (args.Count == 0 || args[0].Equals("schedules", StringComparison.OrdinalIgnoreCase))
        {
            return await ShowSchedulesAsync(options, cancellationToken).ConfigureAwait(false);
        }

        if (args.Count == 1 && args[0].Equals("runs", StringComparison.OrdinalIgnoreCase))
        {
            return await ShowRunsAsync(options, cancellationToken).ConfigureAwait(false);
        }

        if (args.Count == 1 && args[0].Equals("inbox", StringComparison.OrdinalIgnoreCase))
        {
            return await ShowInboxAsync(options, cancellationToken).ConfigureAwait(false);
        }

        return Results.Failure("Usage: autopilot schedules | autopilot runs | autopilot inbox");
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Shell command helpers intentionally keep an instance-based surface.")]
    public async Task<Result<(IReadOnlyList<GatewayAutopilotSchedule> Schedules, IReadOnlyDictionary<string, string> AgentNames)>> LoadSchedulesWithAgentNamesAsync(TuiCommandOptions options, CancellationToken cancellationToken)
    {
        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure<(IReadOnlyList<GatewayAutopilotSchedule>, IReadOnlyDictionary<string, string>)>(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewayAutopilotSchedule[]> schedulesResult = await client.GetAutopilotSchedulesAsync(cancellationToken).ConfigureAwait(false);
        if (!schedulesResult.IsSuccess || schedulesResult.Value is null)
        {
            return Results.Failure<(IReadOnlyList<GatewayAutopilotSchedule>, IReadOnlyDictionary<string, string>)>(schedulesResult.ErrorMessage ?? "Failed to load autopilot schedules.");
        }

        Result<GatewayAgentsResponse> agentsResult = await client.GetAgentsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, string> agentNames = agentsResult.IsSuccess && agentsResult.Value is not null
            ? agentsResult.Value.Agents.ToDictionary(item => item.Id, item => item.Name, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        IReadOnlyList<GatewayAutopilotSchedule> schedules = schedulesResult.Value;
        return Results.Success((schedules, agentNames));
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Shell command helpers intentionally keep an instance-based surface.")]
    public async Task<Result<(IReadOnlyList<GatewayScheduledRun> Runs, IReadOnlyList<GatewayAutopilotSchedule> Schedules, IReadOnlyDictionary<string, string> AgentNames)>> LoadRunsWithSchedulesAsync(TuiCommandOptions options, CancellationToken cancellationToken)
    {
        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure<(IReadOnlyList<GatewayScheduledRun>, IReadOnlyList<GatewayAutopilotSchedule>, IReadOnlyDictionary<string, string>)>(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewayScheduledRun[]> runsResult = await client.GetAutopilotRunsAsync(cancellationToken).ConfigureAwait(false);
        if (!runsResult.IsSuccess || runsResult.Value is null)
        {
            return Results.Failure<(IReadOnlyList<GatewayScheduledRun>, IReadOnlyList<GatewayAutopilotSchedule>, IReadOnlyDictionary<string, string>)>(runsResult.ErrorMessage ?? "Failed to load autopilot runs.");
        }

        Result<GatewayAutopilotSchedule[]> schedulesResult = await client.GetAutopilotSchedulesAsync(cancellationToken).ConfigureAwait(false);
        if (!schedulesResult.IsSuccess || schedulesResult.Value is null)
        {
            return Results.Failure<(IReadOnlyList<GatewayScheduledRun>, IReadOnlyList<GatewayAutopilotSchedule>, IReadOnlyDictionary<string, string>)>(schedulesResult.ErrorMessage ?? "Failed to load autopilot schedules.");
        }

        Result<GatewayAgentsResponse> agentsResult = await client.GetAgentsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, string> agentNames = agentsResult.IsSuccess && agentsResult.Value is not null
            ? agentsResult.Value.Agents.ToDictionary(item => item.Id, item => item.Name, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        IReadOnlyList<GatewayScheduledRun> runs = runsResult.Value;
        IReadOnlyList<GatewayAutopilotSchedule> schedules = schedulesResult.Value;
        return Results.Success((runs, schedules, agentNames));
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Shell command helpers intentionally keep an instance-based surface.")]
    public async Task<Result<(IReadOnlyList<GatewayAutopilotNotification> Notifications, IReadOnlyList<GatewayScheduledRun> Runs, IReadOnlyList<GatewayAutopilotSchedule> Schedules, IReadOnlyDictionary<string, string> AgentNames)>> LoadInboxAsync(TuiCommandOptions options, CancellationToken cancellationToken)
    {
        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure<(IReadOnlyList<GatewayAutopilotNotification>, IReadOnlyList<GatewayScheduledRun>, IReadOnlyList<GatewayAutopilotSchedule>, IReadOnlyDictionary<string, string>)>(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewayAutopilotNotification[]> inboxResult = await client.GetAutopilotInboxAsync(cancellationToken).ConfigureAwait(false);
        if (!inboxResult.IsSuccess || inboxResult.Value is null)
        {
            return Results.Failure<(IReadOnlyList<GatewayAutopilotNotification>, IReadOnlyList<GatewayScheduledRun>, IReadOnlyList<GatewayAutopilotSchedule>, IReadOnlyDictionary<string, string>)>(inboxResult.ErrorMessage ?? "Failed to load autopilot inbox.");
        }

        Result<GatewayScheduledRun[]> runsResult = await client.GetAutopilotRunsAsync(cancellationToken).ConfigureAwait(false);
        if (!runsResult.IsSuccess || runsResult.Value is null)
        {
            return Results.Failure<(IReadOnlyList<GatewayAutopilotNotification>, IReadOnlyList<GatewayScheduledRun>, IReadOnlyList<GatewayAutopilotSchedule>, IReadOnlyDictionary<string, string>)>(runsResult.ErrorMessage ?? "Failed to load autopilot runs.");
        }

        Result<GatewayAutopilotSchedule[]> schedulesResult = await client.GetAutopilotSchedulesAsync(cancellationToken).ConfigureAwait(false);
        if (!schedulesResult.IsSuccess || schedulesResult.Value is null)
        {
            return Results.Failure<(IReadOnlyList<GatewayAutopilotNotification>, IReadOnlyList<GatewayScheduledRun>, IReadOnlyList<GatewayAutopilotSchedule>, IReadOnlyDictionary<string, string>)>(schedulesResult.ErrorMessage ?? "Failed to load autopilot schedules.");
        }

        Result<GatewayAgentsResponse> agentsResult = await client.GetAgentsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, string> agentNames = agentsResult.IsSuccess && agentsResult.Value is not null
            ? agentsResult.Value.Agents.ToDictionary(item => item.Id, item => item.Name, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

        IReadOnlyList<GatewayAutopilotNotification> notifications = inboxResult.Value;
        IReadOnlyList<GatewayScheduledRun> runs = runsResult.Value;
        IReadOnlyList<GatewayAutopilotSchedule> schedules = schedulesResult.Value;
        return Results.Success((notifications, runs, schedules, agentNames));
    }

    private async Task<Result> ShowSchedulesAsync(TuiCommandOptions options, CancellationToken cancellationToken)
    {
        Result<(IReadOnlyList<GatewayAutopilotSchedule> Schedules, IReadOnlyDictionary<string, string> AgentNames)> dataResult = await LoadSchedulesWithAgentNamesAsync(options, cancellationToken).ConfigureAwait(false);
        if (!dataResult.IsSuccess)
        {
            return Results.Failure(dataResult.ErrorMessage ?? "Failed to load autopilot schedules.");
        }

        AnsiConsole.Write(BuildSchedulesRenderable(dataResult.Value.Schedules, dataResult.Value.AgentNames));
        return Results.Success();
    }

    private async Task<Result> ShowRunsAsync(TuiCommandOptions options, CancellationToken cancellationToken)
    {
        Result<(IReadOnlyList<GatewayScheduledRun> Runs, IReadOnlyList<GatewayAutopilotSchedule> Schedules, IReadOnlyDictionary<string, string> AgentNames)> dataResult = await LoadRunsWithSchedulesAsync(options, cancellationToken).ConfigureAwait(false);
        if (!dataResult.IsSuccess)
        {
            return Results.Failure(dataResult.ErrorMessage ?? "Failed to load autopilot runs.");
        }

        AnsiConsole.Write(BuildRunsRenderable(dataResult.Value.Runs, dataResult.Value.Schedules, dataResult.Value.AgentNames));
        return Results.Success();
    }

    private async Task<Result> ShowInboxAsync(TuiCommandOptions options, CancellationToken cancellationToken)
    {
        Result<(IReadOnlyList<GatewayAutopilotNotification> Notifications, IReadOnlyList<GatewayScheduledRun> Runs, IReadOnlyList<GatewayAutopilotSchedule> Schedules, IReadOnlyDictionary<string, string> AgentNames)> dataResult = await LoadInboxAsync(options, cancellationToken).ConfigureAwait(false);
        if (!dataResult.IsSuccess)
        {
            return Results.Failure(dataResult.ErrorMessage ?? "Failed to load autopilot inbox.");
        }

        AnsiConsole.Write(BuildInboxRenderable(dataResult.Value.Notifications, dataResult.Value.Runs, dataResult.Value.Schedules, dataResult.Value.AgentNames));
        return Results.Success();
    }

    private static string ResolveTargetName(string targetId, IReadOnlyDictionary<string, string>? agentNames)
        => agentNames is not null && agentNames.TryGetValue(targetId, out string? name) ? name : targetId;

    private static string FormatTimestamp(string? value)
    {
        DateTimeOffset? timestamp = ParseTimestamp(value);
        return timestamp.HasValue ? timestamp.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "-";
    }

    private static DateTimeOffset? ParseTimestamp(string? value)
        => DateTimeOffset.TryParse(value, out DateTimeOffset timestamp) ? timestamp : null;

    private static Panel CreateEmptyPanel(string title, string message)
        => new Panel(new Markup(Markup.Escape(message)))
            .Header($"[bold]{Markup.Escape(title)}[/]")
            .Border(BoxBorder.Rounded)
            .Expand();
}
