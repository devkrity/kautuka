using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Kautuka.Gateway.Infrastructure.Configuration;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(AgentSkillAttachmentDefinition))]
[JsonSerializable(typeof(List<AgentSkillAttachmentDefinition>))]
[JsonSerializable(typeof(EffectiveSkillManifestEntry))]
[JsonSerializable(typeof(List<EffectiveSkillManifestEntry>))]
[JsonSerializable(typeof(SkillDefinition))]
[JsonSerializable(typeof(List<SkillDefinition>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(ApprovalPolicy))]
[JsonSerializable(typeof(AgentMcpBindingDefinition))]
[JsonSerializable(typeof(List<AgentMcpBindingDefinition>))]
[JsonSerializable(typeof(AgentToolExposureDefinition))]
[JsonSerializable(typeof(AgentSessionDocument))]
[JsonSerializable(typeof(AgentRuntimeDocument))]
[JsonSerializable(typeof(AgentSandboxDocument))]
internal sealed partial class PlatformConfigurationJsonContext : JsonSerializerContext
{
}

internal sealed record AgentSessionDocument(
    [property: JsonPropertyName("history_enabled")] bool HistoryEnabled,
    [property: JsonPropertyName("max_history_runs")] int MaxHistoryRuns,
    [property: JsonPropertyName("summaries_enabled")] bool SummariesEnabled,
    [property: JsonPropertyName("summary_model_id")] string? SummaryModelId,
    [property: JsonPropertyName("memory_fact_extraction_enabled")] bool MemoryFactExtractionEnabled,
    [property: JsonPropertyName("runtime")] AgentRuntimeDocument? Runtime = null);

internal sealed record AgentRuntimeDocument(
    [property: JsonPropertyName("max_run_duration_seconds")] int? MaxRunDurationSeconds,
    [property: JsonPropertyName("hitl_expiry_seconds")] int? HitlExpirySeconds,
    [property: JsonPropertyName("hitl_expiry_action")] string? HitlExpiryAction,
    [property: JsonPropertyName("allow_multiple_tool_calls")] bool? AllowMultipleToolCalls = null,
    [property: JsonPropertyName("allow_concurrent_invocation")] bool? AllowConcurrentInvocation = null,
    [property: JsonPropertyName("max_parallel_tools")] int? MaxParallelTools = null);

internal sealed record AgentSandboxDocument(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("allowed_languages")] List<string> AllowedLanguages,
    [property: JsonPropertyName("max_memory")] string MaxMemory,
    [property: JsonPropertyName("max_timeout")] int MaxTimeout);
