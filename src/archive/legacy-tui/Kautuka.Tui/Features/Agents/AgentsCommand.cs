using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;

using Kautuka.Tui.Cli;
using Kautuka.Tui.Common;
using Kautuka.Tui.Infrastructure.Gateway;

using Spectre.Console;
using Spectre.Console.Rendering;

namespace Kautuka.Tui.Features.Agents;

public sealed class AgentsCommand
{
    public static IRenderable BuildAgentListRenderable(IReadOnlyList<GatewayAgentListItem> agents)
    {
        ArgumentNullException.ThrowIfNull(agents);

        Table table = StripedTable.CreateRounded();
        table.AddColumn("Id");
        table.AddColumn("Name");
        table.AddColumn("Scope");
        table.AddColumn("Model");
        table.AddColumn("State");
        table.AddColumn("Connectors");

        foreach (GatewayAgentListItem agent in agents.OrderBy(agent => agent.Name, StringComparer.OrdinalIgnoreCase))
        {
            table.AddStripedRow(
                agent.Id,
                agent.Name,
                agent.Scope,
                agent.ModelId,
                agent.Enabled ? "enabled" : "disabled",
                FormatConnectorBindings(agent.McpBindings));
        }

        return table;
    }

    public static IRenderable BuildAgentDetailsRenderable(GatewayAgentListItem agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        Table details = StripedTable.CreateRounded();
        details.AddColumn("Field");
        details.AddColumn("Value");
        details.AddStripedRow("Id", agent.Id);
        details.AddStripedRow("Name", agent.Name);
        details.AddStripedRow("Scope", agent.Scope);
        details.AddStripedRow("Owner", agent.OwnerUserId);
        details.AddStripedRow("Model", agent.ModelId);
        details.AddStripedRow("Enabled", agent.Enabled ? "yes" : "no");
        details.AddStripedRow("Super Exec", agent.SuperExecMode);
        details.AddStripedRow("Created", agent.CreatedAt);
        details.AddStripedRow("Connectors", FormatConnectorBindings(agent.McpBindings));
        details.AddStripedRow("Skills", agent.SkillAttachments.Count == 0 ? "-" : string.Join(", ", agent.SkillAttachments.Select(static item => $"{item.SkillId}:{item.Mode}")));
        details.AddStripedRow("Tool Exposure", agent.ToolExposure.ExposedAsTool ? "yes" : "no");
        details.AddStripedRow("Max run duration", FormatRuntimeClock(agent.Runtime?.MaxRunDurationSeconds, "inherit"));
        details.AddStripedRow("HITL expiry", FormatRuntimeClock(agent.Runtime?.HitlExpirySeconds, "inherit"));
        details.AddStripedRow("On HITL expiry", string.IsNullOrWhiteSpace(agent.Runtime?.HitlExpiryAction) ? "inherit" : agent.Runtime.HitlExpiryAction);
        details.AddStripedRow("Approval Timeout", $"{agent.ApprovalConfig.TimeoutSeconds}s");
        details.AddStripedRow("Agent Tool Targets", agent.ApprovalConfig.AgentToolIds.Count == 0 ? "-" : string.Join(", ", agent.ApprovalConfig.AgentToolIds));
        details.AddStripedRow("Agent Tool Targets Requiring Approval", agent.ApprovalConfig.ApprovalRequiredAgentToolIds.Count == 0 ? "-" : string.Join(", ", agent.ApprovalConfig.ApprovalRequiredAgentToolIds));
        details.AddStripedRow("System Prompt", string.IsNullOrWhiteSpace(agent.SystemPrompt) ? "-" : agent.SystemPrompt);

        Table sessionTable = StripedTable.CreateRounded();
        sessionTable.AddColumn("Session");
        sessionTable.AddColumn("Value");
        sessionTable.AddStripedRow("History", agent.Session.HistoryEnabled ? "enabled" : "disabled");
        sessionTable.AddStripedRow("Max History Runs", agent.Session.MaxHistoryRuns.ToString());
        sessionTable.AddStripedRow("Summaries", agent.Session.SummariesEnabled ? "enabled" : "disabled");
        sessionTable.AddStripedRow("Summary Model", agent.Session.SummaryModelId ?? "-");
        sessionTable.AddStripedRow("Fact Extraction", agent.Session.MemoryFactExtractionEnabled ? "enabled" : "disabled");

        Table sandboxTable = StripedTable.CreateRounded();
        sandboxTable.AddColumn("Sandbox");
        sandboxTable.AddColumn("Value");
        sandboxTable.AddStripedRow("Enabled", agent.Sandbox.Enabled ? "yes" : "no");
        sandboxTable.AddStripedRow("Allowed Languages", agent.Sandbox.AllowedLanguages.Count == 0 ? "-" : string.Join(", ", agent.Sandbox.AllowedLanguages));
        sandboxTable.AddStripedRow("Max Memory", agent.Sandbox.MaxMemory);
        sandboxTable.AddStripedRow("Max Timeout", $"{agent.Sandbox.MaxTimeout}s");

        Table personaTable = StripedTable.CreateRounded();
        personaTable.AddColumn("Persona");
        personaTable.AddColumn("Value");
        personaTable.AddStripedRow("Category", ReadPersonaString(agent.Persona, "category") ?? "-");
        personaTable.AddStripedRow("Style", ReadPersonaString(agent.Persona, "style") ?? "-");
        personaTable.AddStripedRow("Tone", ReadPersonaString(agent.Persona, "tone") ?? "-");
        personaTable.AddStripedRow("Verbosity", ReadPersonaString(agent.Persona, "verbosity") ?? "-");
        personaTable.AddStripedRow("Company", ReadNestedPersonaString(agent.Persona, "office_worker", "company_name") ?? "-");
        personaTable.AddStripedRow("Title", ReadNestedPersonaString(agent.Persona, "office_worker", "title") ?? "-");
        personaTable.AddStripedRow("Department", ReadNestedPersonaString(agent.Persona, "office_worker", "department") ?? "-");
        personaTable.AddStripedRow("Industry", ReadNestedPersonaString(agent.Persona, "office_worker", "industry") ?? "-");

        return new Rows(details, sessionTable, sandboxTable, personaTable);
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

        return Results.Failure("Usage: agents list | agents show <agentId> | agents create | agents edit <agentId> | agents delete <agentId>");
    }

