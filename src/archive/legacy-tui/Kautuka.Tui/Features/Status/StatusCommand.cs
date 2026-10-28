// File: /home/ash/sources/kautuka/src/tui/Kautuka.Tui/Features/Status/StatusCommand.cs | Namespace: Kautuka.Tui.Features.Status
using Kautuka.Tui.Cli;
using Kautuka.Tui.Common;
using Kautuka.Tui.Features.Runtime;
using Kautuka.Tui.Features.Services;
using Kautuka.Tui.Infrastructure.Gateway;

using Spectre.Console;
using Spectre.Console.Rendering;

namespace Kautuka.Tui.Features.Status;

public sealed class StatusCommand(SystemServiceInspector serviceInspector, RuntimeInspector runtimeInspector)
{
    private readonly SystemServiceInspector serviceInspector = serviceInspector;
    private readonly RuntimeInspector runtimeInspector = runtimeInspector;

    public async Task<Result<IRenderable>> BuildShellDashboardAsync(TuiCommandOptions? options, CancellationToken cancellationToken)
    {
        Result<(IReadOnlyList<ServiceUnitStatus> Services, RuntimeSummary Runtime)> stateResult = await LoadStateAsync(options, cancellationToken).ConfigureAwait(false);
        if (!stateResult.IsSuccess || stateResult.Value.Services is null)
        {
            return Results.Failure<IRenderable>(stateResult.ErrorMessage ?? "Failed to inspect system state.");
        }

        IReadOnlyList<ServiceUnitStatus> services = stateResult.Value.Services;
        RuntimeSummary runtime = stateResult.Value.Runtime;

        Grid summaryGrid = new();
        summaryGrid.AddColumn();
        summaryGrid.AddColumn();
        summaryGrid.AddRow(
            CreateSummaryPanel(services),
            CreatePanel("Runtime", runtime.IsAvailable ? runtime.Summary : $"[yellow]{Markup.Escape(runtime.Summary)}[/]"));

        Table servicesTable = StripedTable.CreateRounded();
        servicesTable.AddColumn("Service");
        servicesTable.AddColumn("State");

        foreach (ServiceUnitStatus service in services.Take(5))
        {
            servicesTable.AddStripedRow(service.Definition.DisplayName, TableCellValue.Markup(FormatState(service.ActiveState, service.SubState)));
        }

        if (!runtime.IsAvailable || runtime.Containers.Count == 0)
        {
            return Results.Success<IRenderable>(new Rows(summaryGrid, new Panel(servicesTable)
            {
                Header = new PanelHeader("Service Snapshot", Justify.Left),
                Border = BoxBorder.Rounded,
            }));
        }

        Table containersTable = StripedTable.CreateRounded();
        containersTable.AddColumn("Container");
        containersTable.AddColumn("Status");

        foreach (ContainerStatus container in runtime.Containers.Take(5))
        {
            containersTable.AddStripedRow(container.Name, container.Status);
        }

        Grid detailGrid = new();
        detailGrid.AddColumn();
        detailGrid.AddColumn();
        detailGrid.AddRow(
            new Panel(servicesTable)
            {
                Header = new PanelHeader("Service Snapshot", Justify.Left),
                Border = BoxBorder.Rounded,
            },
            new Panel(containersTable)
            {
                Header = new PanelHeader("Container Snapshot", Justify.Left),
                Border = BoxBorder.Rounded,
            });

        return Results.Success<IRenderable>(new Rows(summaryGrid, detailGrid));
    }

