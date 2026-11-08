using System.Net;
using System.Net.Sockets;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

internal static class HostDnsUpstreams
{
    // systemd's loopback stub cannot be reached from the isolated egress namespace.
    internal static string[] Resolve(string resolver, string? systemdResolver)
    {
        (string[] addresses, bool systemdStub) = Read(resolver);
        if (addresses.Length == 0 && systemdStub && systemdResolver is not null) (addresses, _) = Read(systemdResolver);
        if (addresses.Length == 0)
            throw new InvalidOperationException("The host has no usable IPv4 DNS upstreams.");
        NetworkDestinationPolicy.ValidateDnsUpstreams(addresses);
        return addresses;
    }

    private static (string[] Addresses, bool SystemdStub) Read(string text)
    {
        var result = new List<string>();
        bool systemdStub = false;
        foreach (string line in text.Split('\n'))
        {
            string content = line.Split('#', 2)[0].Split(';', 2)[0].Trim();
            string[] fields = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0 || fields[0] != "nameserver") continue;
            if (fields.Length != 2 || !IPAddress.TryParse(fields[1], out IPAddress? address))
                throw new InvalidOperationException("The host DNS resolver file contains an invalid nameserver.");
            if (address.Equals(IPAddress.Parse("127.0.0.53"))) systemdStub = true;
            if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)
                || address.Equals(IPAddress.Any) || address.GetAddressBytes()[0] >= 224) continue;
            string normalized = address.ToString();
            if (!result.Contains(normalized, StringComparer.Ordinal)) result.Add(normalized);
        }
        return ([.. result], systemdStub);
    }
}
