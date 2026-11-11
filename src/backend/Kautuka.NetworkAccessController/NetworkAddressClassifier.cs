using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Kautuka.NetworkAccessController;

public readonly record struct CidrRange(string CanonicalCidr, string AddressPrefix, int PrefixLength, IPNetwork Network);

/// <summary>
/// Single canonical IPv4 destination classifier (<c>NonPublicV4</c>) shared across
/// rule validation, <c>not_public</c> decision checks, Envoy <c>resolved_address_filter</c>
/// rendering, and kernel nft <c>private_reject</c> rendering (verified by U8).
/// </summary>
public static class NetworkAddressClassifier
{
    public const string SinkholeAddress = "198.18.250.250";
    public const string SinkholeCidr = "198.18.0.0/15";
    public const string WorkloadSubnetCidr = "10.247.0.0/16";
    public const string LanSourceBindAddress = "192.0.0.8";
    public const string AzureWireServerAddress = "168.63.129.16";
    public const string AwsImdsAddress = "169.254.169.254";
    public const string AlibabaMetadataAddress = "100.100.100.200";

    private static readonly string[] canonicalNonPublicV4Cidrs =
    [
        "10.0.0.0/8",
        "172.16.0.0/12",
        "192.168.0.0/16",
        "100.64.0.0/10",
        "127.0.0.0/8",
        "169.254.0.0/16",
        "0.0.0.0/8",
        "192.0.0.0/24",
        "198.18.0.0/15",
        "224.0.0.0/3",
        "168.63.129.16/32",
    ];

    private static readonly string[] rfc1918Cidrs =
    [
        "10.0.0.0/8",
        "172.16.0.0/12",
        "192.168.0.0/16",
    ];

    private static readonly string[] metadataCidrs =
    [
        "169.254.0.0/16",
        "168.63.129.16/32",
        "100.100.100.200/32",
    ];

    public static IReadOnlyList<CidrRange> NonPublicV4 { get; } = ParseCidrs(canonicalNonPublicV4Cidrs);

    public static IReadOnlyList<string> NonPublicV4CanonicalCidrs => canonicalNonPublicV4Cidrs;

    public static IReadOnlyList<CidrRange> Rfc1918Ranges { get; } = ParseCidrs(rfc1918Cidrs);

    public static IReadOnlyList<CidrRange> MetadataRanges { get; } = ParseCidrs(metadataCidrs);

    public static CidrRange WorkloadSubnet { get; } = ParseSingle(WorkloadSubnetCidr);

    public static CidrRange SinkholeRange { get; } = ParseSingle(SinkholeCidr);

    public static IReadOnlyList<string> GetRuleValidationNonPublicRanges()
        => [.. NonPublicV4.Select(static r => r.CanonicalCidr)];

    public static IReadOnlyList<string> GetNotPublicDecisionRanges()
        => [.. NonPublicV4.Select(static r => r.CanonicalCidr)];

    public static bool IsNonPublicV4(string ipAddress)
        => TryParseIpv4(ipAddress, out IPAddress? ip) && IsNonPublicV4(ip!);

