using System.Globalization;
using System.Text.Json.Nodes;

namespace Kautuka.Gateway.Infrastructure.AI;

internal static class AiProviderSettings
{
    public static readonly TimeSpan DefaultHttpTimeout = TimeSpan.FromSeconds(300);

    public static string? GetString(JsonObject settings, string key)
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

    public static TimeSpan GetTimeout(JsonObject settings, string key, TimeSpan defaultValue)
    {
        string? rawValue = GetString(settings, key);
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return defaultValue;
        }

        if (!double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
            || seconds <= 0)
        {
            return defaultValue;
        }

        return TimeSpan.FromSeconds(seconds);
    }
}
