using System.Text.Json;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

public sealed partial class ContainerEgressRuntime
{
    private async Task EnsurePrivateSourceAsync(CancellationToken ct)
    {
        const string name = "kautuka-private";
        PodmanCommandResult links = await HelperAsync("/usr/sbin/ip", ["-j", "-d", "link", "show"], ct).ConfigureAwait(false);
        using JsonDocument parsed = JsonDocument.Parse(links.StdOut);
        JsonElement[] existing = [.. parsed.RootElement.EnumerateArray().Where(item => item.GetProperty("ifname").GetString() == name)];
        if (existing.Length == 0)
        {
            _ = await HelperAsync("/usr/sbin/ip", ["link", "add", name, "type", "dummy"], ct, networkAdmin: true).ConfigureAwait(false);
        }
        else if (existing.Length != 1 || !existing[0].TryGetProperty("linkinfo", out JsonElement info)
            || info.GetProperty("info_kind").GetString() != "dummy")
            throw new InvalidOperationException("The private upstream source interface is not the managed dummy interface.");

        PodmanCommandResult addresses = await HelperAsync("/usr/sbin/ip", ["-j", "-4", "address", "show", "dev", name], ct).ConfigureAwait(false);
        using JsonDocument addressDocument = JsonDocument.Parse(addresses.StdOut);
        JsonElement[] assigned = [.. addressDocument.RootElement.EnumerateArray().SelectMany(item => item.GetProperty("addr_info").EnumerateArray())];
        if (assigned.Length == 0)
            _ = await HelperAsync("/usr/sbin/ip", ["address", "add", NetworkDestinationPolicy.PrivateSourceAddress + "/32", "dev", name], ct, networkAdmin: true).ConfigureAwait(false);
        else if (assigned.Length != 1 || assigned[0].GetProperty("local").GetString() != NetworkDestinationPolicy.PrivateSourceAddress
            || assigned[0].GetProperty("prefixlen").GetInt32() != 32)
            throw new InvalidOperationException("The private upstream source address differs from the fixed managed identity.");
        _ = await HelperAsync("/usr/sbin/ip", ["link", "set", name, "up"], ct, networkAdmin: true).ConfigureAwait(false);
    }
}
