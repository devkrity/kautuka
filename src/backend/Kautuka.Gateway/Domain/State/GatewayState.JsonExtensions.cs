using System.Text.Json.Nodes;

namespace Kautuka.Gateway.Domain.State;

internal static class JsonObjectSettingsExtensions
{
    public static bool GetBoolNodeValue(this JsonObject obj, string key, bool fallback)
    {
        if (!obj.TryGetPropertyValue(key, out JsonNode? node) || node is null)
        {
            return fallback;
        }

        try
        {
            return node.GetValue<bool>();
        }
        catch (InvalidOperationException)
        {
            if (bool.TryParse(node.ToString(), out bool result))
            {
                return result;
            }

            return fallback;
        }
        catch (FormatException)
        {
            if (bool.TryParse(node.ToString(), out bool result))
            {
                return result;
            }

            return fallback;
        }
    }

    public static int GetIntNodeValue(this JsonObject obj, string key, int fallback)
    {
        if (!obj.TryGetPropertyValue(key, out JsonNode? node) || node is null)
        {
            return fallback;
        }

        try
        {
            return node.GetValue<int>();
        }
        catch (InvalidOperationException)
        {
            if (int.TryParse(node.ToString(), out int result))
            {
                return result;
            }

            return fallback;
        }
        catch (FormatException)
        {
            if (int.TryParse(node.ToString(), out int result))
            {
                return result;
            }

            return fallback;
        }
    }

    public static string GetStringNodeValue(this JsonObject obj, string key, string fallback)
    {
        if (!obj.TryGetPropertyValue(key, out JsonNode? node) || node is null)
        {
            return fallback;
        }

        try
        {
            return node.GetValue<string>() ?? fallback;
        }
        catch (InvalidOperationException)
        {
            return node.ToString() ?? fallback;
        }
        catch (FormatException)
        {
            return node.ToString() ?? fallback;
        }
    }

    public static string? GetStringNodeValue(this JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out JsonNode? node) || node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<string>();
        }
        catch (InvalidOperationException)
        {
            string s = node.ToString();
            return string.IsNullOrEmpty(s) ? null : s;
        }
        catch (FormatException)
        {
            string s = node.ToString();
            return string.IsNullOrEmpty(s) ? null : s;
        }
    }

    public static List<string> GetStringArrayNodeValue(this JsonObject obj, string key)
    {
        if (!obj.TryGetPropertyValue(key, out JsonNode? node) || node is null)
        {
            return [];
        }

        if (node is JsonArray arr)
        {
            var result = new List<string>(arr.Count);
            foreach (JsonNode? item in arr)
            {
                string? s = item?.GetValue<string>();
                if (s is not null)
                {
                    result.Add(s);
                }
            }

            return result;
        }

        return [];
    }
}
