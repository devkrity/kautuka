using System.Text.Json.Serialization;

namespace Kautuka.Control.Core.Api;

// ── Agents ────────────────────────────────────────────────────────────────────

/// <summary>Matches GatewayAgentListItem wire shape (camelCase).</summary>
public sealed record AgentSummary
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string ModelId { get; init; } = string.Empty;
    public string SystemPrompt { get; init; } = string.Empty;
    public string CreatedAt { get; init; } = string.Empty;
    // TUI compat — gateway has no IsEnabled flag
    [JsonIgnore]
    public bool IsEnabled
    {
        get
        {
            _ = Id;
            return true;
        }
    }
}

/// <summary>Gateway wraps agent list: { "agents": [...] }.</summary>
public sealed record AgentsResponse
{
    public IReadOnlyList<AgentSummary> Agents { get; init; } = Array.Empty<AgentSummary>();
}

// ── Providers & Models ────────────────────────────────────────────────────────

/// <summary>Matches GatewayProviderListItem (id, type, hasApiKey, createdAt).</summary>
public sealed record ProviderSummary
{
    public string Id { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public bool HasApiKey { get; init; }
    public string CreatedAt { get; init; } = string.Empty;
    // TUI compat aliases
    [JsonIgnore]
    public string Name
    {
        get
        {
            _ = Id;
            return Id;
        }
    }
    [JsonIgnore]
    public string Kind
    {
        get
        {
            _ = Type;
            return Type;
        }
    }
}

public sealed record ProvidersResponse
{
    public IReadOnlyList<ProviderSummary> Providers { get; init; } = Array.Empty<ProviderSummary>();
}

/// <summary>Matches GatewayModelListItem (id, providerId, modelName, createdAt).</summary>
public sealed record ModelSummary
{
    public string Id { get; init; } = string.Empty;
    public string ProviderId { get; init; } = string.Empty;
    public string ModelName { get; init; } = string.Empty;
    public string CreatedAt { get; init; } = string.Empty;
    // TUI compat alias
    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            _ = ModelName;
            return ModelName;
        }
    }
}

public sealed record ModelsResponse
{
    public IReadOnlyList<ModelSummary> Models { get; init; } = Array.Empty<ModelSummary>();
}

// ── Users ─────────────────────────────────────────────────────────────────────

/// <summary>Matches GatewayUserListItem.</summary>
public sealed record UserSummary
{
    public string Id { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string? Email { get; init; }
    public string? DisplayName { get; init; }
    public string Role { get; init; } = string.Empty;
    public bool IsActive { get; init; }
}

public sealed record UsersResponse
{
    public IReadOnlyList<UserSummary> Users { get; init; } = Array.Empty<UserSummary>();
}

// ── Sessions ──────────────────────────────────────────────────────────────────

/// <summary>Matches GatewaySession (id, targetType, targetId, title, createdAt, updatedAt).</summary>
public sealed record SessionSummary
{
    public string Id { get; init; } = string.Empty;
    public string TargetType { get; init; } = string.Empty;
    public string TargetId { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string CreatedAt { get; init; } = string.Empty;
    public string UpdatedAt { get; init; } = string.Empty;
    // TUI compat aliases
    [JsonIgnore]
    public string AgentId
    {
        get
        {
            _ = TargetId;
            return TargetId;
        }
    }
    [JsonIgnore]
    public int? MessageCount
    {
        get
        {
            _ = Id;
            return null;
        }
    }
}

public sealed record SessionsResponse
{
    public IReadOnlyList<SessionSummary> Sessions { get; init; } = Array.Empty<SessionSummary>();
}

// ── Approvals ─────────────────────────────────────────────────────────────────

/// <summary>Matches ApprovalResponse wire DTO (flat array from /api/approvals/pending).</summary>
public sealed record ApprovalSummary
{
    public string Id { get; init; } = string.Empty;
    public string? AgentId { get; init; }
    public string ToolName { get; init; } = string.Empty;
    public string ToolType { get; init; } = string.Empty;
    public string RequestedAt { get; init; } = string.Empty;
    public string? Decision { get; init; }
    public string? OuterSessionId { get; init; }
    public string? OuterAgentId { get; init; }
    // TUI compat aliases
    [JsonIgnore]
    public string Kind
    {
        get
        {
            _ = ToolType;
            return ToolType;
        }
    }
    [JsonIgnore]
    public string CreatedAt
    {
        get
        {
            _ = RequestedAt;
            return RequestedAt;
        }
    }
}

// ── System ────────────────────────────────────────────────────────────────────

/// <summary>Matches GatewaySystemSettingsResponse: { system: {...}, kautuka: {...} }.</summary>
public sealed record SystemSettings
{
    public SystemPayload? System { get; init; }
    public KautukaPayload? Kautuka { get; init; }
    // TUI compat — these fields don't exist in the gateway response
    [JsonIgnore]
    public string? PlatformName
    {
        get
        {
            _ = System;
            return null;
        }
    }
    [JsonIgnore]
    public bool? AllowRegistration
    {
        get
        {
            _ = Kautuka;
            return null;
        }
    }
}

public sealed record SystemPayload
{
    public string LogLevel { get; init; } = string.Empty;
    public RuntimeLimitsPayload? RuntimeLimits { get; init; }
}

public sealed record RuntimeLimitsPayload
{
    public int ModelTimeoutSeconds { get; init; }
    public int MaxRunDurationSeconds { get; init; }
    public int HitlExpirySeconds { get; init; }
    public string HitlExpiryAction { get; init; } = "abort";
    public bool AllowMultipleToolCalls { get; init; } = true;
    public bool AllowConcurrentInvocation { get; init; }
    public int MaxParallelTools { get; init; }
}

public sealed record KautukaPayload
{
    public WorkspacePayload? Workspace { get; init; }
}

public sealed record WorkspacePayload
{
    public string BaseRoot { get; init; } = string.Empty;
}

// ── Auth ──────────────────────────────────────────────────────────────────────

public sealed record AuthMe
{
    public string Id { get; init; } = string.Empty;
    public string Username { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
}