    private static async Task<Result> ListAsync(TuiCommandOptions options, CancellationToken cancellationToken)
    {
        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewayAgentsResponse> agentsResult = await client.GetAgentsAsync(cancellationToken).ConfigureAwait(false);
        if (!agentsResult.IsSuccess || agentsResult.Value is null)
        {
            return Results.Failure(agentsResult.ErrorMessage ?? "Failed to load agents.");
        }

        AnsiConsole.Write(BuildAgentListRenderable(agentsResult.Value.Agents));
        return Results.Success();
    }

    private static async Task<Result> ShowAsync(TuiCommandOptions options, string agentId, CancellationToken cancellationToken)
    {
        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewayAgentsResponse> agentsResult = await client.GetAgentsAsync(cancellationToken).ConfigureAwait(false);
        if (!agentsResult.IsSuccess || agentsResult.Value is null)
        {
            return Results.Failure(agentsResult.ErrorMessage ?? "Failed to load agents.");
        }

        GatewayAgentListItem? agent = agentsResult.Value.Agents.FirstOrDefault(item => string.Equals(item.Id, agentId, StringComparison.Ordinal));
        if (agent is null)
        {
            return Results.Failure($"Agent '{agentId}' was not found.");
        }

        AnsiConsole.Write(BuildAgentDetailsRenderable(agent));

        return Results.Success();
    }

