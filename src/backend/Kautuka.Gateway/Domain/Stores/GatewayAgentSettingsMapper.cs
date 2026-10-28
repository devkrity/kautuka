using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Runtime;

namespace Kautuka.Gateway.Domain.Stores;

internal static class GatewayAgentSettingsMapper
{
    public static GatewayAgentListItem ToListItem(GatewayAgent agent, JsonObject persona)
    {
        GatewayAgentSessionPayload session = NormalizeStoredSession(agent.Session);
        GatewayAgentPlanningPayload planning = NormalizeStoredPlanning(agent.Planning);
        GatewayAgentSandboxPayload sandbox = NormalizeStoredSandbox(agent.Sandbox);
        GatewayApprovalConfigPayload approvalConfig = NormalizeStoredApprovalConfig(agent.ApprovalConfig);
        GatewayAgentToolExposurePayload toolExposure = NormalizeStoredToolExposure(agent.ToolExposure);
        GatewayAgentRuntimePayload runtime = NormalizeStoredRuntime(agent.Runtime);

        return new GatewayAgentListItem(
            agent.Id,
            agent.Scope,
            agent.Enabled,
            agent.Name,
            agent.ModelId,
            agent.SystemPrompt,
            agent.SuperExecMode,
            [.. (agent.McpBindings ?? []).Select(static binding => new GatewayAgentMcpBindingPayload
            {
                ConnectorId = binding.ConnectorId,
                Context = binding.Context,
            })],
            [.. agent.SkillAttachments],
            session,
            planning,
            sandbox,
            agent.CreatedAt,
            approvalConfig,
            persona,
            toolExposure,
            agent.OwnerUserId,
            runtime,
            CallerAgentIds: null,
            Revision: agent.Revision);
    }

    public static GatewayAgentSessionPayload NormalizeStoredSession(JsonObject? existing)
        => NormalizeSession(null, existing);

    public static GatewayAgentSandboxPayload NormalizeStoredSandbox(JsonObject? existing)
        => NormalizeSandbox(null, existing);

    public static GatewayAgentPlanningPayload NormalizeStoredPlanning(JsonObject? existing)
        => NormalizePlanning(null, existing);

    public static GatewayApprovalConfigPayload NormalizeStoredApprovalConfig(JsonObject? existing)
        => NormalizeApprovalConfig(null, existing);

    public static GatewayAgentToolExposurePayload NormalizeStoredToolExposure(JsonObject? existing)
        => NormalizeToolExposure(null, existing);

    public static GatewayAgentRuntimePayload NormalizeStoredRuntime(JsonObject? existing)
        => NormalizeRuntime(null, existing);

    public static GatewayAgentRuntimePayload FromDefinition(AgentRuntimeDefinition? runtime)
        => new()
        {
            MaxRunDurationSeconds = runtime?.MaxRunDurationSeconds,
            HitlExpirySeconds = runtime?.HitlExpirySeconds,
            HitlExpiryAction = runtime?.HitlExpiryAction,
            AllowMultipleToolCalls = runtime?.AllowMultipleToolCalls,
            AllowConcurrentInvocation = runtime?.AllowConcurrentInvocation,
            MaxParallelTools = runtime?.MaxParallelTools,
        };

    public static GatewayAgentSessionPayload NormalizeSession(
        GatewayAgentSessionPayload? request,
        JsonObject? existing = null)
        => new()
        {
            HistoryEnabled = request?.HistoryEnabled ?? ReadBool(existing, true, "historyEnabled", "history_enabled"),
            MaxHistoryRuns = Math.Max(1, request?.MaxHistoryRuns ?? ReadInt(existing, 5, "maxHistoryRuns", "max_history_runs")),
            SummariesEnabled = request?.SummariesEnabled ?? ReadBool(existing, true, "summariesEnabled", "summaries_enabled"),
            SummaryModelId = request?.SummaryModelId ?? ReadString(existing, "summaryModelId", "summary_model_id"),
            MemoryFactExtractionEnabled = request?.MemoryFactExtractionEnabled ?? ReadBool(existing, true, "memoryFactExtractionEnabled", "memory_fact_extraction_enabled"),
        };

