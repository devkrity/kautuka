using System.Text.Json;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

// Validate complete Podman and host-route inventories before allocating any /24.
internal static class NetworkPoolInventory
{
    internal static HashSet<int> Validate(NetworkAddressPool pool, JsonElement podmanNetworks, JsonElement routes)
    {
        if (podmanNetworks.ValueKind != JsonValueKind.Array || routes.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Cannot verify network pool availability because the network inventory is invalid.");
        var slots = new HashSet<int>();
        var ownedRoutes = new HashSet<(string Subnet, string Interface)>();
        var ownedLocalRoutes = new HashSet<(string Address, string Interface)>();
        foreach (JsonElement network in podmanNetworks.EnumerateArray())
        {
            string name = network.GetProperty("name").GetString() ?? throw new InvalidOperationException("A Podman network has no name.");
            bool owned = network.TryGetProperty("labels", out JsonElement labels) && labels.ValueKind == JsonValueKind.Object
                && labels.TryGetProperty("kautuka.egress", out JsonElement owner) && owner.GetString() == "true";
            JsonElement subnets = network.GetProperty("subnets");
            if (subnets.ValueKind == JsonValueKind.Null && !owned) continue;
            if (subnets.ValueKind != JsonValueKind.Array || owned && subnets.GetArrayLength() != 1)
                throw new InvalidOperationException("Cannot verify the subnets of Podman network " + name + ".");
            foreach (JsonElement entry in subnets.EnumerateArray())
            {
                string subnet = entry.GetProperty("subnet").GetString() ?? throw new InvalidOperationException("A Podman subnet is missing its CIDR.");
                if (subnet.Contains(':', StringComparison.Ordinal))
                {
                    if (owned) throw new InvalidOperationException("Managed egress networks must use an IPv4 subnet.");
                    continue; // IPv6 cannot overlap an IPv4 pool.
                }
                if (!owned)
                {
                    if (pool.Overlaps(subnet)) throw new InvalidOperationException("The configured egress pool overlaps Podman network " + name + " (" + subnet + "). Choose a different pool.");
                    continue;
                }

                string? scope = labels.GetProperty("kautuka.egress.scope").GetString();
                if (!pool.TryGetSlot(subnet, out int slot) || !slots.Add(slot)
                    || network.GetProperty("driver").GetString() != "bridge"
                    || network.GetProperty("internal").GetBoolean() != (name != "kautuka-outside")
                    || network.GetProperty("dns_enabled").GetBoolean() || network.GetProperty("ipv6_enabled").GetBoolean()
                    || name == "kautuka-outside" && (scope != "outside" || slot != 0)
                    || name == "kautuka-core" && (scope != "core" || slot != 1)
                    || name is not ("kautuka-outside" or "kautuka-core") && (slot < 2 || scope is null || scope.Length != 64
                        || !scope.All(char.IsAsciiHexDigit) || name != "kautuka-" + scope[..32])
                    || !entry.TryGetProperty("gateway", out JsonElement gateway) || gateway.GetString() != pool.Gateway(slot))
                    throw new InvalidOperationException("Existing managed network " + name + " does not match the configured pool or has a duplicate slot. Configure its original pool; automatic migration is not supported.");
                string? iface = network.GetProperty("network_interface").GetString();
                if (string.IsNullOrWhiteSpace(iface)) throw new InvalidOperationException("Managed network " + name + " has no verifiable interface.");
                ownedRoutes.Add((subnet, iface));
                string networkAddress = subnet[..subnet.IndexOf('/', StringComparison.Ordinal)];
                string baseAddress = networkAddress[..(networkAddress.LastIndexOf('.') + 1)];
                ownedLocalRoutes.Add((baseAddress + "0/32", iface));
                ownedLocalRoutes.Add((baseAddress + "1/32", iface));
                ownedLocalRoutes.Add((baseAddress + "255/32", iface));
            }
        }
        foreach (JsonElement route in routes.EnumerateArray())
        {
            string? destination = route.TryGetProperty("dst", out JsonElement dst) ? dst.GetString() : null;
            if (destination == "default" || destination == "0.0.0.0/0") continue;
            if (string.IsNullOrWhiteSpace(destination)) throw new InvalidOperationException("A host route has no destination.");
            string cidr = destination.Contains('/', StringComparison.Ordinal) ? destination : destination + "/32";
            if (!pool.Overlaps(cidr)) continue;
            string? device = route.TryGetProperty("dev", out JsonElement dev) ? dev.GetString() : null;
            bool noNextHop = !route.TryGetProperty("gateway", out _) && !route.TryGetProperty("nexthops", out _)
                && !route.TryGetProperty("via", out _) && !route.TryGetProperty("encap", out _);
            bool direct = noNextHop
                && (!route.TryGetProperty("type", out JsonElement type) || type.GetString() == "unicast");
            bool ownedLocal = noNextHop && device is not null && route.TryGetProperty("type", out JsonElement localType)
                && (localType.GetString() is "local" or "broadcast") && ownedLocalRoutes.Contains((cidr, device));
            if (!ownedLocal && (!direct || device is null || !ownedRoutes.Contains((cidr, device))))
                throw new InvalidOperationException("The configured egress pool overlaps host or VPN route " + cidr + ". Choose a different pool.");
        }
        return slots;
    }
}