    private static async Task<Result> EditInteractiveAsync(TuiCommandOptions options, string agentId, CancellationToken cancellationToken)
    {
        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewayAgentsResponse> agentsResult = await client.GetAgentsAsync(cancellationToken).ConfigureAwait(false);
        if (!agentsResult.IsSuccess || agentsResult.Value is null)
        {
            return Results.Failure(agentsResult.ErrorMessage ?? "Failed to load agents.");
        }

        GatewayAgentListItem? existing = agentsResult.Value.Agents.FirstOrDefault(agent => string.Equals(agent.Id, agentId, StringComparison.Ordinal));
        return existing is null
            ? Results.Failure($"Agent '{agentId}' was not found.")
            : await SaveInteractiveAsync(options, existing, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<Result> SaveInteractiveAsync(TuiCommandOptions options, GatewayAgentListItem? existing, CancellationToken cancellationToken)
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

        if (modelsResult.Value.Models.Count == 0)
        {
            return Results.Failure("Create a model before saving an agent.");
        }

        Result<GatewayConnectorsResponse> connectorsResult = await client.GetConnectorsAsync(cancellationToken).ConfigureAwait(false);
        if (!connectorsResult.IsSuccess || connectorsResult.Value is null)
        {
            return Results.Failure(connectorsResult.ErrorMessage ?? "Failed to load connectors.");
        }

        Result<GatewayPersonaDefinitionsResponse> personaDefinitionsResult = await client.GetPersonaDefinitionsAsync(cancellationToken).ConfigureAwait(false);
        JsonObject personaDefinitions = personaDefinitionsResult.IsSuccess && personaDefinitionsResult.Value is not null
            ? personaDefinitionsResult.Value.Definitions
            : new JsonObject();

        string agentId = existing?.Id ?? PromptRequiredString("Agent id", null);
        string name = PromptRequiredString("Agent name", existing?.Name ?? agentId);
        GatewayModelListItem model = PromptModel(modelsResult.Value.Models, existing?.ModelId);
        string systemPrompt = PromptRequiredString("System prompt", existing?.SystemPrompt);
        string superExecMode = PromptSelection(
            "Super execution mode",
            ["trusted", "ask", "disabled"],
            existing?.SuperExecMode ?? "trusted");

        if (connectorsResult.Value.Connectors.Count > 0)
        {
            Table connectorsTable = StripedTable.CreateRounded();
            connectorsTable.AddColumn("Connector Id");
            connectorsTable.AddColumn("Name");
            connectorsTable.AddColumn("State");

            foreach (GatewayConnectorListItem connector in connectorsResult.Value.Connectors.OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase))
            {
                connectorsTable.AddStripedRow(connector.Id, connector.Name, connector.Enabled ? "enabled" : "disabled");
            }

            AnsiConsole.Write(connectorsTable);
        }

        IReadOnlyList<GatewayAgentMcpBindingPayload> mcpBindings = ParseCsvList(PromptOptionalString(
            "Connector ids (comma-separated)",
            existing is null ? string.Empty : string.Join(", ", existing.McpBindings.Select(static binding => binding.ConnectorId))))
            .Select(static connectorId => new GatewayAgentMcpBindingPayload(connectorId, "OwnContext"))
            .ToArray();

        JsonObject persona = BuildPersonaProfile(existing?.Persona, personaDefinitions);

        bool historyEnabled = PromptConfirmation("Keep chat history enabled?", existing?.Session.HistoryEnabled ?? true);
        int maxHistoryRuns = PromptInt("Max history runs", existing?.Session.MaxHistoryRuns ?? 5, minValue: 0);
        bool summariesEnabled = PromptConfirmation("Enable summaries?", existing?.Session.SummariesEnabled ?? true);
        string? summaryModelId = PromptOptionalString("Summary model id (optional)", existing?.Session.SummaryModelId);
        bool memoryFactExtractionEnabled = PromptConfirmation("Enable memory fact extraction?", existing?.Session.MemoryFactExtractionEnabled ?? true);

        bool sandboxEnabled = PromptConfirmation("Enable sandbox?", existing?.Sandbox.Enabled ?? true);
        IReadOnlyList<string> allowedLanguages = ParseCsvList(PromptOptionalString(
            "Allowed sandbox languages (comma-separated)",
            existing is null ? string.Empty : string.Join(", ", existing.Sandbox.AllowedLanguages)));
        string maxMemory = PromptRequiredString("Sandbox max memory", existing?.Sandbox.MaxMemory ?? "2g");
        int maxTimeout = PromptInt("Sandbox max timeout (seconds)", existing?.Sandbox.MaxTimeout ?? 120, minValue: 1);

        GatewayAgentUpsertRequest request = new(
            name.Trim(),
            model.Id,
            systemPrompt.Trim(),
            superExecMode,
            mcpBindings,
            existing?.SkillAttachments,
            new GatewayAgentSessionPayload(
                historyEnabled,
                maxHistoryRuns,
                summariesEnabled,
                string.IsNullOrWhiteSpace(summaryModelId) ? null : summaryModelId.Trim(),
                memoryFactExtractionEnabled),
            new GatewayAgentSandboxPayload(
                sandboxEnabled,
                allowedLanguages,
                maxMemory.Trim(),
                maxTimeout),
            existing?.ApprovalConfig ?? new GatewayApprovalConfigPayload([], 300, EmptyStringListDictionary(), EmptyStringListDictionary(), [], []),
            persona,
            existing?.ToolExposure ?? new GatewayAgentToolExposurePayload(false, string.Empty, 60),
            existing?.Runtime);

        Result<GatewayAgentResponse> saveResult = await client.SaveAgentAsync(agentId.Trim(), request, cancellationToken).ConfigureAwait(false);
        if (!saveResult.IsSuccess || saveResult.Value is null)
        {
            return Results.Failure(saveResult.ErrorMessage ?? "Failed to save agent.");
        }

        AnsiConsole.MarkupLine($"[green]Saved agent {Markup.Escape(saveResult.Value.Agent.Id)}.[/]");
        return Results.Success();
    }

