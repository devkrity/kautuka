using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kautuka.Gateway.Domain.State;

internal sealed partial class GatewayState
{
    internal string ComputeTopologyHashFromState()
    {
        var payload = new TopologyHashPayload(
            Agents.Values
                .OrderBy(static a => a.Id, StringComparer.Ordinal)
                .Select(static a => new AgentTopologyHashEntry(a.Id, a.Scope, a.Enabled, a.ModelId, a.SystemPrompt, a.SuperExecMode, a.Session, a.Sandbox))
                .ToArray());

        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload, KautukaJsonContext.Default.TopologyHashPayload)));
    }

    internal static JsonObject PreserveSecretSetting(JsonObject snapshotSettings, JsonObject baselineSettings, string key)
    {
        string? snapshotValue = GetStringNodeValue(snapshotSettings, key);
        if (!string.IsNullOrWhiteSpace(snapshotValue) && !string.Equals(snapshotValue, "null", StringComparison.OrdinalIgnoreCase))
        {
            return snapshotSettings;
        }

        string? baselineValue = GetStringNodeValue(baselineSettings, key);
        if (string.IsNullOrWhiteSpace(baselineValue) || string.Equals(baselineValue, "null", StringComparison.OrdinalIgnoreCase))
        {
            return snapshotSettings;
        }

        var merged = snapshotSettings.DeepClone() as JsonObject ?? new JsonObject();
        merged[key] = baselineValue;
        return merged;
    }

    internal sealed record TopologyHashPayload(AgentTopologyHashEntry[] Agents);

    internal sealed record AgentTopologyHashEntry(
        string Id,
        string Scope,
        bool Enabled,
        string ModelId,
        string SystemPrompt,
        string SuperExecMode,
        JsonObject Session,
        JsonObject Sandbox);

    private static string? GetStringNodeValue(JsonObject settings, string key)
    {
        if (!settings.TryGetPropertyValue(key, out JsonNode? node) || node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<string>();
        }
        catch (InvalidOperationException)
        {
            return node.ToString();
        }
        catch (FormatException)
        {
            return node.ToString();
        }
    }

    internal static string Timestamp() => DateTimeOffset.UtcNow.ToString("O");
}
