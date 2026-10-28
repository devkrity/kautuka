// File: /home/ash/sources/kautuka/src/tui/Kautuka.Tui/Features/Connectors/ConnectorsCommand.cs | Namespace: Kautuka.Tui.Features.Connectors
using System.Diagnostics.CodeAnalysis;

using Kautuka.Tui.Cli;
using Kautuka.Tui.Common;
using Kautuka.Tui.Infrastructure.Gateway;

using Spectre.Console;
using Spectre.Console.Rendering;

namespace Kautuka.Tui.Features.Connectors;

public sealed class ConnectorsCommand
{
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Commands keep a consistent instance-based surface across the TUI application.")]
    public async Task<Result<IReadOnlyList<GatewayConnectorListItem>>> GetConnectorsAsync(TuiCommandOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure<IReadOnlyList<GatewayConnectorListItem>>(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewayConnectorsResponse> connectorsResult = await client.GetConnectorsAsync(cancellationToken).ConfigureAwait(false);
        return !connectorsResult.IsSuccess || connectorsResult.Value is null
            ? Results.Failure<IReadOnlyList<GatewayConnectorListItem>>(connectorsResult.ErrorMessage ?? "Failed to load connectors.")
            : Results.Success<IReadOnlyList<GatewayConnectorListItem>>(connectorsResult.Value.Connectors);
    }

    public static IRenderable BuildConnectorsRenderable(IReadOnlyList<GatewayConnectorListItem> connectors)
    {
        ArgumentNullException.ThrowIfNull(connectors);

        if (connectors.Count == 0)
        {
            return new Panel("[grey]No connectors configured.[/]")
            {
                Border = BoxBorder.Rounded,
                Header = new PanelHeader("Connectors", Justify.Left),
            };
        }

        Table table = StripedTable.CreateRounded();
        table.AddColumn("Id");
        table.AddColumn("Name");
        table.AddColumn("Transport");
        table.AddColumn("Enabled");
        table.AddColumn("Tools");

        foreach (GatewayConnectorListItem connector in connectors.OrderBy(connector => connector.Name, StringComparer.OrdinalIgnoreCase))
        {
            table.AddStripedRow(
                connector.Id,
                connector.Name,
                connector.Transport,
                connector.Enabled ? "yes" : "no",
                connector.ToolCount?.ToString() ?? "-");
        }

        return table;
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Commands keep a consistent instance-based surface across the TUI application.")]
    public async Task<Result> ExecuteAsync(TuiCommandOptions options, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(args);

        if (args.Count == 0 || args[0].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            return await ListAsync(options, cancellationToken).ConfigureAwait(false);
        }

        if (args.Count == 2 && args[0].Equals("show", StringComparison.OrdinalIgnoreCase))
        {
            return await ShowAsync(options, args[1], cancellationToken).ConfigureAwait(false);
        }

        if (args.Count == 2 && args[0].Equals("delete", StringComparison.OrdinalIgnoreCase))
        {
            return await DeleteAsync(options, args[1], cancellationToken).ConfigureAwait(false);
        }

        return Results.Failure("Usage: connectors list | connectors show <connectorId> | connectors delete <connectorId>");
    }

    private async Task<Result> ListAsync(TuiCommandOptions options, CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<GatewayConnectorListItem>> connectorsResult = await GetConnectorsAsync(options, cancellationToken).ConfigureAwait(false);
        if (!connectorsResult.IsSuccess || connectorsResult.Value is null)
        {
            return Results.Failure(connectorsResult.ErrorMessage ?? "Failed to load connectors.");
        }

        AnsiConsole.Write(BuildConnectorsRenderable(connectorsResult.Value));
        return Results.Success();
    }

    private static async Task<Result> ShowAsync(TuiCommandOptions options, string connectorId, CancellationToken cancellationToken)
    {
        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewayConnectorListItem> connectorResult = await client.GetConnectorAsync(connectorId, cancellationToken).ConfigureAwait(false);
        if (!connectorResult.IsSuccess || connectorResult.Value is null)
        {
            return Results.Failure(connectorResult.ErrorMessage ?? "Failed to load connector.");
        }

        GatewayConnectorListItem connector = connectorResult.Value;
        Table table = StripedTable.CreateRounded();
        table.AddColumn("Field");
        table.AddColumn("Value");
        table.AddStripedRow("Id", connector.Id);
        table.AddStripedRow("Name", connector.Name);
        table.AddStripedRow("Transport", connector.Transport);
        table.AddStripedRow("Enabled", connector.Enabled ? "yes" : "no");
        table.AddStripedRow("Scope", connector.Scope);
        table.AddStripedRow("HTTP URL", connector.HttpUrl?.ToString() ?? "-");
        table.AddStripedRow("Container Image", connector.ContainerImage ?? "-");
        table.AddStripedRow("Server Type", connector.ServerType ?? "-");
        table.AddStripedRow("Tool Count", connector.ToolCount?.ToString() ?? "-");
        table.AddStripedRow("Tools", connector.ToolNames.Count == 0 ? "-" : string.Join(", ", connector.ToolNames));
        AnsiConsole.Write(table);
        return Results.Success();
    }

    private static async Task<Result> DeleteAsync(TuiCommandOptions options, string connectorId, CancellationToken cancellationToken)
    {
        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewayDeletedResponse> deleteResult = await client.DeleteConnectorAsync(connectorId, cancellationToken).ConfigureAwait(false);
        if (!deleteResult.IsSuccess)
        {
            return Results.Failure(deleteResult.ErrorMessage ?? "Connector deletion failed.");
        }

        AnsiConsole.MarkupLine($"[green]Deleted connector {Markup.Escape(connectorId)}.[/]");
        return Results.Success();
    }
}
