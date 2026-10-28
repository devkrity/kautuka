// File: /home/ash/sources/kautuka/src/tui/Kautuka.Tui/Features/Models/ModelsCommand.cs | Namespace: Kautuka.Tui.Features.Models
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;

using Kautuka.Tui.Cli;
using Kautuka.Tui.Common;
using Kautuka.Tui.Infrastructure.Gateway;

using Spectre.Console;
using Spectre.Console.Rendering;

namespace Kautuka.Tui.Features.Models;

public sealed class ModelsCommand
{
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Commands keep a consistent instance-based surface across the TUI application.")]
    public async Task<Result<IReadOnlyList<GatewayModelListItem>>> GetModelsAsync(TuiCommandOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure<IReadOnlyList<GatewayModelListItem>>(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewayModelsResponse> modelsResult = await client.GetModelsAsync(cancellationToken).ConfigureAwait(false);
        return !modelsResult.IsSuccess || modelsResult.Value is null
            ? Results.Failure<IReadOnlyList<GatewayModelListItem>>(modelsResult.ErrorMessage ?? "Failed to load models.")
            : Results.Success<IReadOnlyList<GatewayModelListItem>>(modelsResult.Value.Models);
    }

    public static IRenderable BuildModelsRenderable(IReadOnlyList<GatewayModelListItem> models)
    {
        ArgumentNullException.ThrowIfNull(models);

        if (models.Count == 0)
        {
            return new Panel("[grey]No models configured.[/]")
            {
                Border = BoxBorder.Rounded,
                Header = new PanelHeader("Models", Justify.Left),
            };
        }

        Table table = StripedTable.CreateRounded();
        table.AddColumn("Id");
        table.AddColumn("Provider");
        table.AddColumn("Model");
        table.AddColumn("Capabilities");

        foreach (GatewayModelListItem model in models.OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase))
        {
            table.AddStripedRow(model.Id, model.ProviderId, model.ModelName, model.Capabilities is null ? "-" : "present");
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

        if (args.Count == 2 && args[0].Equals("discover", StringComparison.OrdinalIgnoreCase))
        {
            return await DiscoverAsync(options, args[1], cancellationToken).ConfigureAwait(false);
        }

        if (args.Count == 1 && args[0].Equals("create", StringComparison.OrdinalIgnoreCase))
        {
            return await SaveInteractiveAsync(options, null, cancellationToken).ConfigureAwait(false);
        }

        if (args.Count == 2 && args[0].Equals("edit", StringComparison.OrdinalIgnoreCase))
        {
            return await EditInteractiveAsync(options, args[1], cancellationToken).ConfigureAwait(false);
        }

        if (args.Count == 2 && args[0].Equals("probe", StringComparison.OrdinalIgnoreCase))
        {
            return await ProbeAsync(options, args[1], cancellationToken).ConfigureAwait(false);
        }

        if (args.Count == 2 && args[0].Equals("delete", StringComparison.OrdinalIgnoreCase))
        {
            return await DeleteAsync(options, args[1], cancellationToken).ConfigureAwait(false);
        }

        return Results.Failure("Usage: models list | models create | models edit <modelId> | models discover <providerId> | models probe <modelId> | models delete <modelId>");
    }

    private async Task<Result> ListAsync(TuiCommandOptions options, CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<GatewayModelListItem>> modelsResult = await GetModelsAsync(options, cancellationToken).ConfigureAwait(false);
        if (!modelsResult.IsSuccess || modelsResult.Value is null)
        {
            return Results.Failure(modelsResult.ErrorMessage ?? "Failed to load models.");
        }

        AnsiConsole.Write(BuildModelsRenderable(modelsResult.Value));
        return Results.Success();
    }

    private static async Task<Result> DiscoverAsync(TuiCommandOptions options, string providerId, CancellationToken cancellationToken)
    {
        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewayDiscoveredModelsResponse> result = await client.DiscoverModelsAsync(providerId, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || result.Value is null)
        {
            return Results.Failure(result.ErrorMessage ?? "Failed to discover models.");
        }

        if (result.Value.Models.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No models were discovered for that provider.[/]");
            return Results.Success();
        }

        Table table = StripedTable.CreateRounded();
        table.AddColumn("Id");
        table.AddColumn("Display Name");
        table.AddColumn("Description");

        foreach (GatewayDiscoveredModelItem model in result.Value.Models)
        {
            table.AddStripedRow(model.Id, model.DisplayName, model.Description ?? "-");
        }

        AnsiConsole.Write(table);
        return Results.Success();
    }

    private static async Task<Result> ProbeAsync(TuiCommandOptions options, string modelId, CancellationToken cancellationToken)
    {
        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewayModelCapabilitiesResponse> result = await client.ProbeModelAsync(modelId, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || result.Value is null)
        {
            return Results.Failure(result.ErrorMessage ?? "Failed to probe model.");
        }

        GatewayModelCapabilities capabilities = result.Value.Capabilities;
        Table table = StripedTable.CreateRounded();
        table.AddColumn("Capability");
        table.AddColumn("Value");
        table.AddStripedRow("Tool Calling", FormatCapability(capabilities.ToolCalling));
        table.AddStripedRow("Streaming", FormatCapability(capabilities.Streaming));
        table.AddStripedRow("Reasoning Only", FormatCapability(capabilities.ReasoningOnlyOutput));
        table.AddStripedRow("Structured JSON", FormatCapability(capabilities.StructuredJson));
        table.AddStripedRow("Probed At", capabilities.ProbedAt);
        AnsiConsole.Write(table);
        return Results.Success();
    }

    private static async Task<Result> DeleteAsync(TuiCommandOptions options, string modelId, CancellationToken cancellationToken)
    {
        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewayBooleanSuccessResponse> deleteResult = await client.DeleteModelAsync(modelId, cancellationToken).ConfigureAwait(false);
        if (!deleteResult.IsSuccess)
        {
            return Results.Failure(deleteResult.ErrorMessage ?? "Model deletion failed.");
        }

        AnsiConsole.MarkupLine($"[green]Deleted model {Markup.Escape(modelId)}.[/]");
        return Results.Success();
    }

    private static string FormatCapability(bool? value)
        => value switch
        {
            true => "yes",
            false => "no",
            null => "unknown",
        };

    private static async Task<Result> EditInteractiveAsync(TuiCommandOptions options, string modelId, CancellationToken cancellationToken)
    {
        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewayModelsResponse> modelsResult = await client.GetModelsAsync(cancellationToken).ConfigureAwait(false);
        if (!modelsResult.IsSuccess || modelsResult.Value is null)
        {
            return Results.Failure(modelsResult.ErrorMessage ?? "Failed to load models.");
        }

        GatewayModelListItem? existing = modelsResult.Value.Models.FirstOrDefault(model => string.Equals(model.Id, modelId, StringComparison.Ordinal));
        return existing is null
            ? Results.Failure($"Model '{modelId}' was not found.")
            : await SaveInteractiveAsync(options, existing, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Result> SaveInteractiveAsync(TuiCommandOptions options, GatewayModelListItem? existing, CancellationToken cancellationToken)
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

        if (providersResult.Value.Providers.Count == 0)
        {
            return Results.Failure("Create a provider before saving a model.");
        }

        string id = PromptRequiredString("Model id", existing?.Id);
        GatewayProviderListItem provider = PromptProvider(providersResult.Value.Providers, existing?.ProviderId);

        Result<GatewayDiscoveredModelsResponse> discoveredResult = await client.DiscoverModelsAsync(provider.Id, cancellationToken).ConfigureAwait(false);
        if (discoveredResult.IsSuccess && discoveredResult.Value is not null && discoveredResult.Value.Models.Count > 0)
        {
            Table table = StripedTable.CreateRounded();
            table.AddColumn("Discovered model id");
            table.AddColumn("Name");
            table.AddColumn("Description");

            foreach (GatewayDiscoveredModelItem model in discoveredResult.Value.Models)
            {
                table.AddStripedRow(model.Id, model.DisplayName, model.Description ?? "-");
            }

            AnsiConsole.Write(table);
        }

        string modelName = PromptRequiredString("Provider model name", existing?.ModelName);
        string customOutputText = provider.Type.Equals("test", StringComparison.OrdinalIgnoreCase)
            ? PromptOptionalString("Custom output text (optional)", ReadString(existing?.Settings, "custom_output_text"))
            : string.Empty;

        JsonObject settings = new();
        if (!string.IsNullOrWhiteSpace(customOutputText))
        {
            settings["custom_output_text"] = customOutputText.Trim();
        }

        Result<GatewayModelResponse> saveResult = await client.SaveModelAsync(
            new GatewayModelSaveRequest(id.Trim(), provider.Id, modelName.Trim(), settings),
            cancellationToken).ConfigureAwait(false);
        if (!saveResult.IsSuccess || saveResult.Value is null)
        {
            return Results.Failure(saveResult.ErrorMessage ?? "Failed to save model.");
        }

        AnsiConsole.MarkupLine($"[green]Saved model {Markup.Escape(saveResult.Value.Model.Id)} for provider {Markup.Escape(saveResult.Value.Model.ProviderId)}.[/]");
        return Results.Success();
    }

    private static GatewayProviderListItem PromptProvider(IReadOnlyList<GatewayProviderListItem> providers, string? currentProviderId)
    {
        List<GatewayProviderListItem> ordered = [.. providers
            .OrderByDescending(provider => string.Equals(provider.Id, currentProviderId, StringComparison.OrdinalIgnoreCase))
            .ThenBy(provider => provider.Id, StringComparer.OrdinalIgnoreCase)];

        return AnsiConsole.Prompt(
            new SelectionPrompt<GatewayProviderListItem>()
                .Title("Provider")
                .UseConverter(provider => $"{provider.Id} ({provider.Type})")
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
