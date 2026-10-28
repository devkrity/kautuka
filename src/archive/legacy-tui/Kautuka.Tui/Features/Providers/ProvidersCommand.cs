// File: /home/ash/sources/kautuka/src/tui/Kautuka.Tui/Features/Providers/ProvidersCommand.cs | Namespace: Kautuka.Tui.Features.Providers
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;

using Kautuka.Tui.Cli;
using Kautuka.Tui.Common;
using Kautuka.Tui.Infrastructure.Gateway;

using Spectre.Console;
using Spectre.Console.Rendering;

namespace Kautuka.Tui.Features.Providers;

public sealed class ProvidersCommand
{
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Commands keep a consistent instance-based surface across the TUI application.")]
    public async Task<Result<IReadOnlyList<GatewayProviderListItem>>> GetProvidersAsync(TuiCommandOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure<IReadOnlyList<GatewayProviderListItem>>(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewayProvidersResponse> providersResult = await client.GetProvidersAsync(cancellationToken).ConfigureAwait(false);
        return !providersResult.IsSuccess || providersResult.Value is null
            ? Results.Failure<IReadOnlyList<GatewayProviderListItem>>(providersResult.ErrorMessage ?? "Failed to load providers.")
            : Results.Success<IReadOnlyList<GatewayProviderListItem>>(providersResult.Value.Providers);
    }

    public static IRenderable BuildProvidersRenderable(IReadOnlyList<GatewayProviderListItem> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        if (providers.Count == 0)
        {
            return new Panel("[grey]No providers configured.[/]")
            {
                Border = BoxBorder.Rounded,
                Header = new PanelHeader("Providers", Justify.Left),
            };
        }

        Table table = StripedTable.CreateRounded();
        table.AddColumn("Id");
        table.AddColumn("Type");
        table.AddColumn("API Key");
        table.AddColumn("Created");

        foreach (GatewayProviderListItem provider in providers.OrderBy(provider => provider.Id, StringComparer.OrdinalIgnoreCase))
        {
            table.AddStripedRow(provider.Id, provider.Type, provider.HasApiKey ? "set" : "missing", provider.CreatedAt);
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

        if (args.Count == 1 && args[0].Equals("types", StringComparison.OrdinalIgnoreCase))
        {
            return await ListTypesAsync(options, cancellationToken).ConfigureAwait(false);
        }

        if (args.Count == 1 && args[0].Equals("create", StringComparison.OrdinalIgnoreCase))
        {
            return await SaveInteractiveAsync(options, null, cancellationToken).ConfigureAwait(false);
        }

        if (args.Count == 2 && args[0].Equals("edit", StringComparison.OrdinalIgnoreCase))
        {
            return await EditInteractiveAsync(options, args[1], cancellationToken).ConfigureAwait(false);
        }

        if (args.Count == 2 && args[0].Equals("delete", StringComparison.OrdinalIgnoreCase))
        {
            return await DeleteAsync(options, args[1], cancellationToken).ConfigureAwait(false);
        }

        return Results.Failure("Usage: providers list | providers types | providers create | providers edit <providerId> | providers delete <providerId>");
    }

    private static async Task<Result> ListTypesAsync(TuiCommandOptions options, CancellationToken cancellationToken)
    {
        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewayProviderTypesResponse> result = await client.GetProviderTypesAsync(cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || result.Value is null)
        {
            return Results.Failure(result.ErrorMessage ?? "Failed to load provider types.");
        }

        Table table = StripedTable.CreateRounded();
        table.AddColumn("Type");
        table.AddColumn("Name");
        table.AddColumn("API Key");
        table.AddColumn("Base URL");

        foreach (GatewayProviderTypeItem providerType in result.Value.ProviderTypes)
        {
            table.AddStripedRow(
                providerType.Type,
                providerType.FriendlyName,
                providerType.RequiresApiKey ? "required" : "optional",
                providerType.RequiresBaseUrl ? "required" : "optional");
        }

        AnsiConsole.Write(table);
        return Results.Success();
    }

    private async Task<Result> ListAsync(TuiCommandOptions options, CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<GatewayProviderListItem>> providersResult = await GetProvidersAsync(options, cancellationToken).ConfigureAwait(false);
        if (!providersResult.IsSuccess || providersResult.Value is null)
        {
            return Results.Failure(providersResult.ErrorMessage ?? "Failed to load providers.");
        }

        AnsiConsole.Write(BuildProvidersRenderable(providersResult.Value));
        return Results.Success();
    }

    private static async Task<Result> DeleteAsync(TuiCommandOptions options, string providerId, CancellationToken cancellationToken)
    {
        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewayBooleanSuccessResponse> deleteResult = await client.DeleteProviderAsync(providerId, cancellationToken).ConfigureAwait(false);
        if (!deleteResult.IsSuccess)
        {
            return Results.Failure(deleteResult.ErrorMessage ?? "Provider deletion failed.");
        }

        AnsiConsole.MarkupLine($"[green]Deleted provider {Markup.Escape(providerId)}.[/]");
        return Results.Success();
    }

    private static async Task<Result> EditInteractiveAsync(TuiCommandOptions options, string providerId, CancellationToken cancellationToken)
    {
        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewayProvidersResponse> providersResult = await client.GetProvidersAsync(cancellationToken).ConfigureAwait(false);
        if (!providersResult.IsSuccess || providersResult.Value is null)
        {
            return Results.Failure(providersResult.ErrorMessage ?? "Failed to load providers.");
        }

        GatewayProviderListItem? existing = providersResult.Value.Providers.FirstOrDefault(provider => string.Equals(provider.Id, providerId, StringComparison.Ordinal));
        return existing is null
            ? Results.Failure($"Provider '{providerId}' was not found.")
            : await SaveInteractiveAsync(options, existing, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Result> SaveInteractiveAsync(TuiCommandOptions options, GatewayProviderListItem? existing, CancellationToken cancellationToken)
    {
        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewayProviderTypesResponse> typesResult = await client.GetProviderTypesAsync(cancellationToken).ConfigureAwait(false);
        if (!typesResult.IsSuccess || typesResult.Value is null)
        {
            return Results.Failure(typesResult.ErrorMessage ?? "Failed to load provider types.");
        }

        string id = PromptRequiredString("Provider id", existing?.Id);
        GatewayProviderTypeItem selectedType = PromptProviderType(typesResult.Value.ProviderTypes, existing?.Type);
        string baseUrl = PromptOptionalString("Base URL", ReadString(existing?.Settings, "base_url"));
        string apiKey = AnsiConsole.Prompt(
            new TextPrompt<string>(existing is null
                    ? "API key (optional)"
                    : "API key (optional, leave blank to keep existing or omit)")
                .PromptStyle("green")
                .AllowEmpty()
                .Secret());

        JsonObject settings = new()
        {
            ["base_url"] = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl.Trim(),
            ["api_key"] = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim(),
        };

        Result<GatewayProviderResponse> saveResult = await client.SaveProviderAsync(
            new GatewayProviderSaveRequest(id.Trim(), selectedType.Type, settings),
            cancellationToken).ConfigureAwait(false);
        if (!saveResult.IsSuccess || saveResult.Value is null)
        {
            return Results.Failure(saveResult.ErrorMessage ?? "Failed to save provider.");
        }

        AnsiConsole.MarkupLine($"[green]Saved provider {Markup.Escape(saveResult.Value.Provider.Id)} ({Markup.Escape(saveResult.Value.Provider.Type)}).[/]");
        return Results.Success();
    }

    private static GatewayProviderTypeItem PromptProviderType(IReadOnlyList<GatewayProviderTypeItem> providerTypes, string? currentType)
    {
        if (providerTypes.Count == 0)
        {
            throw new InvalidOperationException("No provider types are available.");
        }

        List<GatewayProviderTypeItem> ordered = [.. providerTypes
            .OrderByDescending(type => string.Equals(type.Type, currentType, StringComparison.OrdinalIgnoreCase))
            .ThenBy(type => type.FriendlyName, StringComparer.OrdinalIgnoreCase)];

        return AnsiConsole.Prompt(
            new SelectionPrompt<GatewayProviderTypeItem>()
                .Title("Provider type")
                .UseConverter(type => $"{type.FriendlyName} ({type.Type})")
                .AddChoices(ordered));
    }

    private static string PromptRequiredString(string label, string? currentValue)
        => AnsiConsole.Prompt(
            new TextPrompt<string>(label)
                .PromptStyle("green")
                .DefaultValue(currentValue ?? string.Empty)
                .Validate(value => !string.IsNullOrWhiteSpace(value)));

    private static string PromptOptionalString(string label, string? currentValue)
        => AnsiConsole.Prompt(
            new TextPrompt<string>(label)
                .PromptStyle("green")
                .DefaultValue(currentValue ?? string.Empty)
                .AllowEmpty());

    private static string? ReadString(JsonObject? settings, string propertyName)
        => settings?[propertyName]?.GetValue<string?>();
}