    private static async Task<Result> DeleteAsync(TuiCommandOptions options, string agentId, CancellationToken cancellationToken)
    {
        Result<GatewayApiClient> clientResult = await GatewaySessionFactory.CreateAuthenticatedClientAsync(options, cancellationToken).ConfigureAwait(false);
        if (!clientResult.IsSuccess || clientResult.Value is null)
        {
            return Results.Failure(clientResult.ErrorMessage ?? "Authentication failed.");
        }

        using GatewayApiClient client = clientResult.Value;
        Result<GatewayBooleanSuccessResponse> deleteResult = await client.DeleteAgentAsync(agentId, cancellationToken).ConfigureAwait(false);
        if (!deleteResult.IsSuccess)
        {
            return Results.Failure(deleteResult.ErrorMessage ?? "Agent deletion failed.");
        }

        AnsiConsole.MarkupLine($"[green]Deleted agent {Markup.Escape(agentId)}.[/]");
        return Results.Success();
    }

    private static GatewayModelListItem PromptModel(IReadOnlyList<GatewayModelListItem> models, string? currentModelId)
    {
        List<GatewayModelListItem> ordered = [.. models
            .OrderByDescending(model => string.Equals(model.Id, currentModelId, StringComparison.OrdinalIgnoreCase))
            .ThenBy(model => model.Id, StringComparer.OrdinalIgnoreCase)];

        return AnsiConsole.Prompt(
            new SelectionPrompt<GatewayModelListItem>()
                .Title("Model")
                .UseConverter(model => $"{model.Id} | {model.ModelName} | {model.ProviderId}")
                .AddChoices(ordered));
    }

