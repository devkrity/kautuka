using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kautuka.Gateway.Domain.Stores;

internal sealed partial class GatewayCatalogStore
{
    private static Dictionary<string, object?> ToSettingsDictionary(JsonObject settings)
        => JsonSerializer.Deserialize(settings.ToJsonString(), KautukaJsonContext.Default.DictionaryStringObject)
            ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    private static GatewayModelCapabilities? TryReadModelCapabilities(JsonElement body)
    {
        if (!body.TryGetProperty("capabilities", out JsonElement capabilitiesElement)
            || capabilitiesElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return JsonSerializer.Deserialize(capabilitiesElement.GetRawText(), KautukaJsonContext.Default.GatewayModelCapabilities);
    }

    internal static AgentToolExposureDefinition ParseToolExposure(JsonObject toolExposure)
    {
        bool exposed = toolExposure.GetBoolNodeValue("exposedAsTool", false);
        string description = toolExposure["description"]?.GetValue<string>() ?? string.Empty;
        int timeoutSeconds = toolExposure.GetIntNodeValue("invocationTimeoutSeconds", 60);

        return new AgentToolExposureDefinition
        {
            ExposedAsTool = exposed,
            Description = description,
            InvocationTimeoutSeconds = timeoutSeconds,
        };
    }
}
