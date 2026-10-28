using System.Text.Json;

namespace Kautuka.Gateway.Serialization;

internal static class GatewayRuntimePayloadGuard
{
    private static readonly HashSet<string> removedRuntimeFields = new(StringComparer.Ordinal)
    {
        "plannedStepId",
        "deviationReason",
        "unplanned",
    };

    public static string ValidatePersistedJson(string json, string context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(context);
        if (string.IsNullOrWhiteSpace(json))
        {
            return json;
        }

        using JsonDocument document = JsonDocument.Parse(json);
        Scan(document.RootElement, "$", context);
        return json;
    }

    private static void Scan(JsonElement element, string path, string context)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    ValidatePropertyName(property.Name, $"{path}.{property.Name}", context);
                    Scan(property.Value, $"{path}.{property.Name}", context);
                }

                break;
            case JsonValueKind.Array:
                int index = 0;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    Scan(item, $"{path}[{index}]", context);
                    index++;
                }

                break;
        }
    }

    private static void ValidatePropertyName(string propertyName, string path, string context)
    {
        if (removedRuntimeFields.Contains(propertyName))
        {
            throw new InvalidOperationException($"{context} contains removed runtime field '{propertyName}' at {path}.");
        }

        if (IsUnprefixedAdapterField(propertyName))
        {
            throw new InvalidOperationException($"{context} contains unprefixed adapter field '{propertyName}' at {path}.");
        }
    }

    private static bool IsUnprefixedAdapterField(string propertyName)
        => propertyName.StartsWith("agui", StringComparison.OrdinalIgnoreCase)
            && !propertyName.StartsWith("agui_", StringComparison.OrdinalIgnoreCase)
            || propertyName.StartsWith("copilotkit", StringComparison.OrdinalIgnoreCase)
            && !propertyName.StartsWith("copilotkit_", StringComparison.OrdinalIgnoreCase);
}
