using System.Net;
using System.Net.Sockets;

namespace Kautuka.OciHook;

/// <summary>Pure policy decision, independent of namespace and filesystem operations.</summary>
internal static class HookPolicy
{
    internal sealed record State(string Id, int Pid, string? Role = null, string? ImageId = null);
    internal sealed record Infrastructure(string? Role, IReadOnlyList<string> ExpectedIpv4,
        IReadOnlyList<IReadOnlyList<string>>? Alternates = null, string? ImageDigest = null);
    internal sealed record Decision(bool Allowed, string Reason);

    public static Decision Decide(State? state, IReadOnlyList<string>? ips,
        IReadOnlyDictionary<string, Infrastructure> allowlist,
        IReadOnlyDictionary<string, string?> owners, string? inode,
        bool allowlistValid = true, string? allowlistReason = null,
        bool ownersValid = true, string? ownersReason = null)
    {
        static Decision Refuse(string reason) => new(false, reason);
        static Decision Allow(string reason) => new(true, reason);
        if (state is null || state.Pid <= 0) return Refuse("missing_pid");
        if (ips is null || ips.Count == 0) return Refuse("no_ip");
        if (!ips.Any(static ip => ip.StartsWith("10.247.", StringComparison.Ordinal) || ip == "10.247"))
        {
            if (state.Role == "workload")
                return Refuse(ownersValid ? "workload_off_network" : ownersReason ?? "owners_unreadable");
            return Allow("outside_boundary");
        }
        if (!allowlistValid) return Refuse(allowlistReason ?? "missing_allowlist");
        if (allowlist.TryGetValue(state.Id, out Infrastructure? entry))
        {
            if (!ValidSet(entry.ExpectedIpv4) || !ValidSet(ips)) return Refuse("infra_ip_mismatch");
            bool matches = SameSet(ips, entry.ExpectedIpv4);
            if (entry.Alternates is not null)
            {
                if (entry.Role != "egress-boundary" || entry.Alternates.Count != 1 ||
                    !SameSet(entry.ExpectedIpv4, ["10.247.0.2", "10.248.0.2"]))
                    return Refuse("infra_ip_mismatch");
                foreach (IReadOnlyList<string> alternate in entry.Alternates)
                {
                    if (!ValidSet(alternate) || !SameSet(alternate, ["10.247.0.2", "10.248.0.2", "10.248.0.3"]))
                        return Refuse("infra_ip_mismatch");
                    matches |= SameSet(ips, alternate);
                }
            }
            if (!matches) return Refuse("infra_ip_mismatch");
            if (entry.Role != "pkgs-proxy") return Allow("infra_allowlisted");
            if (entry.ExpectedIpv4.Count != 1 || entry.ExpectedIpv4[0] != "10.247.0.3" ||
                state.Role != "package-proxy" || entry.ImageDigest is null || state.ImageId != entry.ImageDigest)
                return Refuse("package_proxy_identity_mismatch");
            return Allow("package_proxy_rules");
        }
        if (!ownersValid) return Refuse(ownersReason ?? "owners_unreadable");
        if (ips.Any(static ip => ip.StartsWith("10.247.0.", StringComparison.Ordinal) && ip.Split('.').Length == 4))
            return Refuse("reserved_range");
        if (ips.Count != 1) return Refuse("bad_workload_ip");
        string[] parts = ips[0].Split('.');
        if (parts.Length != 4 || !int.TryParse(parts[0], out int p0) || !int.TryParse(parts[1], out int p1) ||
            !int.TryParse(parts[2], out int p2) || !int.TryParse(parts[3], out int p3) ||
            p0 != 10 || p1 != 247 || p2 is < 1 or > 255 || p3 is < 0 or > 254)
            return Refuse("bad_workload_ip");
        if (inode is null) return Refuse("missing_netns_inode");
        if (owners.TryGetValue(inode, out string? owner) && owner != state.Id) return Refuse("shared_netns");
        return Allow("workload_rules");
    }

    private static bool ValidSet(IReadOnlyList<string> ips) => ips.Count > 0 &&
        ips.All(static ip => IPAddress.TryParse(ip, out IPAddress? parsed) &&
            parsed.AddressFamily == AddressFamily.InterNetwork && parsed.ToString() == ip) &&
        ips.Distinct(StringComparer.Ordinal).Count() == ips.Count;

    private static bool SameSet(IReadOnlyList<string> first, IReadOnlyList<string> second) =>
        first.Count == second.Count && first.ToHashSet(StringComparer.Ordinal).SetEquals(second);
}
