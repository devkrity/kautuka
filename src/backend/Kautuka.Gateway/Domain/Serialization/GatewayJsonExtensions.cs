using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kautuka.Gateway.Domain.Serialization;

internal static class GatewayJsonExtensions
{
    public static string GetStringProperty(this JsonElement element, string propertyName, string fallback)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? fallback,
            JsonValueKind.Null => fallback,
            _ => value.ToString() ?? fallback,
        };
    }

    public static string? GetNullableStringProperty(this JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        string text = value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.ToString() ?? string.Empty;
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    public static bool GetBoolProperty(this JsonElement element, string propertyName, bool fallback)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(value.GetString(), out bool parsed) ? parsed : fallback,
            _ => fallback,
        };
    }

    public static int GetIntProperty(this JsonElement element, string propertyName, int fallback)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int direct))
        {
            return direct;
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out int parsed))
        {
            return parsed;
        }

        return fallback;
    }

    public static double GetDoubleProperty(this JsonElement element, string propertyName, double fallback)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double direct))
        {
            return direct;
        }

        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double parsed))
        {
            return parsed;
        }

        return fallback;
    }

    public static string[] GetStringArrayProperty(this JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            return [];
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            return [.. value.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!.Trim())];
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return [.. value.GetString()!
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(item => !string.IsNullOrWhiteSpace(item))];
        }

        return [];
    }

    public static JsonObject GetObjectProperty(this JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value) || value.ValueKind != JsonValueKind.Object)
        {
            return new JsonObject();
        }

        return JsonNode.Parse(value.GetRawText()) as JsonObject ?? new JsonObject();
    }
}
