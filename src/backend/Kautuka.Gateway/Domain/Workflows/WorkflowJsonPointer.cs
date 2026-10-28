using System.Text.Json.Nodes;

namespace Kautuka.Gateway.Domain.Workflows;

/// <summary>
/// Restricted RFC 6901 JSON Pointer. Empty pointer is the document root.
/// The array-append token "-" is rejected so maps are pure copy, not mutation of list tails.
/// </summary>
internal static class WorkflowJsonPointer
{
    public static JsonNode? Get(JsonNode? root, string pointer)
    {
        ArgumentNullException.ThrowIfNull(pointer);
        IReadOnlyList<string> tokens = Parse(pointer);
        JsonNode? current = root;
        foreach (string token in tokens)
        {
            current = Step(current, token, missingOk: true);
            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    public static JsonObject Set(JsonObject root, string pointer, JsonNode? value)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(pointer);

        IReadOnlyList<string> tokens = Parse(pointer);
        JsonObject clone = JsonNode.Parse(root.ToJsonString())!.AsObject();
        if (tokens.Count == 0)
        {
            throw new WorkflowMapException("JSON Pointer maps cannot replace the document root.");
        }

        JsonNode current = clone;
        for (int i = 0; i < tokens.Count - 1; i++)
        {
            JsonNode? next = Step(current, tokens[i], missingOk: false);
            if (next is null)
            {
                throw new WorkflowMapException($"JSON Pointer '{pointer}' does not resolve.");
            }

            current = next;
        }

        Assign(current, tokens[^1], value?.DeepClone());
        return clone;
    }

    public static IReadOnlyList<string> Parse(string pointer)
    {
        if (pointer.Length == 0)
        {
            return [];
        }

        if (pointer[0] != '/')
        {
            throw new WorkflowMapException("JSON Pointer must be empty or start with '/'.");
        }

        string[] raw = pointer.Split('/');
        var tokens = new List<string>(raw.Length - 1);
        for (int i = 1; i < raw.Length; i++)
        {
            string token = Unescape(raw[i]);
            if (token == "-")
            {
                throw new WorkflowMapException("JSON Pointer array-append token '-' is not allowed.");
            }

            tokens.Add(token);
        }

        return tokens;
    }

    private static JsonNode? Step(JsonNode? current, string token, bool missingOk)
    {
        switch (current)
        {
            case JsonObject obj:
                if (obj.TryGetPropertyValue(token, out JsonNode? child))
                {
                    return child;
                }

                if (missingOk)
                {
                    return null;
                }

                throw new WorkflowMapException($"JSON Pointer token '{token}' was not found.");
            case JsonArray array:
                if (!int.TryParse(token, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index)
                    || index < 0
                    || index >= array.Count)
                {
                    if (missingOk)
                    {
                        return null;
                    }

                    throw new WorkflowMapException($"JSON Pointer array index '{token}' is out of range.");
                }

                return array[index];
            default:
                if (missingOk)
                {
                    return null;
                }

                throw new WorkflowMapException("JSON Pointer walked into a scalar.");
        }
    }

    private static void Assign(JsonNode parent, string token, JsonNode? value)
    {
        switch (parent)
        {
            case JsonObject obj:
                obj[token] = value;
                return;
            case JsonArray array:
                if (!int.TryParse(token, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index)
                    || index < 0
                    || index >= array.Count)
                {
                    throw new WorkflowMapException($"JSON Pointer array index '{token}' is out of range.");
                }

                array[index] = value;
                return;
            default:
                throw new WorkflowMapException("JSON Pointer cannot assign into a scalar.");
        }
    }

    private static string Unescape(string token) => token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
}

internal sealed class WorkflowMapException : InvalidOperationException
{
    public WorkflowMapException()
    {
    }

    public WorkflowMapException(string message)
        : base(message)
    {
    }

    public WorkflowMapException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
