using System.Text.Json.Nodes;

namespace Kautuka.Gateway.Infrastructure.Configuration;

internal static class AgentDefinitionRevisionDiffer
{
    public static IReadOnlyList<AgentDefinitionSettingChange> Diff(
        IReadOnlyList<AgentDefinitionSettingSnapshot> before,
        IReadOnlyList<AgentDefinitionSettingSnapshot> after)
    {
        Dictionary<string, AgentDefinitionSettingSnapshot> beforeById = before
            .ToDictionary(static setting => setting.SettingId, StringComparer.Ordinal);
        Dictionary<string, AgentDefinitionSettingSnapshot> afterById = after
            .ToDictionary(static setting => setting.SettingId, StringComparer.Ordinal);

        var changes = new List<AgentDefinitionSettingChange>();
        foreach (string settingId in beforeById.Keys.Union(afterById.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            beforeById.TryGetValue(settingId, out AgentDefinitionSettingSnapshot? previous);
            afterById.TryGetValue(settingId, out AgentDefinitionSettingSnapshot? next);
            JsonNode? beforeNode = Parse(previous?.ValueJson);
            JsonNode? afterNode = Parse(next?.ValueJson);
            if (JsonCanonicalEquality.Equals(beforeNode, afterNode))
            {
                continue;
            }

            changes.Add(new AgentDefinitionSettingChange(
                settingId,
                next?.SettingName ?? previous?.SettingName ?? settingId,
                beforeNode,
                afterNode));
        }

        return changes;
    }

    private static JsonNode? Parse(string? valueJson)
    {
        if (string.IsNullOrWhiteSpace(valueJson))
        {
            return null;
        }

        return JsonNode.Parse(valueJson);
    }
}

internal static class JsonCanonicalEquality
{
    public static bool Equals(JsonNode? left, JsonNode? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        if (left is JsonObject leftObject && right is JsonObject rightObject)
        {
            if (leftObject.Count != rightObject.Count)
            {
                return false;
            }

            foreach ((string key, JsonNode? value) in leftObject)
            {
                if (!rightObject.TryGetPropertyValue(key, out JsonNode? other) || !Equals(value, other))
                {
                    return false;
                }
            }

            return true;
        }

        if (left is JsonArray leftArray && right is JsonArray rightArray)
        {
            if (leftArray.Count != rightArray.Count)
            {
                return false;
            }

            for (int index = 0; index < leftArray.Count; index++)
            {
                if (!Equals(leftArray[index], rightArray[index]))
                {
                    return false;
                }
            }

            return true;
        }

        return string.Equals(left.ToJsonString(), right.ToJsonString(), StringComparison.Ordinal);
    }
}
