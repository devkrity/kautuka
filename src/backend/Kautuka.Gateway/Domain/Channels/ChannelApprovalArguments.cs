using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kautuka.Gateway.Domain.Channels;

internal static class ChannelApprovalArguments
{
    private const int maximumCharacters = 1800;

    public static string? Sanitize(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return null;

        JsonObject? arguments;
        try
        {
            arguments = JsonSerializer.Deserialize(argumentsJson, KautukaJsonContext.Default.JsonObject);
        }
        catch (JsonException)
        {
            return "{\"notice\":\"Arguments omitted because they were not valid JSON.\"}";
        }

        RedactSensitiveProperties(arguments);
        string serialized = arguments is null ? "null" : JsonSerializer.Serialize(arguments, KautukaJsonContext.Default.JsonObject);
        if (serialized.Length <= maximumCharacters) return serialized;

        string preview = serialized[..Math.Min(serialized.Length, maximumCharacters - 128)];
        while (preview.Length > 0)
        {
            var cappedObject = new JsonObject { ["truncated"] = true, ["preview"] = preview };
            string capped = JsonSerializer.Serialize(cappedObject, KautukaJsonContext.Default.JsonObject);
            if (capped.Length <= maximumCharacters) return capped;
            preview = preview[..Math.Max(0, preview.Length - 64)];
        }

        return "{\"truncated\":true}";
    }

    public static JsonNode? SanitizeForDisplay(JsonNode? value)
    {
        JsonNode? clone = value?.DeepClone();
        RedactSensitiveProperties(clone);
        return clone;
    }

    private static void RedactSensitiveProperties(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach ((string key, JsonNode? value) in obj.ToArray())
            {
                if (IsSensitiveProperty(key)) obj[key] = "[redacted]";
                else RedactSensitiveProperties(value);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (JsonNode? item in array) RedactSensitiveProperties(item);
        }
    }

    internal static bool IsSensitiveProperty(string key)
    {
        string normalized = string.Concat(key.Where(char.IsLetterOrDigit)).ToUpperInvariant();
        return normalized.Contains("PASSWORD", StringComparison.Ordinal)
            || normalized.Contains("PASSWD", StringComparison.Ordinal)
            || normalized.Contains("SECRET", StringComparison.Ordinal)
            || normalized.Contains("TOKEN", StringComparison.Ordinal)
            || normalized.Contains("CREDENTIAL", StringComparison.Ordinal)
            || normalized.Contains("AUTHORIZATION", StringComparison.Ordinal)
            || normalized.Contains("COOKIE", StringComparison.Ordinal)
            || normalized.Contains("APIKEY", StringComparison.Ordinal)
            || normalized.Contains("ACCESSKEY", StringComparison.Ordinal)
            || normalized.Contains("PRIVATEKEY", StringComparison.Ordinal)
            || normalized.Contains("EMAIL", StringComparison.Ordinal)
            || normalized.Contains("PHONE", StringComparison.Ordinal)
            || normalized.Contains("ADDRESS", StringComparison.Ordinal)
            || normalized.Contains("SSN", StringComparison.Ordinal)
            || normalized.Contains("TAXID", StringComparison.Ordinal)
            || normalized.Contains("DATEOFBIRTH", StringComparison.Ordinal)
            || normalized.Contains("CREDITCARD", StringComparison.Ordinal)
            || normalized.Contains("ACCOUNTNUMBER", StringComparison.Ordinal);
    }
}
