using System.Text.Json;
using System.Text.RegularExpressions;

namespace Kautuka.Gateway.Domain.Memory;

internal sealed class JsonMemoryResponseParser
{
    private enum JsonScanState
    {
        Normal,
        InString,
    }

    private const string memoriesProperty = "memories";

    private static readonly Regex jsonCodeFenceRegex = new(
        "```(?:json)?\\s*(?<json>[\\s\\S]*?)\\s*```",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ILogger<JsonMemoryResponseParser> logger;

    public JsonMemoryResponseParser(ILogger<JsonMemoryResponseParser> logger)
    {
        this.logger = logger;
    }

    public bool TryGetMemoriesArray(string responseText, string targetType, string targetId, out JsonElement memories)
    {
        memories = default;

        foreach (string candidate in EnumerateJsonCandidates(responseText))
        {
            if (!TryParseRootElement(candidate, out JsonElement root))
            {
                continue;
            }

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty(memoriesProperty, out JsonElement array)
                && array.ValueKind == JsonValueKind.Array)
            {
                memories = array;
                return true;
            }
        }

        JsonMemoryResponseParserLog.InvalidMemoriesArray(logger, targetType, targetId, BuildPreview(responseText));
        return false;
    }

    private static IEnumerable<string> EnumerateJsonCandidates(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);

        string trimmed = text.Trim();
        if (seen.Add(trimmed))
        {
            yield return trimmed;
        }

        foreach (Match match in jsonCodeFenceRegex.Matches(trimmed))
        {
            if (!match.Groups["json"].Success)
            {
                continue;
            }

            string fencedJson = match.Groups["json"].Value.Trim();
            if (!string.IsNullOrEmpty(fencedJson) && seen.Add(fencedJson))
            {
                yield return fencedJson;
            }
        }

        if (TryExtractFirstBalancedJson(trimmed, out string extracted) && seen.Add(extracted))
        {
            yield return extracted;
        }
    }

    private static bool TryParseRootElement(string json, out JsonElement root)
    {
        root = default;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            root = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryExtractFirstBalancedJson(string text, out string json)
    {
        json = string.Empty;

        int objectStart = text.IndexOf('{', StringComparison.Ordinal);
        int arrayStart = text.IndexOf('[', StringComparison.Ordinal);
        int start = objectStart >= 0 && arrayStart >= 0
            ? Math.Min(objectStart, arrayStart)
            : Math.Max(objectStart, arrayStart);

        if (start < 0)
        {
            return false;
        }

        if (!TryFindBalancedJsonEnd(text, start, out int end))
        {
            return false;
        }

        json = text[start..(end + 1)].Trim();
        return true;
    }

    private static bool TryFindBalancedJsonEnd(string text, int start, out int end)
    {
        end = -1;
        if (start < 0 || start >= text.Length)
        {
            return false;
        }

        char open = text[start];
        if (open != '{' && open != '[')
        {
            return false;
        }

        char close = open == '{' ? '}' : ']';
        int depth = 0;
        JsonScanState state = JsonScanState.Normal;
        bool escaped = false;

        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];

            if (state == JsonScanState.InString && TryProcessStringCharacter(c, ref state, ref escaped))
            {
                continue;
            }

            switch (c)
            {
                case '"':
                    state = JsonScanState.InString;
                    continue;

                case var _ when c == open:
                    depth++;
                    continue;

                case var _ when c == close && --depth == 0:
                    end = i;
                    return true;

                case var _ when c == close:
                    continue;
            }
        }

        return false;
    }

    private static bool TryProcessStringCharacter(char c, ref JsonScanState state, ref bool escaped)
    {
        if (state != JsonScanState.InString)
        {
            return false;
        }

        if (escaped)
        {
            escaped = false;
            return true;
        }

        switch (c)
        {
            case '\\':
                escaped = true;
                return true;

            case '"':
                state = JsonScanState.Normal;
                return true;

            default:
                return true;
        }
    }

    private static string BuildPreview(string text)
    {
        const int maxLen = 180;
        string normalized = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= maxLen ? normalized : string.Concat(normalized.AsSpan(0, maxLen), "...");
    }
}

internal static class JsonElementMemoryExtensions
{
    public static string GetStringProperty(this JsonElement element, string name, string fallback)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return fallback;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : value.ToString() ?? fallback;
    }

    public static double GetDoubleProperty(this JsonElement element, string name, double fallback)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double parsed))
        {
            return parsed;
        }

        return fallback;
    }
}

internal static partial class JsonMemoryResponseParserLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "FactExtract model response did not contain a valid memories array for {TargetType}:{TargetId} (preview={Preview})")]
    public static partial void InvalidMemoriesArray(ILogger logger, string targetType, string targetId, string preview);
}
