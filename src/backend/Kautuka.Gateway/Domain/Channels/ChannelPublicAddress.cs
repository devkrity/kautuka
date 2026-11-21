using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Domain.Channels;

internal static class ChannelPublicAddress
{
    internal const string SettingKey = "channels.public_address";
    internal static bool TryNormalize(string? value, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || uri.AbsolutePath != "/") return false;
        normalized = uri.GetLeftPart(UriPartial.Authority);
        return true;
    }

    internal static string? Read(IGatewaySettingsStore? settings)
    {
        string? json = settings?.ReadSettingJson(SettingKey);
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            string? value = JsonNode.Parse(json)?["address"]?.GetValue<string>();
            return TryNormalize(value, out string? normalized) ? normalized : null;
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { return null; }
    }

    internal static string NetworkRequestMessage(string? address)
        => TryNormalize(address, out string? normalized) && normalized is not null
            ? $"A network request was blocked. Review it in Network Access: {normalized}/network-access"
            : "A network request was blocked. Ask your administrator to configure a public Kautuka address, then open Network Access to review the request.";
}
