using System.Diagnostics.CodeAnalysis;

using Kautuka.Tui.Cli;
using Kautuka.Tui.Common;
using Kautuka.Tui.Infrastructure.Gateway;

using Spectre.Console;
using Spectre.Console.Rendering;

namespace Kautuka.Tui.Features.Memories;

public sealed class MemoriesCommand
{
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Shell command helpers intentionally keep an instance-based surface.")]
    public IRenderable BuildMemoriesRenderable(string agentName, GatewayMemoriesResponse memories)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        ArgumentNullException.ThrowIfNull(memories);

        IRenderable factsRenderable = memories.Facts.Count == 0
            ? CreateEmptyPanel("Durable Facts", $"No durable facts stored yet for {agentName}.")
            : BuildFactsTable(memories.Facts);

        IRenderable sessionsRenderable = memories.Sessions.Count == 0
            ? CreateEmptyPanel("Sessions", $"No sessions recorded yet for {agentName}.")
            : BuildSessionsTable(memories.Sessions);

        return new Rows(factsRenderable, sessionsRenderable);
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Commands keep a consistent instance-based surface across the TUI application.")]
    public async Task<Result> ExecuteAsync(TuiCommandOptions options, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(args);

        if (args.Count == 2 && args[0].Equals("show", StringComparison.OrdinalIgnoreCase))
        {
            return await ShowAsync(options, args[1], cancellationToken).ConfigureAwait(false);
        }

        return Results.Failure("Usage: memories show <agentId>");
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Shell command helpers intentionally keep an instance-based surface.")]
    public async Task<Result<(GatewayMemoriesResponse Memories, string AgentName)>> LoadAgentMemoriesAsync(TuiCommandOptions options, string agentId, CancellationToken cancellationToken)
    {
        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure<(GatewayMemoriesResponse, string)>(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewayAgentsResponse> agentsResult = await client.GetAgentsAsync(cancellationToken).ConfigureAwait(false);
        if (!agentsResult.IsSuccess || agentsResult.Value is null)
        {
            return Results.Failure<(GatewayMemoriesResponse, string)>(agentsResult.ErrorMessage ?? "Failed to load agents.");
        }

        GatewayAgentListItem? agent = agentsResult.Value.Agents.FirstOrDefault(item => string.Equals(item.Id, agentId, StringComparison.Ordinal));
        if (agent is null)
        {
            return Results.Failure<(GatewayMemoriesResponse, string)>($"Agent '{agentId}' was not found.");
        }

        Result<GatewayMemoriesResponse> memoriesResult = await client.GetMemoriesForAgentAsync(agentId, cancellationToken).ConfigureAwait(false);
        if (!memoriesResult.IsSuccess || memoriesResult.Value is null)
        {
            return Results.Failure<(GatewayMemoriesResponse, string)>(memoriesResult.ErrorMessage ?? "Failed to load memories.");
        }

        return Results.Success((memoriesResult.Value, agent.Name));
    }

    private async Task<Result> ShowAsync(TuiCommandOptions options, string agentId, CancellationToken cancellationToken)
    {
        Result<(GatewayMemoriesResponse Memories, string AgentName)> memoriesResult = await LoadAgentMemoriesAsync(options, agentId, cancellationToken).ConfigureAwait(false);
        if (!memoriesResult.IsSuccess)
        {
            return Results.Failure(memoriesResult.ErrorMessage ?? "Failed to load memories.");
        }

        AnsiConsole.Write(BuildMemoriesRenderable(memoriesResult.Value.AgentName, memoriesResult.Value.Memories));
        return Results.Success();
    }

    private static Table BuildFactsTable(IReadOnlyList<GatewayMemoryFactPayload> facts)
    {
        Table table = StripedTable.CreateRounded();
        table.Title("Durable Facts");
        table.AddColumn("Title");
        table.AddColumn("Category");
        table.AddColumn("Confidence");
        table.AddColumn("Updated");
        table.AddColumn("Summary");

        foreach (GatewayMemoryFactPayload fact in facts.OrderByDescending(item => ParseTimestamp(item.UpdatedAt)))
        {
            table.AddStripedRow(
                string.IsNullOrWhiteSpace(fact.Title) ? "-" : fact.Title,
                string.IsNullOrWhiteSpace(fact.Category) ? "general" : fact.Category,
                fact.Confidence.ToString("0.##"),
                FormatTimestamp(fact.UpdatedAt),
                Summarize(fact.Content, 80));
        }

        return table;
    }

    private static Table BuildSessionsTable(IReadOnlyList<GatewaySession> sessions)
    {
        Table table = StripedTable.CreateRounded();
        table.Title("Sessions");
        table.AddColumn("Title");
        table.AddColumn("Updated");
        table.AddColumn("Messages");
        table.AddColumn("Compacted");
        table.AddColumn("Summary");

        foreach (GatewaySession session in sessions.OrderByDescending(item => ParseTimestamp(item.UpdatedAt)))
        {
            table.AddStripedRow(
                string.IsNullOrWhiteSpace(session.Title) ? session.Id : session.Title,
                FormatTimestamp(session.UpdatedAt),
                session.Messages.Count.ToString(),
                session.Compacted ? "yes" : "no",
                Summarize(session.CompactSummary, 80));
        }

        return table;
    }

    private static string Summarize(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "-";
        }

        string trimmed = value.Trim().Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        return trimmed.Length <= maxLength ? trimmed : $"{trimmed[..(maxLength - 3)]}...";
    }

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