    public static GatewayAgentSandboxPayload NormalizeSandbox(
        GatewayAgentSandboxPayload? request,
        JsonObject? existing = null)
        => new()
        {
            Enabled = request?.Enabled ?? ReadBool(existing, true, "enabled"),
            AllowedLanguages = request?.AllowedLanguages is { Length: > -1 } requestLanguages
                ? [.. requestLanguages]
                : [.. ReadStringArray(existing, "allowedLanguages", "allowed_languages")],
            MaxMemory = request?.MaxMemory ?? ReadString(existing, "maxMemory", "max_memory") ?? "2g",
            MaxTimeout = request?.MaxTimeout ?? ReadInt(existing, 120, "maxTimeout", "max_timeout"),
        };

    public static GatewayAgentPlanningPayload NormalizePlanning(
        GatewayAgentPlanningPayload? _,
        JsonObject? __ = null)
        => new()
        {
            Enabled = false,
            RequireSignoff = false,
        };

    public static GatewayApprovalConfigPayload NormalizeApprovalConfig(
        GatewayApprovalConfigPayload? request,
        JsonObject? existing = null)
        => new()
        {
            RequireMcpIds = request?.RequireMcpIds is { Length: > -1 } requireMcpIds
                ? [.. requireMcpIds]
                : [.. ReadStringArray(existing, "requireMcpIds", "require_mcp_ids")],
            TimeoutSeconds = request?.TimeoutSeconds ?? ReadInt(existing, 300, "timeoutSeconds", "timeout_seconds"),
            RequireMcpFunctionFilters = request?.RequireMcpFunctionFilters is not null
                ? CloneStringArrayDictionary(request.RequireMcpFunctionFilters)
                : ReadStringArrayDictionary(existing, "requireMcpFunctionFilters", "require_mcp_function_filters"),
            DisabledMcpFunctions = request?.DisabledMcpFunctions is not null
                ? CloneStringArrayDictionary(request.DisabledMcpFunctions)
                : ReadStringArrayDictionary(existing, "disabledMcpFunctions", "disabled_mcp_functions"),
            AgentToolIds = request?.AgentToolIds is { Length: > -1 } agentToolIds
                ? [.. agentToolIds]
                : [.. ReadStringArray(existing, "agentToolIds")],
            ApprovalRequiredAgentToolIds = request?.ApprovalRequiredAgentToolIds is { Length: > -1 } approvalRequiredAgentToolIds
                ? [.. approvalRequiredAgentToolIds]
                : [.. ReadStringArray(existing, "approvalRequiredAgentToolIds")],
        };

    public static GatewayAgentToolExposurePayload NormalizeToolExposure(
        GatewayAgentToolExposurePayload? request,
        JsonObject? existing = null)
        => new()
        {
            ExposedAsTool = request?.ExposedAsTool ?? ReadBool(existing, false, "exposedAsTool"),
            Description = request?.Description ?? ReadString(existing, "description") ?? string.Empty,
            InvocationTimeoutSeconds = request?.InvocationTimeoutSeconds ?? ReadInt(existing, 60, "invocationTimeoutSeconds"),
        };

