namespace Kautuka.Gateway.Infrastructure.Configuration;

/// <summary>
/// Transport type for an MCP server.
/// </summary>
internal enum McpTransport
{
    /// <summary>HTTP+SSE transport — server runs at a URL.</summary>
    Http,

    /// <summary>Container transport — server runs as a Kautuka.Manager-managed sidecar container.</summary>
    Container,
}

/// <summary>
/// Represents an MCP server registration in the Kautuka topology.
/// </summary>
internal sealed class McpServerDefinition
{
    /// <summary>Unique identifier (slug). E.g. "github-mcp".</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Human-readable display name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Optional description shown in the Connectors UI.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Optional icon URL for the Connectors app-store card.</summary>
    public Uri? IconUrl { get; init; }

    /// <summary>The well-known preset identifier, if this was seeded from a preset. Null for custom servers.</summary>
    public string? PresetId { get; init; }

    /// <summary>Transport type.</summary>
    public McpTransport Transport { get; init; } = McpTransport.Http;

    // ── HTTP transport ──────────────────────────────────────────────────────

    /// <summary>Base URL for HTTP/SSE transport. Null for container connectors.</summary>
    public Uri? HttpUrl { get; init; }

    // ── Container transport ───────────────────────────────────────────────

    /// <summary>Container image reference for containerized MCP connectors.</summary>
    public string? ContainerImage { get; init; }

    /// <summary>Optional command override used when launching the container.</summary>
    public string? ContainerCommand { get; init; }

    /// <summary>Optional arguments passed to the container command.</summary>
    public IReadOnlyList<string> ContainerArgs { get; init; } = [];

    /// <summary>Optional deterministic sidecar name override. If null, derived from connector id.</summary>
    public string? ContainerSidecarName { get; init; }

    /// <summary>Host path to the stored .mcpb archive for MCPB-backed container connectors. Null for non-bundle connectors.</summary>
    public string? BundlePath { get; init; }

    /// <summary>Runtime server type from the MCPB manifest (node, python, uv, binary). Null for non-bundle connectors.</summary>
    public string? ServerType { get; init; }

    /// <summary>Derived runtime artifact type for imported bundle-backed connectors. Null for non-bundle connectors.</summary>
    public string? RuntimeArtifactType { get; init; }

    /// <summary>Source bundle digest used to derive the runtime artifact. Null for non-bundle connectors.</summary>
    public string? RuntimeArtifactSourceDigest { get; init; }

    /// <summary>Artifact build status recorded by Gateway after import-time validation.</summary>
    public string? RuntimeArtifactBuildStatus { get; init; }

    /// <summary>Optional readiness timeout override in seconds.</summary>
    public int? ReadinessTimeoutSeconds { get; init; }

    /// <summary>Optional JIT runtime cooldown override in seconds.</summary>
    public int? JitCooldownSeconds { get; init; }

    /// <summary>
    /// MCP protocol version to request. Null keeps the client automatic handshake.
    /// </summary>
    public string? McpProtocolVersion { get; init; }

    /// <summary>Optional container health check URL.</summary>
    public Uri? HealthcheckUrl { get; init; }

    /// <summary>Optional health check poll interval in milliseconds.</summary>
    public int? HealthcheckIntervalMilliseconds { get; init; }

    // ── Secrets ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Declared environment variable key names for this connector.
    /// Used by the UI to render credential input forms and field labels.
    /// Shape: ["API_KEY", "API_SECRET", "BASE_URL"]
    /// </summary>
    public IReadOnlyList<string> EnvKeys { get; init; } = [];

    // ── Metadata ────────────────────────────────────────────────────────────

    public string CreatedAt { get; init; } = string.Empty;
    public string UpdatedAt { get; init; } = string.Empty;

    /// <summary>Whether the server is enabled for use in agent executions.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Marks a system-owned connector whose lifecycle is controlled by the product.</summary>
    public bool IsInternalSystemConnector { get; init; }

    /// <summary>Ownership scope for this server registration.</summary>
    public string Scope { get; init; } = Kautuka.Gateway.Domain.Models.GatewayOwnershipScopes.System;

    /// <summary>Owner user id when <see cref="Scope"/> is user-scoped.</summary>
    public string? OwnerUserId { get; init; }

    /// <summary>Last known tool count, populated when an admin runs Test Connection. Null if never tested.</summary>
    public int? ToolCount { get; init; }

    /// <summary>Last known tool names, populated when an admin runs Test Connection. Null if never tested.</summary>
    public IReadOnlyList<string>? ToolNames { get; init; }
}
