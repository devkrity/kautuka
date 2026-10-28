using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;

using Kautuka.Tui.Cli;
using Kautuka.Tui.Common;
using Kautuka.Tui.Infrastructure.Gateway;

using Spectre.Console;

namespace Kautuka.Tui.Features.Advanced;

public sealed class AdvancedSettingsCommand
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

        if (args.Count == 1 && args[0].Equals("edit-system", StringComparison.OrdinalIgnoreCase))
        {
            return await EditSystemAsync(options, cancellationToken).ConfigureAwait(false);
        }

        if (args.Count == 1 && args[0].Equals("edit-provider-path", StringComparison.OrdinalIgnoreCase))
        {
            return await EditProviderPathPolicyAsync(options, cancellationToken).ConfigureAwait(false);
        }

        if (args.Count == 1 && args[0].Equals("edit-memory", StringComparison.OrdinalIgnoreCase))
        {
            return await EditMemoryAsync(options, cancellationToken).ConfigureAwait(false);
        }

        if (args.Count == 1 && args[0].Equals("show-persona", StringComparison.OrdinalIgnoreCase))
        {
            return await ShowPersonaDefinitionsAsync(options, cancellationToken).ConfigureAwait(false);
        }

        if (args.Count == 1 && args[0].Equals("edit-persona", StringComparison.OrdinalIgnoreCase))
        {
            return await EditPersonaDefinitionsAsync(options, cancellationToken).ConfigureAwait(false);
        }

        return Results.Failure("Usage: advanced show | advanced edit-system | advanced edit-provider-path | advanced edit-memory | advanced show-persona | advanced edit-persona");
    }

    private static async Task<Result> ShowAsync(TuiCommandOptions options, CancellationToken cancellationToken)
    {
        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewaySystemSettingsResponse> systemResult = await client.GetSystemSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (!systemResult.IsSuccess || systemResult.Value is null)
        {
            return Results.Failure(systemResult.ErrorMessage ?? "Failed to load system settings.");
        }

        Result<GatewayProviderPathPolicySettingsResponse> providerPathResult = await client.GetProviderPathPolicySettingsAsync(cancellationToken).ConfigureAwait(false);
        if (!providerPathResult.IsSuccess || providerPathResult.Value is null)
        {
            return Results.Failure(providerPathResult.ErrorMessage ?? "Failed to load provider path policy settings.");
        }

        Result<GatewayMemorySettingsResponse> memoryResult = await client.GetMemorySettingsAsync(cancellationToken).ConfigureAwait(false);
        if (!memoryResult.IsSuccess || memoryResult.Value is null)
        {
            return Results.Failure(memoryResult.ErrorMessage ?? "Failed to load memory settings.");
        }

        Result<GatewayPersonaDefinitionsResponse> personaResult = await client.GetPersonaDefinitionsAsync(cancellationToken).ConfigureAwait(false);
        if (!personaResult.IsSuccess || personaResult.Value is null)
        {
            return Results.Failure(personaResult.ErrorMessage ?? "Failed to load persona definitions.");
        }

        GatewaySystemSettingsResponse settings = systemResult.Value;

        Table systemTable = StripedTable.CreateRounded();
        systemTable.Title("System");
        systemTable.AddColumn("Field");
        systemTable.AddColumn("Value");
        systemTable.AddStripedRow("Log level", settings.System.LogLevel);
        systemTable.AddStripedRow("Include binary", FormatBoolean(settings.System.Observability.IncludeBinaryContent));
        systemTable.AddStripedRow("Raw message capture", FormatBoolean(settings.System.Observability.CaptureMessageContent));
        systemTable.AddStripedRow("Model timeout", FormatClock(settings.System.RuntimeLimits?.ModelTimeoutSeconds, "300s"));
        systemTable.AddStripedRow("Max run duration", FormatClock(settings.System.RuntimeLimits?.MaxRunDurationSeconds, "unlimited"));
        systemTable.AddStripedRow("HITL expiry", FormatClock(settings.System.RuntimeLimits?.HitlExpirySeconds, "never"));
        systemTable.AddStripedRow("On HITL expiry", settings.System.RuntimeLimits?.HitlExpiryAction ?? "abort");
        systemTable.AddStripedRow("Allow multiple tool calls", FormatBoolean(settings.System.RuntimeLimits?.AllowMultipleToolCalls ?? true));
        systemTable.AddStripedRow("Allow concurrent invocation", FormatBoolean(settings.System.RuntimeLimits?.AllowConcurrentInvocation ?? false));
        systemTable.AddStripedRow("Max parallel tools", FormatParallelTools(settings.System.RuntimeLimits?.MaxParallelTools ?? 0));
        systemTable.AddStripedRow("Workspace base root", settings.Kautuka.Workspace.BaseRoot);
        systemTable.AddStripedRow("Sandbox image", settings.Kautuka.Sandbox.Image ?? "-");
        systemTable.AddStripedRow("Sandbox CPUs", settings.Kautuka.Sandbox.DefaultCpus.ToString());
        systemTable.AddStripedRow("Sandbox memory", settings.Kautuka.Sandbox.DefaultMemory ?? "-");
        systemTable.AddStripedRow("Sandbox timeout", settings.Kautuka.Sandbox.DefaultTimeout.ToString());
        systemTable.AddStripedRow("Document ingestion", FormatBoolean(settings.Kautuka.DocumentIngestion.Enabled));
        systemTable.AddStripedRow("Parser connector", settings.Kautuka.DocumentIngestion.InternalParserConnectorId);
        systemTable.AddStripedRow("Parser tool", settings.Kautuka.DocumentIngestion.InternalParserToolName);
        systemTable.AddStripedRow("Parser timeout", settings.Kautuka.DocumentIngestion.ParserTimeoutSeconds.ToString());
        systemTable.AddStripedRow("Parse extensions", settings.Kautuka.DocumentIngestion.ParseRequiredExtensions);
        systemTable.AddStripedRow("PDF image fallback", FormatBoolean(settings.Kautuka.DocumentIngestion.PdfImageFallbackEnabled));
        systemTable.AddStripedRow("Vision route", FormatBoolean(settings.Kautuka.DocumentIngestion.VisionRouteAllowed));

        Table providerPathTable = StripedTable.CreateRounded();
        providerPathTable.Title("Provider Path Policy");
        providerPathTable.AddColumn("Field");
        providerPathTable.AddColumn("Value");
        providerPathTable.AddStripedRow("Tool native promotion threshold", providerPathResult.Value.ToolNativePromotionThreshold.ToString("0.##"));
        providerPathTable.AddStripedRow("Tool min observations", providerPathResult.Value.ToolMinObservations.ToString());
        providerPathTable.AddStripedRow("Tool native demotion threshold", providerPathResult.Value.ToolNativeDemotionThreshold.ToString("0.##"));
        providerPathTable.AddStripedRow("Structured JSON strict promotion", providerPathResult.Value.StructuredJsonStrictPromotionThreshold.ToString("0.##"));
        providerPathTable.AddStripedRow("Structured JSON min observations", providerPathResult.Value.StructuredJsonMinObservations.ToString());
        providerPathTable.AddStripedRow("Vision enable threshold", providerPathResult.Value.VisionEnableThreshold.ToString("0.##"));
        providerPathTable.AddStripedRow("Observation decay window days", providerPathResult.Value.ObservationDecayWindowDays.ToString());
        providerPathTable.AddStripedRow("Fallback mode", FormatBoolean(providerPathResult.Value.ProviderPathFallbackModeEnabled));

        Table memoryTable = StripedTable.CreateRounded();
        memoryTable.Title("Memory");
        memoryTable.AddColumn("Field");
        memoryTable.AddColumn("Value");
        memoryTable.AddStripedRow("Fact persist threshold", memoryResult.Value.FactPersistThreshold.ToString("0.##"));
        memoryTable.AddStripedRow("Fact inject threshold", memoryResult.Value.FactInjectThreshold.ToString("0.##"));
        memoryTable.AddStripedRow("Max injected facts", memoryResult.Value.MaxInjectedFacts.ToString());
        memoryTable.AddStripedRow("Injection token budget", memoryResult.Value.InjectionTokenBudget.ToString());

        AnsiConsole.Write(systemTable);
        AnsiConsole.WriteLine();
        AnsiConsole.Write(providerPathTable);
        AnsiConsole.WriteLine();
        AnsiConsole.Write(memoryTable);
        AnsiConsole.WriteLine();
        AnsiConsole.Write(CreatePersonaDefinitionsTable(personaResult.Value.Definitions));
        return Results.Success();
    }

    private static async Task<Result> EditSystemAsync(TuiCommandOptions options, CancellationToken cancellationToken)
    {
        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewaySystemSettingsResponse> existingResult = await client.GetSystemSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (!existingResult.IsSuccess || existingResult.Value is null)
        {
            return Results.Failure(existingResult.ErrorMessage ?? "Failed to load system settings.");
        }

        GatewaySystemSettingsResponse existing = existingResult.Value;
        string logLevel = PromptLogLevel(existing.System.LogLevel);
        bool includeBinary = await AnsiConsole.ConfirmAsync("Include binary content in observability?", existing.System.Observability.IncludeBinaryContent, cancellationToken).ConfigureAwait(false);
        bool captureMessageContent = await AnsiConsole.ConfirmAsync("Include raw message payloads in traces and logs?", existing.System.Observability.CaptureMessageContent, cancellationToken).ConfigureAwait(false);
        string sandboxImage = PromptOptionalString("Sandbox image", existing.Kautuka.Sandbox.Image);
        int sandboxCpus = PromptInt("Sandbox default CPUs", existing.Kautuka.Sandbox.DefaultCpus, minValue: 1);
        string sandboxMemory = PromptOptionalString("Sandbox default memory", existing.Kautuka.Sandbox.DefaultMemory);
        int sandboxTimeout = PromptInt("Sandbox default timeout (seconds)", existing.Kautuka.Sandbox.DefaultTimeout, minValue: 1);
        bool ingestionEnabled = await AnsiConsole.ConfirmAsync("Enable document ingestion?", existing.Kautuka.DocumentIngestion.Enabled, cancellationToken).ConfigureAwait(false);
        string parserConnectorId = PromptRequiredString("Internal parser connector id", existing.Kautuka.DocumentIngestion.InternalParserConnectorId);
        string parserToolName = PromptRequiredString("Internal parser tool name", existing.Kautuka.DocumentIngestion.InternalParserToolName);
        int parserTimeout = PromptInt("Parser timeout (seconds)", existing.Kautuka.DocumentIngestion.ParserTimeoutSeconds, minValue: 1);
        string parseRequiredExtensions = PromptRequiredString("Required extensions", existing.Kautuka.DocumentIngestion.ParseRequiredExtensions);
        bool pdfImageFallback = await AnsiConsole.ConfirmAsync("Enable PDF image fallback?", existing.Kautuka.DocumentIngestion.PdfImageFallbackEnabled, cancellationToken).ConfigureAwait(false);
        bool visionRouteAllowed = await AnsiConsole.ConfirmAsync("Allow vision route for ingestion?", existing.Kautuka.DocumentIngestion.VisionRouteAllowed, cancellationToken).ConfigureAwait(false);
        int modelTimeoutSeconds = PromptInt("Model timeout (seconds)", existing.System.RuntimeLimits?.ModelTimeoutSeconds ?? 300, minValue: 1);
        int maxRunDurationSeconds = PromptInt("Max run duration seconds (0 = unlimited)", existing.System.RuntimeLimits?.MaxRunDurationSeconds ?? 0, minValue: 0);
        int hitlExpirySeconds = PromptInt("HITL expiry seconds (0 = never)", existing.System.RuntimeLimits?.HitlExpirySeconds ?? 0, minValue: 0);
        string hitlExpiryAction = PromptHitlExpiryAction(existing.System.RuntimeLimits?.HitlExpiryAction ?? "abort");
        bool allowMultipleToolCalls = await AnsiConsole.ConfirmAsync(
            "Allow multiple tool calls in one model turn?",
            existing.System.RuntimeLimits?.AllowMultipleToolCalls ?? true,
            cancellationToken).ConfigureAwait(false);
        bool allowConcurrentInvocation = existing.System.RuntimeLimits?.AllowConcurrentInvocation ?? false;
        int maxParallelTools = existing.System.RuntimeLimits?.MaxParallelTools ?? 0;
        if (allowMultipleToolCalls)
        {
            allowConcurrentInvocation = await AnsiConsole.ConfirmAsync(
                "Run independent tool calls concurrently?",
                allowConcurrentInvocation,
                cancellationToken).ConfigureAwait(false);
            if (allowConcurrentInvocation)
            {
                maxParallelTools = Math.Min(
                    64,
                    PromptInt(
                        "Max parallel tools (0 = unlimited, 1 = serial, cap 64)",
                        maxParallelTools,
                        minValue: 0));
            }
        }

        GatewaySystemSettingsResponse request = new(
            new GatewaySystemSettingsPayload(
                logLevel,
                new GatewayObservabilitySettingsPayload(includeBinary, captureMessageContent),
                existing.System.Updates,
                existing.System.RuntimeGrounding,
                new GatewayRuntimeLimitsSettingsPayload(
                    modelTimeoutSeconds,
                    maxRunDurationSeconds,
                    hitlExpirySeconds,
                    hitlExpiryAction,
                    allowMultipleToolCalls,
                    allowConcurrentInvocation,
                    maxParallelTools)),
            new GatewayKautukaInfrastructureSettingsPayload(
                existing.Kautuka.Workspace,
                new GatewaySandboxSettingsPayload(
                    string.IsNullOrWhiteSpace(sandboxImage) ? null : sandboxImage.Trim(),
                    sandboxCpus,
                    string.IsNullOrWhiteSpace(sandboxMemory) ? null : sandboxMemory.Trim(),
                    sandboxTimeout),
                new GatewayDocumentIngestionSettingsPayload(
                    ingestionEnabled,
                    parserConnectorId.Trim(),
                    parserToolName.Trim(),
                    parserTimeout,
                    parseRequiredExtensions.Trim(),
                    pdfImageFallback,
                    visionRouteAllowed)));

        Result<GatewaySystemSettingsResponse> saveResult = await client.SaveSystemSettingsAsync(request, cancellationToken).ConfigureAwait(false);
        if (!saveResult.IsSuccess || saveResult.Value is null)
        {
            return Results.Failure(saveResult.ErrorMessage ?? "Failed to save system settings.");
        }

        AnsiConsole.MarkupLine("[green]Advanced system settings saved.[/]");
        return Results.Success();
    }

    private static async Task<Result> EditProviderPathPolicyAsync(TuiCommandOptions options, CancellationToken cancellationToken)
    {
        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewayProviderPathPolicySettingsResponse> existingResult = await client.GetProviderPathPolicySettingsAsync(cancellationToken).ConfigureAwait(false);
        if (!existingResult.IsSuccess || existingResult.Value is null)
        {
            return Results.Failure(existingResult.ErrorMessage ?? "Failed to load provider path policy settings.");
        }

        GatewayProviderPathPolicySettingsResponse existing = existingResult.Value;
        double toolNativePromotionThreshold = PromptDouble("Tool native promotion threshold", existing.ToolNativePromotionThreshold, 0, 1);
        int toolMinObservations = PromptInt("Tool min observations", existing.ToolMinObservations, minValue: 1);
        double toolNativeDemotionThreshold = PromptDouble("Tool native demotion threshold", existing.ToolNativeDemotionThreshold, 0, 1);
        double structuredJsonStrictPromotionThreshold = PromptDouble("Structured JSON strict promotion threshold", existing.StructuredJsonStrictPromotionThreshold, 0, 1);
        int structuredJsonMinObservations = PromptInt("Structured JSON min observations", existing.StructuredJsonMinObservations, minValue: 1);
        double visionEnableThreshold = PromptDouble("Vision enable threshold", existing.VisionEnableThreshold, 0, 1);
        int observationDecayWindowDays = PromptInt("Observation decay window days", existing.ObservationDecayWindowDays, minValue: 1);
        bool providerPathFallbackModeEnabled = await AnsiConsole.ConfirmAsync("Enable provider path fallback mode?", existing.ProviderPathFallbackModeEnabled, cancellationToken).ConfigureAwait(false);

        Result<GatewayProviderPathPolicySettingsResponse> saveResult = await client.SaveProviderPathPolicySettingsAsync(
            new GatewayProviderPathPolicySettingsRequest(
                toolNativePromotionThreshold,
                toolMinObservations,
                toolNativeDemotionThreshold,
                structuredJsonStrictPromotionThreshold,
                structuredJsonMinObservations,
                visionEnableThreshold,
                observationDecayWindowDays,
                providerPathFallbackModeEnabled),
            cancellationToken).ConfigureAwait(false);
        if (!saveResult.IsSuccess || saveResult.Value is null)
        {
            return Results.Failure(saveResult.ErrorMessage ?? "Failed to save provider path policy settings.");
        }

        AnsiConsole.MarkupLine("[green]Provider path policy settings saved.[/]");
        return Results.Success();
    }

    private static async Task<Result> EditMemoryAsync(TuiCommandOptions options, CancellationToken cancellationToken)
    {
        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewayMemorySettingsResponse> existingResult = await client.GetMemorySettingsAsync(cancellationToken).ConfigureAwait(false);
        if (!existingResult.IsSuccess || existingResult.Value is null)
        {
            return Results.Failure(existingResult.ErrorMessage ?? "Failed to load memory settings.");
        }

        GatewayMemorySettingsResponse existing = existingResult.Value;
        double factPersistThreshold = PromptDouble("Fact persist threshold", existing.FactPersistThreshold, 0, 1);
        double factInjectThreshold = PromptDouble("Fact inject threshold", existing.FactInjectThreshold, 0, 1);
        int maxInjectedFacts = PromptInt("Max injected facts", existing.MaxInjectedFacts, minValue: 1);
        int injectionTokenBudget = PromptInt("Injection token budget", existing.InjectionTokenBudget, minValue: 1);

        Result<GatewayMemorySettingsResponse> saveResult = await client.SaveMemorySettingsAsync(
            new GatewayMemorySettingsRequest(
                factPersistThreshold,
                factInjectThreshold,
                maxInjectedFacts,
                injectionTokenBudget),
            cancellationToken).ConfigureAwait(false);
        if (!saveResult.IsSuccess || saveResult.Value is null)
        {
            return Results.Failure(saveResult.ErrorMessage ?? "Failed to save memory settings.");
        }

        AnsiConsole.MarkupLine("[green]Memory settings saved.[/]");
        return Results.Success();
    }

    private static async Task<Result> ShowPersonaDefinitionsAsync(TuiCommandOptions options, CancellationToken cancellationToken)
    {
        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewayPersonaDefinitionsResponse> personaResult = await client.GetPersonaDefinitionsAsync(cancellationToken).ConfigureAwait(false);
        if (!personaResult.IsSuccess || personaResult.Value is null)
        {
            return Results.Failure(personaResult.ErrorMessage ?? "Failed to load persona definitions.");
        }

        AnsiConsole.Write(CreatePersonaDefinitionsTable(personaResult.Value.Definitions));
        return Results.Success();
    }

    private static async Task<Result> EditPersonaDefinitionsAsync(TuiCommandOptions options, CancellationToken cancellationToken)
    {
        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewayPersonaDefinitionsResponse> existingResult = await client.GetPersonaDefinitionsAsync(cancellationToken).ConfigureAwait(false);
        if (!existingResult.IsSuccess || existingResult.Value is null)
        {
            return Results.Failure(existingResult.ErrorMessage ?? "Failed to load persona definitions.");
        }

        JsonObject existing = existingResult.Value.Definitions;
        string companyName = PromptOptionalString("Company name", ReadOptionalString(existing, "company_name"));
        string categories = PromptRequiredString("Categories (comma separated)", JoinValues(ReadStringArray(existing, "categories"), string.Empty));
        string styles = PromptRequiredString("Styles (comma separated)", JoinValues(ReadStringArray(existing, "styles"), string.Empty));
        string tones = PromptRequiredString("Tones (comma separated)", JoinValues(ReadStringArray(existing, "tones"), string.Empty));
        string verbosityLevels = PromptRequiredString("Verbosity levels (comma separated)", JoinValues(ReadStringArray(existing, "verbosity_levels"), string.Empty));

        GatewayPersonaDefinitionsRequest request = new(
            string.IsNullOrWhiteSpace(companyName) ? null : companyName.Trim(),
            ParseCsvList(categories),
            ParseCsvList(styles),
            ParseCsvList(tones),
            ParseCsvList(verbosityLevels));

        Result<GatewayPersonaDefinitionsResponse> saveResult = await client.SavePersonaDefinitionsAsync(request, cancellationToken).ConfigureAwait(false);
        if (!saveResult.IsSuccess || saveResult.Value is null)
        {
            return Results.Failure(saveResult.ErrorMessage ?? "Failed to save persona definitions.");
        }

        AnsiConsole.MarkupLine("[green]Persona definitions saved.[/]");
        return Results.Success();
    }

    private static string FormatBoolean(bool value) => value ? "yes" : "no";

    private static string FormatClock(int? seconds, string zeroLabel)
        => seconds is null or <= 0 ? zeroLabel : $"{seconds.Value}s";

    private static string FormatParallelTools(int maxParallelTools)
        => maxParallelTools <= 0 ? "unlimited" : maxParallelTools.ToString();

    private static string PromptHitlExpiryAction(string currentValue)
    {
        string[] ordered = [.. new[] { currentValue.Trim().ToLowerInvariant(), "abort", "inform", "retry" }.Distinct(StringComparer.OrdinalIgnoreCase)];
        return AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("On HITL expiry")
                .AddChoices(ordered));
    }

    private static Table CreatePersonaDefinitionsTable(JsonObject definitions)
    {
        Table personaTable = StripedTable.CreateRounded();
        personaTable.Title("Persona Definitions");
        personaTable.AddColumn("Field");
        personaTable.AddColumn("Value");
        personaTable.AddStripedRow("Company name", ReadOptionalString(definitions, "company_name") ?? "-");
        personaTable.AddStripedRow("Categories", JoinValues(ReadStringArray(definitions, "categories"), "-"));
        personaTable.AddStripedRow("Styles", JoinValues(ReadStringArray(definitions, "styles"), "-"));
        personaTable.AddStripedRow("Tones", JoinValues(ReadStringArray(definitions, "tones"), "-"));
        personaTable.AddStripedRow("Verbosity levels", JoinValues(ReadStringArray(definitions, "verbosity_levels"), "-"));
        return personaTable;
    }

    private static string PromptLogLevel(string currentValue)
    {
        string[] ordered = [.. new[] { currentValue.ToUpperInvariant(), "TRACE", "DEBUG", "INFO", "WARN", "ERROR", "CRITICAL" }.Distinct(StringComparer.OrdinalIgnoreCase)];
        return AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Log level")
                .AddChoices(ordered));
    }

    private static string PromptRequiredString(string label, string currentValue)
        => AnsiConsole.Prompt(
            new TextPrompt<string>(label)
                .PromptStyle("green")
                .DefaultValue(currentValue)
                .Validate(value => !string.IsNullOrWhiteSpace(value)));

    private static string PromptOptionalString(string label, string? currentValue)
        => AnsiConsole.Prompt(
            new TextPrompt<string>(label)
                .PromptStyle("green")
                .DefaultValue(currentValue ?? string.Empty)
                .AllowEmpty());

    private static IReadOnlyList<string> ParseCsvList(string input)
        => [.. input
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static item => item.Length > 0)];

    private static IReadOnlyList<string> ReadStringArray(JsonObject definitions, string propertyName)
        => definitions[propertyName] is JsonArray array
            ? [.. array.Select(static item => item?.GetValue<string>()?.Trim()).Where(static item => !string.IsNullOrWhiteSpace(item))!]
            : [];

    private static string? ReadOptionalString(JsonObject definitions, string propertyName)
        => definitions[propertyName]?.GetValue<string?>();

    private static string JoinValues(IReadOnlyList<string> values, string emptyFallback)
        => values.Count == 0 ? emptyFallback : string.Join(", ", values);

    private static int PromptInt(string label, int currentValue, int minValue)
        => AnsiConsole.Prompt(
            new TextPrompt<int>(label)
                .PromptStyle("green")
                .DefaultValue(currentValue)
                .Validate(value => value >= minValue));

    private static double PromptDouble(string label, double currentValue, double minValue, double maxValue)
        => AnsiConsole.Prompt(
            new TextPrompt<double>(label)
                .PromptStyle("green")
                .DefaultValue(currentValue)
                .Validate(value => value >= minValue && value <= maxValue));
}