    public static GatewayAgentRuntimePayload NormalizeRuntime(
        GatewayAgentRuntimePayload? request,
        JsonObject? existing = null)
    {
        if (request is null)
        {
            return new GatewayAgentRuntimePayload
            {
                MaxRunDurationSeconds = ReadNullableInt(existing, "maxRunDurationSeconds", "max_run_duration_seconds"),
                HitlExpirySeconds = ReadNullableInt(existing, "hitlExpirySeconds", "hitl_expiry_seconds"),
                HitlExpiryAction = ReadString(existing, "hitlExpiryAction", "hitl_expiry_action"),
                AllowMultipleToolCalls = ReadNullableBool(existing, "allowMultipleToolCalls", "allow_multiple_tool_calls"),
                AllowConcurrentInvocation = ReadNullableBool(existing, "allowConcurrentInvocation", "allow_concurrent_invocation"),
                MaxParallelTools = ReadNullableInt(existing, "maxParallelTools", "max_parallel_tools") is int storedMax
                    ? RuntimeSettingKeys.ClampMaxParallelTools(storedMax)
                    : null,
            };
        }

        return new GatewayAgentRuntimePayload
        {
            MaxRunDurationSeconds = request.MaxRunDurationSeconds,
            HitlExpirySeconds = request.HitlExpirySeconds,
            HitlExpiryAction = string.IsNullOrWhiteSpace(request.HitlExpiryAction) || !HitlExpiryActions.IsKnown(request.HitlExpiryAction)
                ? null
                : HitlExpiryActions.ToStorage(HitlExpiryActions.Parse(request.HitlExpiryAction)),
            AllowMultipleToolCalls = request.AllowMultipleToolCalls,
            AllowConcurrentInvocation = request.AllowConcurrentInvocation,
            MaxParallelTools = request.MaxParallelTools is int maxParallel
                ? RuntimeSettingKeys.ClampMaxParallelTools(maxParallel)
                : null,
        };
    }

    public static JsonObject ToJsonObject(GatewayAgentSessionPayload payload)
        => new()
        {
            ["historyEnabled"] = payload.HistoryEnabled,
            ["maxHistoryRuns"] = payload.MaxHistoryRuns,
            ["summariesEnabled"] = payload.SummariesEnabled,
            ["summaryModelId"] = payload.SummaryModelId,
            ["memoryFactExtractionEnabled"] = payload.MemoryFactExtractionEnabled,
        };

    public static JsonObject ToJsonObject(GatewayAgentSandboxPayload payload)
        => new()
        {
            ["enabled"] = payload.Enabled,
            ["allowedLanguages"] = CreateStringArrayNode(payload.AllowedLanguages),
            ["maxMemory"] = payload.MaxMemory,
            ["maxTimeout"] = payload.MaxTimeout,
        };

    public static JsonObject ToJsonObject(GatewayAgentPlanningPayload payload)
        => new()
        {
            ["enabled"] = payload.Enabled,
            ["requireSignoff"] = payload.RequireSignoff,
        };

    public static JsonObject ToJsonObject(GatewayApprovalConfigPayload payload)
        => new()
        {
            ["requireMcpIds"] = CreateStringArrayNode(payload.RequireMcpIds),
            ["timeoutSeconds"] = payload.TimeoutSeconds,
            ["requireMcpFunctionFilters"] = ToJsonObject(payload.RequireMcpFunctionFilters),
            ["disabledMcpFunctions"] = ToJsonObject(payload.DisabledMcpFunctions),
            ["agentToolIds"] = CreateStringArrayNode(payload.AgentToolIds),
            ["approvalRequiredAgentToolIds"] = CreateStringArrayNode(payload.ApprovalRequiredAgentToolIds),
        };

    public static JsonObject ToJsonObject(GatewayAgentToolExposurePayload payload)
        => new()
        {
            ["exposedAsTool"] = payload.ExposedAsTool,
            ["description"] = payload.Description,
            ["invocationTimeoutSeconds"] = payload.InvocationTimeoutSeconds,
        };

    public static JsonObject ToJsonObject(GatewayAgentRuntimePayload payload)
    {
        var result = new JsonObject();
        if (payload.MaxRunDurationSeconds is int maxRun)
        {
            result["maxRunDurationSeconds"] = maxRun;
        }

        if (payload.HitlExpirySeconds is int hitlExpiry)
        {
            result["hitlExpirySeconds"] = hitlExpiry;
        }

        if (!string.IsNullOrWhiteSpace(payload.HitlExpiryAction))
        {
            result["hitlExpiryAction"] = payload.HitlExpiryAction;
        }

        if (payload.AllowMultipleToolCalls is bool allowMultiple)
        {
            result["allowMultipleToolCalls"] = allowMultiple;
        }

        if (payload.AllowConcurrentInvocation is bool allowConcurrent)
        {
            result["allowConcurrentInvocation"] = allowConcurrent;
        }

        if (payload.MaxParallelTools is int maxParallel)
        {
            result["maxParallelTools"] = RuntimeSettingKeys.ClampMaxParallelTools(maxParallel);
        }

        return result;
    }

