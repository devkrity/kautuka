using System.Text.Json.Nodes;

namespace Narad.Contracts;

/// <summary>
/// Minimal protected actor provenance for Narad authority events.
/// Never includes user identifiers; callers attach only resolution channel/source.
/// </summary>
public static class NaradProtectedActorProvenance
{
    public static JsonObject FromSource(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        return new JsonObject { ["source"] = source.Trim() };
    }
}
