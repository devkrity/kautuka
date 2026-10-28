using System.Diagnostics.CodeAnalysis;

using Kautuka.Tui.Cli;
using Kautuka.Tui.Common;
using Kautuka.Tui.Infrastructure.Gateway;

using Spectre.Console;

namespace Kautuka.Tui.Features.Organization;

public sealed class OrganizationCommand
{
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Commands keep a consistent instance-based surface across the TUI application.")]
    public async Task<Result> ExecuteAsync(TuiCommandOptions options, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(args);

        if (args.Count == 0 || args[0].Equals("show", StringComparison.OrdinalIgnoreCase))
        {
            return await ShowAsync(options, cancellationToken).ConfigureAwait(false);
        }

        if (args.Count == 1 && args[0].Equals("edit", StringComparison.OrdinalIgnoreCase))
        {
            return await EditAsync(options, cancellationToken).ConfigureAwait(false);
        }

        return Results.Failure("Usage: organization show | organization edit");
    }

    private static async Task<Result> ShowAsync(TuiCommandOptions options, CancellationToken cancellationToken)
    {
        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewayOrganizationSettingsResponse> response = await client.GetOrganizationAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess || response.Value is null)
        {
            return Results.Failure(response.ErrorMessage ?? "Failed to load organization settings.");
        }

        Table table = StripedTable.CreateRounded();
        table.AddColumn("Field");
        table.AddColumn("Value");
        table.AddStripedRow("Name", response.Value.Organization.Name ?? "-");
        table.AddStripedRow("Industry", response.Value.Organization.Industry ?? "-");
        AnsiConsole.Write(table);
        return Results.Success();
    }

    private static async Task<Result> EditAsync(TuiCommandOptions options, CancellationToken cancellationToken)
    {
        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewayOrganizationSettingsResponse> existingResult = await client.GetOrganizationAsync(cancellationToken).ConfigureAwait(false);
        if (!existingResult.IsSuccess || existingResult.Value is null)
        {
            return Results.Failure(existingResult.ErrorMessage ?? "Failed to load organization settings.");
        }

        GatewayOrganizationSettingsPayload existing = existingResult.Value.Organization;
        string name = PromptOptionalString("Organization name", existing.Name);
        string industry = PromptOptionalString("Industry", existing.Industry);

        Result<GatewayOrganizationSettingsResponse> saveResult = await client.SaveOrganizationAsync(
            new GatewayOrganizationSettingsPayload(
                string.IsNullOrWhiteSpace(name) ? null : name.Trim(),
                string.IsNullOrWhiteSpace(industry) ? null : industry.Trim()),
            cancellationToken).ConfigureAwait(false);
        if (!saveResult.IsSuccess || saveResult.Value is null)
        {
            return Results.Failure(saveResult.ErrorMessage ?? "Failed to save organization settings.");
        }

        AnsiConsole.MarkupLine("[green]Organization settings saved.[/]");
        return Results.Success();
    }

    private static string PromptOptionalString(string label, string? currentValue)
        => AnsiConsole.Prompt(
            new TextPrompt<string>(label)
                .PromptStyle("green")
                .DefaultValue(currentValue ?? string.Empty)
                .AllowEmpty());
}