    public static AgentSessionDefinition ToDefinition(GatewayAgentSessionPayload payload)
        => new()
        {
            HistoryEnabled = payload.HistoryEnabled,
            MaxHistoryRuns = payload.MaxHistoryRuns,
            SummariesEnabled = payload.SummariesEnabled,
            MemoryFactExtractionEnabled = payload.MemoryFactExtractionEnabled,
            SummaryModelId = payload.SummaryModelId,
        };

    public static AgentSandboxDefinition ToDefinition(GatewayAgentSandboxPayload payload)
        => new()
        {
            Enabled = payload.Enabled,
            AllowedLanguages = new Collection<string>([.. payload.AllowedLanguages]),
            MaxMemory = payload.MaxMemory,
            MaxTimeout = payload.MaxTimeout,
        };

    public static ApprovalPolicy ToDefinition(GatewayApprovalConfigPayload payload)
        => new()
        {
            RequireMcpIds = [.. payload.RequireMcpIds],
            TimeoutSeconds = payload.TimeoutSeconds,
            RequireMcpFunctionFilters = ToReadOnlyListDictionary(payload.RequireMcpFunctionFilters),
            DisabledMcpFunctions = ToReadOnlyListDictionary(payload.DisabledMcpFunctions),
            AgentToolIds = [.. payload.AgentToolIds],
            ApprovalRequiredAgentToolIds = [.. payload.ApprovalRequiredAgentToolIds],
        };

    public static AgentToolExposureDefinition ToDefinition(GatewayAgentToolExposurePayload payload)
        => new()
        {
            ExposedAsTool = payload.ExposedAsTool,
            Description = payload.Description,
            InvocationTimeoutSeconds = payload.InvocationTimeoutSeconds,
        };

    public static AgentRuntimeDefinition ToDefinition(GatewayAgentRuntimePayload payload)
        => new()
        {
            MaxRunDurationSeconds = payload.MaxRunDurationSeconds,
            HitlExpirySeconds = payload.HitlExpirySeconds,
            HitlExpiryAction = payload.HitlExpiryAction,
            AllowMultipleToolCalls = payload.AllowMultipleToolCalls,
            AllowConcurrentInvocation = payload.AllowConcurrentInvocation,
            MaxParallelTools = payload.MaxParallelTools is int maxParallel
                ? RuntimeSettingKeys.ClampMaxParallelTools(maxParallel)
                : null,
        };

    public static AgentSkillAttachmentDefinition[] NormalizeSkillAttachments(
        GatewayAgentSkillAttachmentPayload[]? payloads,
        IReadOnlyList<AgentSkillAttachmentDefinition>? current)
    {
        if (payloads is null)
        {
            return current is { Count: > 0 } ? [.. current] : [];
        }

        var attachments = new List<AgentSkillAttachmentDefinition>(payloads.Length);
        foreach (GatewayAgentSkillAttachmentPayload item in payloads)
        {
            if (string.IsNullOrWhiteSpace(item.SkillId))
            {
                continue;
            }

            attachments.Add(new AgentSkillAttachmentDefinition
            {
                SkillId = item.SkillId.Trim(),
                Mode = ParseSkillAttachmentMode(item.Mode),
            });
        }

        return [.. attachments];
    }

    private static SkillAttachmentMode ParseSkillAttachmentMode(string? raw)
        => raw?.Trim().ToUpperInvariant() switch
        {
            "TRUSTED" => SkillAttachmentMode.Trusted,
            "REQUIREAPPROVAL" or "REQUIRE_APPROVAL" or "REQUIRE-APPROVAL" => SkillAttachmentMode.RequireApproval,
            _ => SkillAttachmentMode.Disabled,
        };

