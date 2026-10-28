using System.Text.Json.Nodes;

namespace Kautuka.Gateway.Domain.Stores;

internal static class GatewayPersonaProfileMapper
{
    private const double defaultTemperature = 0.7d;
    private static readonly string[] identityColors = ["#0F766E", "#2563EB", "#7C3AED", "#DB2777", "#EA580C", "#16A34A"];

    public static JsonObject CreateDefaultDefinitions()
        => new()
        {
            ["styles"] = CreateStringArray("professional", "helper", "analytical"),
            ["tones"] = CreateStringArray("neutral", "friendly", "formal", "assertive", "empathetic"),
            ["verbosity_levels"] = CreateStringArray("concise", "normal", "detailed"),
        };

    public static JsonObject BuildDefaultProfile(JsonObject definitions)
    {
        string style = FirstOrFallback(definitions["styles"] as JsonArray, "professional");
        string tone = FirstOrFallback(definitions["tones"] as JsonArray, "neutral");
        string verbosity = FirstOrFallback(definitions["verbosity_levels"] as JsonArray, "concise");

        return new JsonObject
        {
            ["temperature"] = defaultTemperature,
            ["style"] = style,
            ["tone"] = tone,
            ["verbosity"] = verbosity,
            ["icon"] = "🤖",
            ["color"] = "#0F766E",
        };
    }

    public static JsonObject ValidateAndNormalizeDefinitions(JsonObject definitions)
    {
        JsonArray styles = ReadRequiredStringArray(definitions, "styles");
        JsonArray tones = ReadRequiredStringArray(definitions, "tones");
        JsonArray verbosityLevels = ReadRequiredStringArray(definitions, "verbosity_levels");

        return new JsonObject
        {
            ["styles"] = styles,
            ["tones"] = tones,
            ["verbosity_levels"] = verbosityLevels,
        };
    }

    public static JsonObject ValidateAndNormalizeProfile(JsonObject profile)
    {
        double temperature = ReadTemperature(profile);
        string style = ReadRequiredString(profile, "style");
        string tone = ReadRequiredString(profile, "tone");
        string verbosity = ReadRequiredString(profile, "verbosity");

        JsonObject normalized = new()
        {
            ["temperature"] = temperature,
            ["style"] = style,
            ["tone"] = tone,
            ["verbosity"] = verbosity,
            ["icon"] = NormalizeIdentityIcon(profile["icon"]?.GetValue<string>()),
            ["color"] = ReadIdentityValue(profile, "color", identityColors, "#0F766E"),
        };

        return normalized;
    }

    private static string ReadIdentityValue(JsonObject profile, string propertyName, string[] allowed, string fallback)
    {
        string? value = profile[propertyName]?.GetValue<string>()?.Trim();
        return value is not null && allowed.Contains(value, StringComparer.Ordinal) ? value : fallback;
    }

    internal static string NormalizeIdentityIcon(string? value)
    {
        string icon = value?.Trim() ?? string.Empty;
        return icon.Length is > 0 and <= 16
            && !icon.Any(char.IsControl)
            && System.Globalization.StringInfo.ParseCombiningCharacters(icon).Length == 1
                ? icon
                : "🤖";
    }

    public static JsonObject NormalizeStoredProfile(JsonObject? profile, JsonObject definitions)
    {
        if (profile is null)
        {
            return BuildDefaultProfile(definitions);
        }

        return ValidateAndNormalizeProfile(profile);
    }

    private static JsonArray CreateStringArray(params string[] values)
    {
        JsonArray array = new();
        foreach (string value in values)
        {
            array.Add((JsonNode?)JsonValue.Create(value));
        }

        return array;
    }

    private static JsonArray ReadRequiredStringArray(JsonObject source, string propertyName)
    {
        if (source[propertyName] is not JsonArray array)
        {
            throw new ArgumentException($"Persona definitions {propertyName} must be an array of strings.");
        }

        JsonArray normalized = new();
        foreach (JsonNode? item in array)
        {
            if (item is null)
            {
                continue;
            }

            if (item is not JsonValue)
            {
                throw new ArgumentException($"Persona definitions {propertyName} entries must be strings.");
            }

            string? value = item.GetValue<string>()?.Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                normalized.Add((JsonNode?)JsonValue.Create(value));
            }
        }

        if (normalized.Count == 0)
        {
            throw new ArgumentException($"Persona definitions {propertyName} must contain at least one value.");
        }

        return normalized;
    }

    private static string ReadRequiredString(JsonObject source, string propertyName)
    {
        if (source[propertyName] is not JsonValue value)
        {
            throw new ArgumentException($"Persona profile {propertyName} is required.");
        }

        string? parsed = value.GetValue<string>()?.Trim();
        if (string.IsNullOrWhiteSpace(parsed))
        {
            throw new ArgumentException($"Persona profile {propertyName} is required.");
        }

        return parsed;
    }
    private static string? ReadOptionalString(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        string? value = node.GetValue<string>();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string FirstOrFallback(JsonArray? array, string fallback)
    {
        if (array is null || array.Count == 0)
        {
            return fallback;
        }

        string? first = array[0]?.GetValue<string>();
        return string.IsNullOrWhiteSpace(first) ? fallback : first;
    }

    private static double ReadTemperature(JsonObject profile)
    {
        if (profile["temperature"] is JsonValue temperatureValue)
        {
            try
            {
                double parsed = temperatureValue.GetValue<double>();
                return Math.Clamp(parsed, 0.0d, 1.0d);
            }
            catch (InvalidOperationException)
            {
                throw new ArgumentException("Persona profile temperature must be a number.");
            }
            catch (FormatException)
            {
                throw new ArgumentException("Persona profile temperature must be a number.");
            }
        }

        return defaultTemperature;
    }
}
