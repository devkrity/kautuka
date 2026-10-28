using System.Text.Json.Nodes;

namespace Kautuka.Gateway.Domain.Autopilot;

internal static class AutopilotWorkflowOutput
{
    public static string? Serialize(JsonNode? output)
    {
        if (output is null)
        {
            return null;
        }

        if (output is JsonValue value
            && value.TryGetValue<string>(out string? text)
            && string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string json = output.ToJsonString();
        return string.IsNullOrWhiteSpace(json) ? null : json;
    }
}
