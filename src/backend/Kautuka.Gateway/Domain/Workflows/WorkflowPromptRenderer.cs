using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Kautuka.Gateway.Domain.Workflows;

internal static partial class WorkflowPromptRenderer
{
    [GeneratedRegex(@"\{\{\s*([^}]+?)\s*\}\}", RegexOptions.CultureInvariant)]
    private static partial Regex PlaceholderPattern();

    public static string Render(string template, JsonObject envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (string.IsNullOrWhiteSpace(template))
        {
            return envelope.ToJsonString();
        }

        string rendered = PlaceholderPattern().Replace(template, match =>
        {
            string token = match.Groups[1].Value.Trim();
            string pointer = ToPointer(token);
            JsonNode? value = WorkflowJsonPointer.Get(envelope, pointer);
            return value is null ? string.Empty : Unwrap(value);
        });

        return rendered;
    }

    public static string RenderStep(GatewayWorkflowNode node, JsonObject envelope, bool governed = false)
    {
        ArgumentNullException.ThrowIfNull(node);
        JsonObject promptEnvelope = governed ? ProjectDeclared(node, envelope) : envelope;
        string rendered = Render(node.PromptTemplate, promptEnvelope);
        if (!string.Equals(node.OutputKind, "schema", StringComparison.OrdinalIgnoreCase))
        {
            return rendered;
        }

        string schemaJson = node.OutputSchema?.ToJsonString() ?? """{"type":"object"}""";
        return $"""
            {rendered}

            Current working data:
            {promptEnvelope.ToJsonString()}

            Reply with a JSON object only. Match this schema exactly. No markdown, no extra keys, no commentary.
            {schemaJson}
            """;
    }

    private static JsonObject ProjectDeclared(GatewayWorkflowNode node, JsonObject envelope)
    {
        JsonObject projected = [];
        foreach (GatewayWorkflowMap map in node.Maps)
        {
            projected = Copy(envelope, projected, map.From);
            projected = Copy(envelope, projected, map.To);
        }

        foreach (Match match in PlaceholderPattern().Matches(node.PromptTemplate ?? string.Empty))
        {
            projected = Copy(envelope, projected, ToPointer(match.Groups[1].Value.Trim()));
        }

        return projected;
    }

    private static JsonObject Copy(JsonObject source, JsonObject destination, string pointer)
    {
        if (string.IsNullOrWhiteSpace(pointer))
        {
            return destination;
        }

        JsonNode? value = WorkflowJsonPointer.Get(source, pointer);
        return value is null ? destination : WorkflowJsonPointer.Set(destination, pointer, value.DeepClone());
    }

    private static string ToPointer(string token)
    {
        if (token.StartsWith('/'))
        {
            return token;
        }

        string trimmed = token;
        if (trimmed.StartsWith("input.", StringComparison.Ordinal)
            || trimmed.StartsWith("working.", StringComparison.Ordinal)
            || trimmed.StartsWith("output.", StringComparison.Ordinal))
        {
            trimmed = trimmed[(trimmed.IndexOf('.', StringComparison.Ordinal) + 1)..];
        }

        return "/" + trimmed.Replace('.', '/');
    }

    private static string Unwrap(JsonNode value)
        => value is JsonValue jsonValue && jsonValue.TryGetValue(out string? text)
            ? text ?? string.Empty
            : value.ToJsonString();
}
