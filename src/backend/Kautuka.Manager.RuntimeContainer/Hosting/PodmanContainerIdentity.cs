using System.Text.Json;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

internal static class PodmanContainerIdentity
{
    internal static bool TryReadCanonicalLaunchOutput(string? output, out string containerId)
    {
        string value = output?.Trim() ?? string.Empty;
        if (IsCanonicalContainerId(value))
        {
            containerId = value;
            return true;
        }

        containerId = string.Empty;
        return false;
    }

    internal static bool TryRead(string json, out string containerId, out string workloadIp)
    {
        containerId = string.Empty;
        workloadIp = string.Empty;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement container = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray().FirstOrDefault()
                : document.RootElement;
            if (container.ValueKind != JsonValueKind.Object
                || !container.TryGetProperty("Id", out JsonElement idElement)
                || idElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            string? id = idElement.GetString();
            if (!IsCanonicalContainerId(id))
            {
                return false;
            }

            containerId = id!;
            if (!container.TryGetProperty("NetworkSettings", out JsonElement networkSettings)
                || !networkSettings.TryGetProperty("Networks", out JsonElement networks)
                || !networks.TryGetProperty("kautuka-workload", out JsonElement workloadNetwork)
                || !workloadNetwork.TryGetProperty("IPAddress", out JsonElement ipElement)
                || ipElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(ipElement.GetString()))
            {
                return true;
            }

            workloadIp = ipElement.GetString()!;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool IsCanonicalContainerId(string? containerId)
        => containerId is { Length: 64 } && containerId.All(Uri.IsHexDigit);
}
