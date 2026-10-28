using System.Text.Json.Nodes;

namespace Kautuka.Gateway.Domain.Workflows;

internal static class WorkflowStepOutputParser
{
    public static JsonObject Parse(string response, GatewayWorkflowNode node, bool governed, out IReadOnlyList<string> droppedKeys)
    {
        ArgumentNullException.ThrowIfNull(node);
        droppedKeys = [];
        string trimmed = StripFence(response ?? string.Empty).Trim();
        if (string.Equals(node.OutputKind, "text", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonObject { ["text"] = trimmed };
        }

        if (governed)
        {
            if (!TryParseExact(trimmed, out JsonObject? exact) || exact is null)
            {
                throw new WorkflowSchemaException("Step output was not JSON.");
            }

            if (node.OutputSchema is not null)
            {
                WorkflowSchemaSubset.ValidateInstance(node.OutputSchema, exact);
            }

            return exact;
        }

        if (!TryParseObject(trimmed, out JsonObject? parsed) || parsed is null)
        {
            throw new WorkflowSchemaException("Step output was not JSON.");
        }

        if (node.OutputSchema is not null)
        {
            JsonObject aligned = AlignToSchema(parsed, node.OutputSchema, out List<string> dropped);
            droppedKeys = dropped;
            WorkflowSchemaSubset.ValidateInstance(node.OutputSchema, aligned);
            return aligned;
        }

        return parsed;
    }

    public static JsonObject Parse(string response, GatewayWorkflowNode node)
        => Parse(response, node, governed: false, out _);

    private static JsonObject AlignToSchema(JsonObject parsed, JsonObject schema, out List<string> droppedKeys)
    {
        droppedKeys = [];
        if (schema["properties"] is not JsonObject properties)
        {
            return parsed;
        }

        JsonObject aligned = [];
        foreach ((string name, JsonNode? value) in parsed)
        {
            if (properties.ContainsKey(name))
            {
                aligned[name] = value?.DeepClone();
            }
            else
            {
                droppedKeys.Add(name);
            }
        }

        return aligned;
    }

    private static bool TryParseObject(string text, out JsonObject? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (TryParseExact(text, out parsed))
        {
            return true;
        }

        for (int start = 0; start < text.Length; start++)
        {
            if (text[start] != '{')
            {
                continue;
            }

            int end = FindMatchingBrace(text, start);
            if (end < 0)
            {
                continue;
            }

            if (TryParseExact(text[start..(end + 1)], out parsed))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseExact(string text, out JsonObject? parsed)
    {
        parsed = null;
        try
        {
            parsed = JsonNode.Parse(text) as JsonObject;
            return parsed is not null;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static int FindMatchingBrace(string text, int start)
    {
        int depth = 0;
        bool inString = false;
        bool escaped = false;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (c == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }

                    break;
            }
        }

        return -1;
    }

    private static string StripFence(string text)
    {
        string trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        int firstNewline = trimmed.IndexOf('\n', StringComparison.Ordinal);
        if (firstNewline < 0)
        {
            return trimmed;
        }

        string withoutOpen = trimmed[(firstNewline + 1)..];
        int close = withoutOpen.LastIndexOf("```", StringComparison.Ordinal);
        return close >= 0 ? withoutOpen[..close] : withoutOpen;
    }
}