    public static bool IsNonPublicV4(IPAddress ipAddress)
    {
        ArgumentNullException.ThrowIfNull(ipAddress);
        if (ipAddress.AddressFamily != AddressFamily.InterNetwork)
        {
            return true;
        }

        foreach (CidrRange range in NonPublicV4)
        {
            if (range.Network.Contains(ipAddress))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsMetadata(string destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        string trimmed = destination.Trim().TrimEnd('.');
        if (string.Equals(trimmed, "metadata.google.internal", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "metadata", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!TryParseIpv4(trimmed, out IPAddress? ip))
        {
            return false;
        }

        foreach (CidrRange range in MetadataRanges)
        {
            if (range.Network.Contains(ip!))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsWorkloadSubnet(string ipAddress)
        => TryParseIpv4(ipAddress, out IPAddress? ip) && WorkloadSubnet.Network.Contains(ip!);

    public static bool IsSinkholeRange(string ipAddress)
        => TryParseIpv4(ipAddress, out IPAddress? ip) && SinkholeRange.Network.Contains(ip!);

    public static bool IsRfc1918(string ipAddress)
    {
        if (!TryParseIpv4(ipAddress, out IPAddress? ip))
        {
            return false;
        }

        foreach (CidrRange range in Rfc1918Ranges)
        {
            if (range.Network.Contains(ip!))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsRfc1918AllowableWithLan(string ipAddress)
        => IsRfc1918(ipAddress) && !IsWorkloadSubnet(ipAddress);

    /// <summary>
    /// Renders the Envoy <c>resolved_address_filter</c> YAML snippet for <see cref="NonPublicV4"/>.
    /// In Envoy v1.40.2, <c>resolved_address_filter</c> is a direct <c>envoy.type.matcher.v3.AddressMatcher</c>
    /// field using <c>ranges:</c> without <c>@type</c>.
    /// </summary>
    public static string RenderEnvoyResolvedAddressFilterYaml(IEnumerable<CidrRange>? ranges = null)
    {
        IReadOnlyList<CidrRange> effective = ranges is null ? NonPublicV4 : [.. ranges];
        var sb = new StringBuilder();
        sb.AppendLine("resolved_address_filter:");
        sb.AppendLine("  ranges:");
        foreach (CidrRange range in effective)
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"    - address_prefix: {range.AddressPrefix}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"      prefix_len: {range.PrefixLength}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Extracts canonical CIDR strings from a rendered Envoy <c>resolved_address_filter</c> YAML snippet.
    /// </summary>
    public static IReadOnlyList<string> ExtractRangesFromEnvoyFilterYaml(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        var results = new List<string>();
        string? currentPrefix = null;
        string[] lines = yaml.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.StartsWith("- address_prefix:", StringComparison.Ordinal))
            {
                currentPrefix = line["- address_prefix:".Length..].Trim().Trim('"', '\'');
            }
            else if (line.StartsWith("address_prefix:", StringComparison.Ordinal))
            {
                currentPrefix = line["address_prefix:".Length..].Trim().Trim('"', '\'');
            }
            else if (line.StartsWith("prefix_len:", StringComparison.Ordinal) && currentPrefix is not null)
            {
                string lenText = line["prefix_len:".Length..].Trim();
                if (int.TryParse(lenText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int prefixLen))
                {
                    results.Add($"{currentPrefix}/{prefixLen.ToString(CultureInfo.InvariantCulture)}");
                    currentPrefix = null;
                }
            }
            else if (results.Count > 0 && currentPrefix is null && !line.StartsWith('#'))
            {
                break;
            }
        }

        return results;
    }

    /// <summary>
    /// Renders the kernel nft <c>private_reject</c> rule covering all of <see cref="NonPublicV4"/>.
    /// </summary>
    public static string RenderNftPrivateRejectRule(IEnumerable<CidrRange>? ranges = null)
    {
        IReadOnlyList<CidrRange> effective = ranges is null ? NonPublicV4 : [.. ranges];
        string joined = string.Join(", ", effective.Select(static r => r.CanonicalCidr));
        return $"ip daddr {{ {joined} }} counter reject comment \"private_reject\"";
    }

    /// <summary>
    /// Extracts canonical CIDR strings from a rendered nft <c>private_reject</c> rule line.
    /// </summary>
    public static IReadOnlyList<string> ExtractRangesFromNftPrivateReject(string nftRule)
    {
        ArgumentNullException.ThrowIfNull(nftRule);
        string target = nftRule;
        if (nftRule.Contains('\n', StringComparison.Ordinal))
        {
            foreach (string rawLine in nftRule.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (rawLine.Contains("private_reject", StringComparison.OrdinalIgnoreCase))
                {
                    target = rawLine;
                    break;
                }
            }
        }

        int openBrace = target.IndexOf('{', StringComparison.Ordinal);
        int closeBrace = target.IndexOf('}', StringComparison.Ordinal);
        if (openBrace < 0 || closeBrace <= openBrace)
        {
            return [];
        }

        string body = target[(openBrace + 1)..closeBrace];
        var results = new List<string>();
        foreach (string part in body.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string token = part.Trim();
            if (!token.Contains('/', StringComparison.Ordinal))
            {
                token += "/32";
            }

            results.Add(token);
        }

        return results;
    }

    internal static bool TryParseIpv4(string? text, out IPAddress? ip)
    {
        ip = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        int dotCount = 0;
        foreach (char c in trimmed)
        {
            if (c == '.')
            {
                dotCount++;
            }
            else if (c is < '0' or > '9')
            {
                return false;
            }
        }

        if (dotCount != 3)
        {
            return false;
        }

        if (!IPAddress.TryParse(trimmed, out IPAddress? parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        if (!string.Equals(parsed.ToString(), trimmed, StringComparison.Ordinal))
        {
            return false;
        }

        ip = parsed;
        return true;
    }

    private static CidrRange[] ParseCidrs(string[] cidrs)
        => [.. cidrs.Select(ParseSingle)];

    private static CidrRange ParseSingle(string cidr)
    {
        string normalized = cidr.Contains('/', StringComparison.Ordinal) ? cidr : cidr + "/32";
        string[] parts = normalized.Split('/');
        var network = IPNetwork.Parse(normalized);
        return new CidrRange(
            normalized,
            parts[0],
            int.Parse(parts[1], CultureInfo.InvariantCulture),
            network);
    }
}
