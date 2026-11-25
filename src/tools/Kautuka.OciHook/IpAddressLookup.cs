using System.Text.Json;

namespace Kautuka.OciHook;

/// <summary>Parses IPv4 addresses from the hook's ip -j -4 addr show response.</summary>
internal static class IpAddressLookup
{
    internal static List<string> Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid IP address response.");
        var addresses = new List<string>();
        foreach (JsonElement iface in document.RootElement.EnumerateArray())
        {
            if (iface.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid interface.");
            if (!iface.TryGetProperty("addr_info", out JsonElement info)) continue;
            if (info.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid interface addresses.");
            foreach (JsonElement address in info.EnumerateArray())
            {
                if (address.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid interface address.");
                if (!address.TryGetProperty("local", out JsonElement local) || local.ValueKind == JsonValueKind.Null) continue;
                if (local.ValueKind != JsonValueKind.String) throw new InvalidDataException("Invalid local address.");
                string value = local.GetString()!;
                if (value.Length != 0 && value != "127.0.0.1") addresses.Add(value);
            }
        }
        return addresses;
    }
}
