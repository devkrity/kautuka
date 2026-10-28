using System.Diagnostics.CodeAnalysis;

using Kautuka.Tui.Cli;
using Kautuka.Tui.Common;
using Kautuka.Tui.Infrastructure.Gateway;

using Spectre.Console;
using Spectre.Console.Rendering;

namespace Kautuka.Tui.Features.Auth;

public sealed class WhoAmICommand
{
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Commands keep a consistent instance-based surface across the TUI application.")]
    public async Task<Result<GatewayUserPayload>> GetCurrentUserAsync(TuiCommandOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure<GatewayUserPayload>(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewayUserPayload> meResult = await client.GetMeAsync(cancellationToken).ConfigureAwait(false);
        return meResult.IsSuccess && meResult.Value is not null
            ? meResult
            : Results.Failure<GatewayUserPayload>(meResult.ErrorMessage ?? "Failed to load current user.");
    }

    public static IRenderable BuildUserRenderable(GatewayUserPayload user)
    {
        ArgumentNullException.ThrowIfNull(user);

        Table table = StripedTable.CreateRounded();
        table.AddColumn("Field");
        table.AddColumn("Value");
        table.AddStripedRow("Id", user.Id);
        table.AddStripedRow("Username", user.Username);
        table.AddStripedRow("Role", user.Role);
        table.AddStripedRow("Active", user.IsActive ? "yes" : "no");
        table.AddStripedRow("Email", user.Email ?? "-");
        table.AddStripedRow("Display Name", user.DisplayName ?? "-");
        table.AddStripedRow("Can Import Connectors", user.CanImportConnectors ? "yes" : "no");
        table.AddStripedRow("Can Import Skills", user.CanImportSkills ? "yes" : "no");
        return table;
    }

    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Commands keep a consistent instance-based surface across the TUI application.")]
    public async Task<Result> ExecuteAsync(TuiCommandOptions options, CancellationToken cancellationToken)
    {
        Result<GatewayUserPayload> meResult = await GetCurrentUserAsync(options, cancellationToken).ConfigureAwait(false);
        if (!meResult.IsSuccess || meResult.Value is null)
        {
            return Results.Failure(meResult.ErrorMessage ?? "Failed to load current user.");
        }

        AnsiConsole.Write(BuildUserRenderable(meResult.Value));
        return Results.Success();
    }
}
