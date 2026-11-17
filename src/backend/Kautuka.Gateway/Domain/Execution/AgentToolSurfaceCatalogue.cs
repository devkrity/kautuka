using System.Text.Json;

using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Domain.Execution;

internal static class AgentToolSurfaceSources
{
    public const string Mcp = "mcp";
    public const string Shell = "shell";
    public const string Skill = "skill";
    public const string Aaat = "aaat";
}

internal sealed class AgentToolSurfaceCatalogue
{
    public List<AgentToolSurfaceEntry> Entries { get; set; } = [];

    public List<string> UnavailableConnectors { get; set; } = [];
}

internal sealed class AgentToolSurfaceEntry
{
    public string Source { get; set; } = string.Empty;

    public string SourceId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string JsonSchema { get; set; } = "{}";

    public string? ArtifactDigest { get; set; }

    public string? ImageDigest { get; set; }

    public string? ExecutionConfigHash { get; set; }

    public string? EffectiveEndpointHash { get; set; }

    internal static AgentToolSurfaceEntry From(string source, string sourceId, AIFunction tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return Describe(source, sourceId, tool.Name, tool.Description ?? string.Empty, tool.JsonSchema);
    }

    internal static AgentToolSurfaceEntry Describe(
        string source,
        string sourceId,
        string name,
        string description,
        JsonElement schema)
        => Describe(source, sourceId, name, description, SchemaText(schema));

    internal static AgentToolSurfaceEntry Describe(
        string source,
        string sourceId,
        string name,
        string description,
        string? jsonSchema)
        => new()
        {
            Source = source,
            SourceId = sourceId,
            Name = name,
            Description = description,
            JsonSchema = string.IsNullOrWhiteSpace(jsonSchema) ? "{}" : jsonSchema,
        };

    private static string SchemaText(JsonElement schema)
        => schema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? "{}"
            : schema.GetRawText();
}

internal readonly record struct AgentToolSurfaceContribution(
    IReadOnlyList<AgentToolSurfaceEntry> Entries,
    bool Complete = true,
    IReadOnlyList<string>? UnavailableConnectors = null);

internal sealed record AgentToolSurfaceSnapshot(
    string Fingerprint,
    AgentToolSurfaceCatalogue Catalogue,
    string BuiltAt);