    public async Task<Result> ExecuteAsync(TuiCommandOptions? options, CancellationToken cancellationToken)
    {
        Result<(IReadOnlyList<ServiceUnitStatus> Services, RuntimeSummary Runtime)> stateResult = await LoadStateAsync(options, cancellationToken).ConfigureAwait(false);
        if (!stateResult.IsSuccess || stateResult.Value.Services is null)
        {
            return Results.Failure(stateResult.ErrorMessage ?? "Failed to inspect system state.");
        }

        IReadOnlyList<ServiceUnitStatus> services = stateResult.Value.Services;
        RuntimeSummary runtime = stateResult.Value.Runtime;

        Grid grid = new();
        grid.AddColumn();
        grid.AddColumn();
        grid.AddRow(CreateSummaryPanel(services), CreatePanel("Runtime", runtime.IsAvailable ? runtime.Summary : $"[yellow]{Markup.Escape(runtime.Summary)}[/]"));
        AnsiConsole.Write(grid);
        AnsiConsole.WriteLine();

        Table servicesTable = StripedTable.CreateRounded();
        servicesTable.AddColumn("Service");
        servicesTable.AddColumn("State");
        servicesTable.AddColumn("Enabled");

        foreach (ServiceUnitStatus service in services)
        {
            servicesTable.AddStripedRow(
                service.Definition.DisplayName,
                TableCellValue.Markup(FormatState(service.ActiveState, service.SubState)),
                service.UnitFileState);
        }

        AnsiConsole.Write(servicesTable);

        if (runtime.IsAvailable && runtime.Containers.Count > 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Containers[/]");

            Table runtimeTable = StripedTable.CreateRounded();
            runtimeTable.AddColumn("Name");
            runtimeTable.AddColumn("Status");

            foreach (ContainerStatus container in runtime.Containers.Take(5))
            {
                runtimeTable.AddStripedRow(container.Name, container.Status);
            }

            AnsiConsole.Write(runtimeTable);
        }

        using GatewayApiClient historyClient = GatewaySessionFactory.CreateClient(options);
        Result<GatewayRuntimeUpdateOperationItem[]> historyResult = await historyClient.GetUpdateHistoryAsync(cancellationToken).ConfigureAwait(false);
        Result<GatewayApplianceHealthItem> healthResult = await historyClient.GetApplianceHealthAsync(cancellationToken).ConfigureAwait(false);

        DateTimeOffset? lastUpdateTime = null;
        if (healthResult.IsSuccess && healthResult.Value?.LastUpdateAppliedAtUtc is string lastApplied
            && DateTimeOffset.TryParse(lastApplied, out DateTimeOffset parsedLastUpdate))
        {
            lastUpdateTime = parsedLastUpdate;
        }

        if (healthResult.IsSuccess && healthResult.Value is { } health)
        {
            AnsiConsole.WriteLine();
            string version = health.InstalledVersion ?? "unknown";
            string sidecarInfo = health.SidecarCount.HasValue ? $"{health.SidecarCount} sidecar(s)" : "sidecars unknown";
            string stagedAt = health.SidecarsStagedAtUtc is not null && DateTimeOffset.TryParse(health.SidecarsStagedAtUtc, out DateTimeOffset stagedTs)
                ? stagedTs.LocalDateTime.ToString("yyyy-MM-dd HH:mm")
                : "never";
            AnsiConsole.MarkupLine($"[bold]Appliance[/]  version=[bold]{Markup.Escape(version)}[/]  {sidecarInfo}  staged={Markup.Escape(stagedAt)}");
        }

        if (lastUpdateTime.HasValue)
        {
            bool anyStale = services.Any(s => s.ExecMainStartTime.HasValue && s.ExecMainStartTime.Value < lastUpdateTime.Value);
            if (anyStale)
            {
                AnsiConsole.MarkupLine("[yellow]⚠  Some services started before the last update and may be running the old version.[/]");
            }
        }

        if (historyResult.IsSuccess && historyResult.Value is { Length: > 0 } operations)
        {
            AnsiConsole.WriteLine();

            Table historyTable = StripedTable.CreateRounded();
            historyTable.AddColumn("Date");
            historyTable.AddColumn("Type");
            historyTable.AddColumn("From");
            historyTable.AddColumn("To");
            historyTable.AddColumn("Result");

            foreach (GatewayRuntimeUpdateOperationItem op in operations.TakeLast(5))
            {
                string date = DateTimeOffset.TryParse(op.CompletedAtUtc, out DateTimeOffset ts)
                    ? ts.LocalDateTime.ToString("yyyy-MM-dd HH:mm")
                    : op.CompletedAtUtc;
                string result = op.Succeeded ? "[green]ok[/]" : "[red]failed[/]";
                historyTable.AddStripedRow(
                    date,
                    op.OperationType,
                    op.FromVersion ?? "-",
                    op.ToVersion,
                    TableCellValue.Markup(result));
            }

            AnsiConsole.MarkupLine("[bold]Recent Operations[/]");
            AnsiConsole.Write(historyTable);
        }

        return Results.Success();
    }

    private async Task<Result<(IReadOnlyList<ServiceUnitStatus> Services, RuntimeSummary Runtime)>> LoadStateAsync(TuiCommandOptions? options, CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<ServiceUnitStatus>> servicesResult = await serviceInspector.GetAllAsync(cancellationToken).ConfigureAwait(false);
        if (!servicesResult.IsSuccess || servicesResult.Value is null)
        {
            return Results.Failure<(IReadOnlyList<ServiceUnitStatus>, RuntimeSummary)>(servicesResult.ErrorMessage ?? "Failed to inspect services.");
        }

        Result<RuntimeSummary> runtimeResult = await runtimeInspector.GetAsync(options, cancellationToken).ConfigureAwait(false);
        if (!runtimeResult.IsSuccess || runtimeResult.Value is null)
        {
            return Results.Failure<(IReadOnlyList<ServiceUnitStatus>, RuntimeSummary)>(runtimeResult.ErrorMessage ?? "Failed to inspect runtime state.");
        }

        return Results.Success((servicesResult.Value, runtimeResult.Value));
    }

    private static Panel CreateSummaryPanel(IReadOnlyList<ServiceUnitStatus> services)
    {
        int activeServices = services.Count(service => service.ActiveState.Equals("active", StringComparison.OrdinalIgnoreCase));
        int failedServices = services.Count(service => service.ActiveState.Equals("failed", StringComparison.OrdinalIgnoreCase));

        return CreatePanel("Services", $"[bold]{activeServices}[/] active\n[bold]{failedServices}[/] failed");
    }

    private static Panel CreatePanel(string header, string body)
        => new Panel(body)
        {
            Header = new PanelHeader(header),
            Border = BoxBorder.Rounded,
        };

    private static string FormatState(string activeState, string subState)
    {
        string label = string.IsNullOrWhiteSpace(subState) ? activeState : $"{activeState} ({subState})";

        return activeState.ToLowerInvariant() switch
        {
            "active" => $"[green]{Markup.Escape(label)}[/]",
            "inactive" => $"[yellow]{Markup.Escape(label)}[/]",
            "failed" => $"[red]{Markup.Escape(label)}[/]",
            _ => $"[grey]{Markup.Escape(label)}[/]",
        };
    }
}
