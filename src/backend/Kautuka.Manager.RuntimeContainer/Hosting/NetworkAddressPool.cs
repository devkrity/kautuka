using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

// A pool is divided into /24s. Podman owns address allocation within each subnet.
internal readonly record struct NetworkAddressPool(uint Start, int Prefix)
{
    internal int SlotCount => 1 << (24 - Prefix);

    internal static NetworkAddressPool Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !TryParseCidr(value, out uint address, out int prefix) || prefix is < 8 or > 19
            || address != (address & Mask(prefix)) || !IsPrivate(address, prefix))
            throw new InvalidOperationException("The container egress network pool must be a canonical RFC1918 IPv4 CIDR of /19 or larger. Configure a non-overlapping pool; existing networks are not migrated automatically.");
        return new NetworkAddressPool(address, prefix);
    }

    internal string Subnet(int slot)
    {
        if (slot < 0 || slot >= SlotCount) throw new ArgumentOutOfRangeException(nameof(slot));
        return Format(Start + ((uint)slot << 8)) + "/24";
    }

    internal string Gateway(int slot)
    {
        if (slot < 0 || slot >= SlotCount) throw new ArgumentOutOfRangeException(nameof(slot));
        return Format(Start + ((uint)slot << 8) + 1);
    }

    internal bool TryGetSlot(string cidr, out int slot)
    {
        slot = -1;
        if (!TryParseCidr(cidr, out uint address, out int prefix) || prefix != 24 || (address & 255) != 0
            || (address & Mask(Prefix)) != Start) return false;
        slot = (int)((address - Start) >> 8);
        return slot < SlotCount;
    }

    internal bool Overlaps(string cidr)
    {
        if (!TryParseCidr(cidr, out uint address, out int prefix))
            throw new InvalidOperationException("Cannot validate an invalid IPv4 network route or Podman subnet: " + cidr);
        int common = Math.Min(prefix, Prefix);
        return (address & Mask(common)) == (Start & Mask(common));
    }

    internal void ValidateResolvers(IReadOnlyList<string> resolvers)
    {
        foreach (string resolver in resolvers)
            if (Overlaps(resolver + "/32"))
                throw new InvalidOperationException("A configured DNS upstream overlaps the managed container egress network pool.");
    }

    private static bool IsPrivate(uint address, int prefix)
        => prefix >= 8 && (address & 0xff000000U) == 0x0a000000U
        || prefix >= 12 && (address & 0xfff00000U) == 0xac100000U
        || prefix >= 16 && (address & 0xffff0000U) == 0xc0a80000U;

    private static bool TryParseCidr(string value, out uint address, out int prefix)
    {
        address = 0;
        prefix = 0;
        string[] parts = value.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out IPAddress? ip)
            || ip.AddressFamily != AddressFamily.InterNetwork
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out prefix)
            || prefix is < 0 or > 32) return false;
        address = BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes());
        return parts[0] == Format(address) && parts[1] == prefix.ToString(CultureInfo.InvariantCulture);
    }

    private static uint Mask(int prefix) => prefix == 0 ? 0 : uint.MaxValue << (32 - prefix);

    private static string Format(uint address)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, address);
        return new IPAddress(bytes).ToString();
    }
}