    private static JsonObject BuildPersonaProfile(JsonObject? existing, JsonObject definitions)
    {
        JsonObject persona = existing is null ? new JsonObject() : DeepClone(existing);
        persona["category"] = PromptSelection("Persona category", ReadStringArray(definitions, "categories", "general"), ReadPersonaString(existing, "category") ?? "general");
        persona["style"] = PromptSelection("Persona style", ReadStringArray(definitions, "styles", "professional"), ReadPersonaString(existing, "style") ?? "professional");
        persona["tone"] = PromptSelection("Persona tone", ReadStringArray(definitions, "tones", "neutral"), ReadPersonaString(existing, "tone") ?? "neutral");
        persona["verbosity"] = PromptSelection("Persona verbosity", ReadStringArray(definitions, "verbosity_levels", "concise"), ReadPersonaString(existing, "verbosity") ?? "concise");

        string companyName = PromptOptionalString(
            "Office company name (optional)",
            ReadNestedPersonaString(existing, "office_worker", "company_name") ?? ReadPersonaString(definitions, "company_name"));
        string title = PromptOptionalString("Office title (optional)", ReadNestedPersonaString(existing, "office_worker", "title"));
        string department = PromptOptionalString("Office department (optional)", ReadNestedPersonaString(existing, "office_worker", "department"));
        string industry = PromptOptionalString("Office industry (optional)", ReadNestedPersonaString(existing, "office_worker", "industry"));

        if (string.IsNullOrWhiteSpace(companyName)
            && string.IsNullOrWhiteSpace(title)
            && string.IsNullOrWhiteSpace(department)
            && string.IsNullOrWhiteSpace(industry))
        {
            persona.Remove("office_worker");
            return persona;
        }

        JsonObject officeWorker = [];
        if (!string.IsNullOrWhiteSpace(companyName))
        {
            officeWorker["company_name"] = companyName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            officeWorker["title"] = title.Trim();
        }

        if (!string.IsNullOrWhiteSpace(department))
        {
            officeWorker["department"] = department.Trim();
        }

        if (!string.IsNullOrWhiteSpace(industry))
        {
            officeWorker["industry"] = industry.Trim();
        }

        persona["office_worker"] = officeWorker;
        return persona;
    }

    private static JsonObject DeepClone(JsonObject source)
        => JsonNode.Parse(source.ToJsonString())?.AsObject() ?? new JsonObject();

    private static Dictionary<string, IReadOnlyList<string>> EmptyStringListDictionary()
        => new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<string> ReadStringArray(JsonObject source, string propertyName, string fallback)
        => source[propertyName] is JsonArray array
            ? [.. array.Select(static item => item?.GetValue<string>()?.Trim()).Where(static item => !string.IsNullOrWhiteSpace(item))!]
            : [fallback];

    private static string? ReadPersonaString(JsonObject? persona, string propertyName)
        => persona?[propertyName]?.GetValue<string?>();

    private static string? ReadNestedPersonaString(JsonObject? persona, string sectionName, string propertyName)
        => persona?[sectionName] is JsonObject section
            ? section[propertyName]?.GetValue<string?>()
            : null;

    private static string FormatConnectorBindings(IReadOnlyList<GatewayAgentMcpBindingPayload> bindings)
        => bindings.Count == 0
            ? "-"
            : string.Join(", ", bindings.Select(static binding => $"{binding.ConnectorId}:{binding.Context}"));

    private static string FormatRuntimeClock(int? seconds, string inheritLabel)
        => seconds is null ? inheritLabel : seconds.Value <= 0 ? "unlimited" : $"{seconds.Value}s";

    private static IReadOnlyList<string> ParseCsvList(string value)
        => [.. value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(static item => !string.IsNullOrWhiteSpace(item))];

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

    private static string PromptSelection(string label, IReadOnlyList<string> options, string currentValue)
    {
        List<string> ordered = [.. options
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(option => string.Equals(option, currentValue, StringComparison.OrdinalIgnoreCase))
            .ThenBy(option => option, StringComparer.OrdinalIgnoreCase)];

        return AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title(label)
                .AddChoices(ordered));
    }

    private static bool PromptConfirmation(string label, bool currentValue)
        => AnsiConsole.Confirm(label, currentValue);

    private static int PromptInt(string label, int currentValue, int minValue)
        => AnsiConsole.Prompt(
            new TextPrompt<int>(label)
                .DefaultValue(currentValue)
                .Validate(value => value >= minValue));
}
