// File: /home/ash/sources/kautuka/src/tui/Kautuka.Tui/Features/Services/ServicesCommand.cs | Namespace: Kautuka.Tui.Features.Services
using Kautuka.Tui.Common;

using Spectre.Console;
using Spectre.Console.Rendering;

namespace Kautuka.Tui.Features.Services;

public sealed class ServicesCommand(SystemServiceInspector inspector)
{
    private readonly SystemServiceInspector inspector = inspector;

    public Task<Result<IReadOnlyList<ServiceUnitStatus>>> GetAllAsync(CancellationToken cancellationToken)
        => inspector.GetAllAsync(cancellationToken);

    public static IRenderable BuildServicesRenderable(IReadOnlyList<ServiceUnitStatus> services)
    {
        ArgumentNullException.ThrowIfNull(services);

        Table table = StripedTable.CreateRounded();
        table.AddColumn("Service");
        table.AddColumn("Active");
        table.AddColumn("Enabled");
        table.AddColumn("Notes");

        foreach (ServiceUnitStatus service in services)
        {
            table.AddStripedRow(
                service.Definition.DisplayName,
                TableCellValue.Markup(FormatState(service.ActiveState, service.SubState)),
                service.UnitFileState,
                string.IsNullOrWhiteSpace(service.Detail) ? "-" : service.Detail);
        }

        return table;
    }

    public async Task<Result> ExecuteAsync(CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<ServiceUnitStatus>> servicesResult = await inspector.GetAllAsync(cancellationToken).ConfigureAwait(false);
        if (!servicesResult.IsSuccess || servicesResult.Value is null)
        {
            return Results.Failure(servicesResult.ErrorMessage ?? "Failed to inspect services.");
        }

        AnsiConsole.Write(BuildServicesRenderable(servicesResult.Value));
        return Results.Success();
    }

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
