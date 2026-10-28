using System.Collections.ObjectModel;
using System.Text.Json.Nodes;

namespace Kautuka.Gateway.Infrastructure.Configuration;

internal sealed class AgentDefinition
{
    public string Id { get; init; } = string.Empty;

    /// <summary>
    /// Agent ownership scope.
    /// - system: shared visibility, admin-governed management.
    /// - user: owner-only visibility and management.
    /// </summary>
    public string Scope { get; init; } = "user";

    /// <summary>Owner recorded on the agent registry. Empty when the definition was loaded without that row.</summary>
    public string? OwnerUserId { get; init; }

    /// <summary>
    /// Controls whether this agent is available for execution/listing.
    /// </summary>
    public bool Enabled { get; init; } = true;

    public string Name { get; init; } = string.Empty;

    public string ModelId { get; init; } = string.Empty;

    public string SystemPrompt { get; init; } = string.Empty;

    /// <summary>SuperExec tool permission: "disabled", "trusted", or "ask".</summary>
    public string SuperExecMode { get; init; } = "disabled";

    public ApprovalPolicy ApprovalPolicy { get; init; } = new();

    public AgentSessionDefinition Session { get; init; } = new();

    public AgentSandboxDefinition Sandbox { get; init; } = new();

    /// <summary>Per-agent MCP bindings. Bindings with context "agent" are exposed as callable tools.</summary>
    public IReadOnlyList<AgentMcpBindingDefinition> McpBindings { get; init; } = [];

    /// <summary>Platform skill attachments accessible to this agent when running standalone.</summary>
    public IReadOnlyList<AgentSkillAttachmentDefinition> SkillAttachments { get; init; } = [];

    /// <summary>Stored persona profile for this agent, without organization defaults applied.</summary>
    public JsonObject Persona { get; init; } = new();

    /// <summary>Controls whether this agent is exposed as a callable tool to other agents.</summary>
    public AgentToolExposureDefinition ToolExposure { get; init; } = new();

    /// <summary>Optional per-agent run clocks and tool-invocation overrides. Null fields inherit the platform default.</summary>
    public AgentRuntimeDefinition Runtime { get; init; } = new();

}

/// <summary>
/// Per-agent overrides for run clocks and tool invocation. Null fields inherit the platform default.
/// </summary>
internal sealed class AgentRuntimeDefinition
{
    public int? MaxRunDurationSeconds { get; init; }

    public int? HitlExpirySeconds { get; init; }

    public string? HitlExpiryAction { get; init; }

    public bool? AllowMultipleToolCalls { get; init; }

    public bool? AllowConcurrentInvocation { get; init; }

    public int? MaxParallelTools { get; init; }
}

internal sealed class AgentMcpBindingDefinition
{
    public string ConnectorId { get; init; } = string.Empty;

    public string Context { get; init; } = "agent";
}

/// <summary>Controls how this agent is exposed as an invocable tool to peer agents.</summary>
internal sealed class AgentToolExposureDefinition
{
    /// <summary>When true this agent is visible as a callable tool to permitted callers.</summary>
    public bool ExposedAsTool { get; init; }

    /// <summary>Tool description shown to caller agents. Max 200 characters, enforced at save time.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Seconds before an individual invocation is cancelled. Default 60.</summary>
    public int InvocationTimeoutSeconds { get; init; } = 60;
}

internal sealed class AgentSessionDefinition
{
    public bool HistoryEnabled { get; init; } = true;

    public int MaxHistoryRuns { get; init; } = 5;

    public bool SummariesEnabled { get; init; } = true;

    public string? SummaryModelId { get; init; }

    public bool MemoryFactExtractionEnabled { get; init; } = true;
}

internal sealed class AgentSandboxDefinition
{
    public bool Enabled { get; init; } = true;

    public Collection<string> AllowedLanguages { get; init; } = [];

    public string MaxMemory { get; init; } = "2g";

    public int MaxTimeout { get; init; } = 120;
}
