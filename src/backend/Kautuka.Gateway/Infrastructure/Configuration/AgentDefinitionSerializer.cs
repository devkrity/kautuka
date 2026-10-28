using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kautuka.Gateway.Infrastructure.Configuration;

internal static class AgentDefinitionSerializer
{
    private static readonly PlatformConfigurationJsonContext jsonContext = PlatformConfigurationJsonContext.Default;

    internal sealed record SerializedAgentDefinition(
        string Id,
        string Scope,
        int Enabled,
        string Name,
        string ModelId,
        string SystemPrompt,
        string SuperExecMode,
        string SessionJson,
        string SandboxJson,
        string McpBindingsJson,
        string SkillAttachmentsJson,
        string PersonaJson,
        string ApprovalConfigJson,
        string ToolExposureJson);

    public static SerializedAgentDefinition Serialize(AgentDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        AgentSessionDefinition session = definition.Session ?? new AgentSessionDefinition();
        AgentSandboxDefinition sandbox = definition.Sandbox ?? new AgentSandboxDefinition();
        IReadOnlyList<AgentMcpBindingDefinition> mcpBindings = definition.McpBindings ?? [];
        IReadOnlyList<AgentSkillAttachmentDefinition> skillAttachments = definition.SkillAttachments ?? [];
        JsonObject persona = definition.Persona ?? new JsonObject();
        ApprovalPolicy approvalPolicy = definition.ApprovalPolicy ?? new ApprovalPolicy();
        AgentToolExposureDefinition toolExposure = definition.ToolExposure ?? new AgentToolExposureDefinition();
        AgentRuntimeDefinition runtime = definition.Runtime ?? new AgentRuntimeDefinition();

        string sessionJson = JsonSerializer.Serialize(
            new AgentSessionDocument(
                session.HistoryEnabled,
                session.MaxHistoryRuns,
                session.SummariesEnabled,
                session.SummaryModelId,
                session.MemoryFactExtractionEnabled,
                new AgentRuntimeDocument(
                    runtime.MaxRunDurationSeconds,
                    runtime.HitlExpirySeconds,
                    runtime.HitlExpiryAction,
                    runtime.AllowMultipleToolCalls,
                    runtime.AllowConcurrentInvocation,
                    runtime.MaxParallelTools)),
            jsonContext.AgentSessionDocument);
        string sandboxJson = JsonSerializer.Serialize(
            new AgentSandboxDocument(
                sandbox.Enabled,
                [.. (sandbox.AllowedLanguages ?? [])],
                sandbox.MaxMemory,
                sandbox.MaxTimeout),
            jsonContext.AgentSandboxDocument);
        string mcpBindingsJson = JsonSerializer.Serialize(mcpBindings.ToList(), jsonContext.ListAgentMcpBindingDefinition);
        string skillAttachmentsJson = JsonSerializer.Serialize(skillAttachments.ToList(), jsonContext.ListAgentSkillAttachmentDefinition);
        string personaJson = JsonSerializer.Serialize(persona, jsonContext.JsonObject);
        string approvalJson = JsonSerializer.Serialize(approvalPolicy, jsonContext.ApprovalPolicy);
        string toolExposureJson = JsonSerializer.Serialize(toolExposure, jsonContext.AgentToolExposureDefinition);

        return new SerializedAgentDefinition(
            definition.Id,
            definition.Scope,
            definition.Enabled ? 1 : 0,
            definition.Name,
            definition.ModelId,
            definition.SystemPrompt,
            definition.SuperExecMode,
            sessionJson,
            sandboxJson,
            mcpBindingsJson,
            skillAttachmentsJson,
            personaJson,
            approvalJson,
            toolExposureJson);
    }

    public static AgentDefinition Deserialize(SerializedAgentDefinition serialized)
    {
        ArgumentNullException.ThrowIfNull(serialized);

        AgentSessionDocument sessionDoc = JsonSerializer.Deserialize(serialized.SessionJson, jsonContext.AgentSessionDocument)
            ?? new AgentSessionDocument(true, 5, true, null, true);
        AgentSandboxDocument sandboxDoc = JsonSerializer.Deserialize(serialized.SandboxJson, jsonContext.AgentSandboxDocument)
            ?? new AgentSandboxDocument(true, [], "2g", 120);
        ApprovalPolicy approvalPolicy = JsonSerializer.Deserialize(serialized.ApprovalConfigJson, jsonContext.ApprovalPolicy)
            ?? new ApprovalPolicy();
        List<AgentMcpBindingDefinition> mcpBindings = JsonSerializer.Deserialize(serialized.McpBindingsJson, jsonContext.ListAgentMcpBindingDefinition) ?? [];
        AgentRuntimeDocument? runtimeDoc = sessionDoc.Runtime;

        return new AgentDefinition
        {
            Id = serialized.Id,
            Scope = serialized.Scope,
            Enabled = serialized.Enabled != 0,
            Name = serialized.Name,
            ModelId = serialized.ModelId,
            SystemPrompt = serialized.SystemPrompt,
            SuperExecMode = NormalizeSuperExecMode(serialized.SuperExecMode),
            ApprovalPolicy = approvalPolicy,
            Session = new AgentSessionDefinition
            {
                HistoryEnabled = sessionDoc.HistoryEnabled,
                MaxHistoryRuns = sessionDoc.MaxHistoryRuns,
                SummariesEnabled = sessionDoc.SummariesEnabled,
                SummaryModelId = sessionDoc.SummaryModelId,
                MemoryFactExtractionEnabled = sessionDoc.MemoryFactExtractionEnabled,
            },
            Sandbox = new AgentSandboxDefinition
            {
                Enabled = sandboxDoc.Enabled,
                AllowedLanguages = new Collection<string>(sandboxDoc.AllowedLanguages ?? []),
                MaxMemory = sandboxDoc.MaxMemory,
                MaxTimeout = sandboxDoc.MaxTimeout,
            },
            McpBindings = mcpBindings,
            SkillAttachments = JsonSerializer.Deserialize(serialized.SkillAttachmentsJson, jsonContext.ListAgentSkillAttachmentDefinition) ?? [],
            Persona = JsonSerializer.Deserialize(serialized.PersonaJson, jsonContext.JsonObject) ?? new JsonObject(),
            ToolExposure = JsonSerializer.Deserialize(serialized.ToolExposureJson, jsonContext.AgentToolExposureDefinition)
                ?? new AgentToolExposureDefinition(),
            Runtime = new AgentRuntimeDefinition
            {
                MaxRunDurationSeconds = runtimeDoc?.MaxRunDurationSeconds,
                HitlExpirySeconds = runtimeDoc?.HitlExpirySeconds,
                HitlExpiryAction = runtimeDoc?.HitlExpiryAction,
                AllowMultipleToolCalls = runtimeDoc?.AllowMultipleToolCalls,
                AllowConcurrentInvocation = runtimeDoc?.AllowConcurrentInvocation,
                MaxParallelTools = runtimeDoc?.MaxParallelTools,
            },
        };
    }

    public static string NormalizeSuperExecMode(string? raw) => raw switch
    {
        "0" or "" or null => "disabled",
        "1" => "trusted",
        _ => raw,
    };
}
