using System.Net;
using System.Net.Sockets;

namespace Kautuka.Hosting.Hosting;

/// <summary>Common IPv4 destination classification for appliance enforcement and its user-facing checks.</summary>
public static class KautukaNetworkDestinationPolicy
{
    public const string PrivateSourceAddress = "192.0.0.8";
    public const string PlatformControlAddress = "168.63.129.16";
    public const string PublicSpecialSet = "192.0.0.9, 192.0.0.10";
    // IMDS plus container credential endpoints. Do not block the adjacent AWS DNS resolver.
    public const string MetadataSet = "169.254.169.254, 169.254.170.0/24, 100.100.100.200";
    public const string NonPublicSet = "0.0.0.0/8, 10.0.0.0/8, 100.64.0.0/10, 127.0.0.0/8, 169.254.0.0/16, 172.16.0.0/12, 192.0.0.0/24, 192.0.2.0/24, 192.88.99.0/24, 192.168.0.0/16, 198.18.0.0/15, 198.51.100.0/24, 203.0.113.0/24, 224.0.0.0/4, 240.0.0.0/4";

    public static bool IsMetadata(string destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        return destination.TrimEnd('.').Equals("metadata.google.internal", StringComparison.OrdinalIgnoreCase) || Matches(destination, MetadataSet);
    }
    public static bool IsNonPublic(string destination) => destination == PlatformControlAddress || Matches(destination, NonPublicSet) && !Matches(destination, PublicSpecialSet);

    private static bool Matches(string destination, string ranges)
    {
        if (!IPAddress.TryParse(destination, out IPAddress? ip) || ip.AddressFamily != AddressFamily.InterNetwork) return false;
        return ranges.Split(", ", StringSplitOptions.None).Any(range => IPNetwork.Parse(range.Contains('/', StringComparison.Ordinal) ? range : range + "/32").Contains(ip));
    }
}