    private static Dictionary<string, IReadOnlyList<string>> ToReadOnlyListDictionary(
        Dictionary<string, string[]> source)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string[] values) in source)
        {
            if (values.Length > 0)
            {
                result[key] = [.. values];
            }
        }

        return result;
    }

    private static Dictionary<string, string[]> CloneStringArrayDictionary(
        Dictionary<string, string[]> source)
    {
        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string[] values) in source)
        {
            string[] cleaned = values
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .ToArray();
            if (cleaned.Length > 0)
            {
                result[key] = cleaned;
            }
        }

        return result;
    }

    private static JsonObject ToJsonObject(Dictionary<string, string[]> values)
    {
        var result = new JsonObject();
        foreach ((string key, string[] entries) in values)
        {
            result[key] = CreateStringArrayNode(entries);
        }

        return result;
    }

    private static JsonArray CreateStringArrayNode(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (string value in values)
        {
            array.Add((JsonNode?)JsonValue.Create(value));
        }

        return array;
    }

    private static bool ReadBool(JsonObject? source, bool fallback, params string[] keys)
    {
        if (source is null)
        {
            return fallback;
        }

        foreach (string key in keys)
        {
            if (source.TryGetPropertyValue(key, out JsonNode? _))
            {
                return source.GetBoolNodeValue(key, fallback);
            }
        }

        return fallback;
    }

    private static int ReadInt(JsonObject? source, int fallback, params string[] keys)
    {
        if (source is null)
        {
            return fallback;
        }

        foreach (string key in keys)
        {
            if (source.TryGetPropertyValue(key, out JsonNode? _))
            {
                return source.GetIntNodeValue(key, fallback);
            }
        }

        return fallback;
    }

    private static int? ReadNullableInt(JsonObject? source, params string[] keys)
    {
        if (source is null)
        {
            return null;
        }

        foreach (string key in keys)
        {
            if (!source.TryGetPropertyValue(key, out JsonNode? node) || node is null)
            {
                continue;
            }

            if (node.GetValueKind() == JsonValueKind.Null)
            {
                return null;
            }

            return source.GetIntNodeValue(key, 0);
        }

        return null;
    }

    private static bool? ReadNullableBool(JsonObject? source, params string[] keys)
    {
        if (source is null)
        {
            return null;
        }

        foreach (string key in keys)
        {
            if (!source.TryGetPropertyValue(key, out JsonNode? node) || node is null)
            {
                continue;
            }

            if (node.GetValueKind() == JsonValueKind.Null)
            {
                return null;
            }

            return source.GetBoolNodeValue(key, false);
        }

        return null;
    }

    private static string? ReadString(JsonObject? source, params string[] keys)
    {
        if (source is null)
        {
            return null;
        }

        foreach (string key in keys)
        {
            if (source.TryGetPropertyValue(key, out JsonNode? _))
            {
                return source.GetStringNodeValue(key);
            }
        }

        return null;
    }

    private static string[] ReadStringArray(JsonObject? source, params string[] keys)
    {
        if (source is null)
        {
            return [];
        }

        foreach (string key in keys)
        {
            List<string> values = source.GetStringArrayNodeValue(key);
            if (values.Count > 0 || source.TryGetPropertyValue(key, out JsonNode? _))
            {
                return [.. values];
            }
        }

        return [];
    }

    private static Dictionary<string, string[]> ReadStringArrayDictionary(JsonObject? source, params string[] keys)
    {
        JsonObject? candidate = GetObject(source, keys);
        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (candidate is null)
        {
            return result;
        }

        foreach ((string key, JsonNode? value) in candidate)
        {
            if (value is not JsonArray arr)
            {
                continue;
            }

            string[] items = arr
                .Select(static item => item?.GetValue<string>())
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .Select(static item => item!.Trim())
                .ToArray();

            if (items.Length > 0)
            {
                result[key] = items;
            }
        }

        return result;
    }

    private static JsonObject? GetObject(JsonObject? source, params string[] keys)
    {
        if (source is null)
        {
            return null;
        }

        foreach (string key in keys)
        {
            if (source.TryGetPropertyValue(key, out JsonNode? node) && node is JsonObject jsonObject)
            {
                return jsonObject;
            }
        }

        return null;
    }
}
