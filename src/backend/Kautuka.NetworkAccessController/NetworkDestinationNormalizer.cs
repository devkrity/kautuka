using System.Globalization;
using System.Net;

namespace Kautuka.NetworkAccessController;

public static class NetworkDestinationNormalizer
{
    private const int maxHostnameLength = 253;
    private const int maxLabelLength = 63;

    /// <summary>
    /// Normalizes a TLS SNI or DNS hostname according to Section 4.1.3:
    /// lowercase, strip at most one trailing dot, convert IDN labels to ASCII (A-label),
    /// and reject userinfo (<c>@</c>), <c>:</c>, <c>/</c>, <c>?</c>, <c>#</c>, <c>%</c>,
    /// whitespace, control chars, leading dot, consecutive dots, empty labels, IP literals,
    /// or names exceeding 253 characters.
    /// </summary>
    public static bool TryNormalizeHostname(string? input, out string normalized, out string errorReason)
    {
        normalized = string.Empty;
        errorReason = "bad_sni";

        if (string.IsNullOrEmpty(input))
        {
            return false;
        }

        // Reject any whitespace, control character, or URI/userinfo/wildcard characters up front.
        foreach (char ch in input)
        {
            if (char.IsWhiteSpace(ch)
                || char.IsControl(ch)
                || ch is '@' or ':' or '/' or '\\' or '?' or '#' or '%' or '*' or '[' or ']')
            {
                return false;
            }
        }

        if (input.StartsWith('.'))
        {
            return false;
        }

        // Strip at most one trailing dot. If another trailing dot remains, reject (e.g. "example.com..").
        ReadOnlySpan<char> span = input.AsSpan();
        if (span.EndsWith("."))
        {
            span = span[..^1];
        }

        if (span.IsEmpty || span.EndsWith(".") || span.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        string candidate = span.ToString();
        string ascii;
        try
        {
            var idn = new IdnMapping { UseStd3AsciiRules = true };
            ascii = idn.GetAscii(candidate).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return false;
        }

        if (ascii.Length is 0 or > maxHostnameLength)
        {
            return false;
        }

        string[] labels = ascii.Split('.');
        bool hasNonNumericLabelChar = false;
        foreach (string label in labels)
        {
            if (label.Length is 0 or > maxLabelLength)
            {
                return false;
            }

            if (label.StartsWith('-') || label.EndsWith('-'))
            {
                return false;
            }

            foreach (char ch in label)
            {
                bool isDigit = ch is >= '0' and <= '9';
                bool isLowerAlpha = ch is >= 'a' and <= 'z';
                bool isHyphen = ch == '-';
                if (!isDigit && !isLowerAlpha && !isHyphen)
                {
                    return false;
                }

                if (!isDigit)
                {
                    hasNonNumericLabelChar = true;
                }
            }
        }

        // Reject IPv4 literals or all-numeric dotted strings passed as hostnames.
        if (!hasNonNumericLabelChar || IPAddress.TryParse(ascii, out _))
        {
            return false;
        }

        // Top-level domain (last label) may not be all-numeric in valid DNS names.
        string lastLabel = labels[^1];
        bool lastLabelAllDigits = true;
        foreach (char ch in lastLabel)
        {
            if (ch is < '0' or > '9')
            {
                lastLabelAllDigits = false;
                break;
            }
        }

        if (lastLabelAllDigits)
        {
            return false;
        }

        normalized = ascii;
        errorReason = string.Empty;
        return true;
    }

    public static bool TryNormalizeIpv4(string? input, out string normalized, out IPAddress? parsed)
    {
        if (NetworkAddressClassifier.TryParseIpv4(input, out parsed) && parsed is not null)
        {
            normalized = parsed.ToString();
            return true;
        }

        normalized = string.Empty;
        parsed = null;
        return false;
    }

    /// <summary>
    /// Validates and normalizes a destination for rule writes (Section 4.1.4).
    /// Throws <see cref="UnauthorizedAccessException"/> or <see cref="ArgumentException"/> on invalid inputs.
    /// </summary>
    public static (string DestKind, string NormalizedDestination, int Port) ValidateAndNormalizeRuleDestination(
        string? destKind,
        string? destination,
        int port,
        bool allowed,
        bool allowLan,
        bool isAdmin,
        bool allowLoopback = false)
    {
        if (string.IsNullOrWhiteSpace(destKind))
        {
            throw new ArgumentException("dest_kind is required.", nameof(destKind));
        }

        if (allowLan && !isAdmin)
        {
            throw new UnauthorizedAccessException("privilege_required: Only an administrator may grant LAN access.");
        }

        switch (destKind)
        {
            case "any-public":
                if (!isAdmin)
                {
                    throw new UnauthorizedAccessException("PermissionDenied: Only an administrator may grant any-public access.");
                }

                if (!allowed)
                {
                    throw new ArgumentException("invalid_destination: any-public rules must be allow rules.", nameof(allowed));
                }

                if (allowLan)
                {
                    throw new ArgumentException("invalid_destination: any-public rules cannot include LAN access.", nameof(allowLan));
                }

                if (!string.IsNullOrEmpty(destination) && destination != "*")
                {
                    throw new ArgumentException("invalid_destination: any-public rules must have an empty destination.", nameof(destination));
                }

                if (port != 0)
                {
                    throw new ArgumentException("invalid_destination: any-public rules must use port 0 (any port).", nameof(port));
                }

                return ("any-public", string.Empty, 0);

            case "host":
                if (port is < 1 or > 65535)
                {
                    throw new ArgumentException("invalid_port: Port must be between 1 and 65535.", nameof(port));
                }

                if (!TryNormalizeHostname(destination, out string normalizedHost, out _))
                {
                    throw new ArgumentException("invalid_destination: Hostname is malformed.", nameof(destination));
                }

                if (NetworkAddressClassifier.IsMetadata(normalizedHost)
                    || normalizedHost.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
                    || (string.Equals(normalizedHost, "localhost", StringComparison.OrdinalIgnoreCase)
                        && !(allowLoopback && allowed && isAdmin && allowLan)))
                {
                    throw new ArgumentException("invalid_destination: Protected or loopback hostnames cannot be added to rules.", nameof(destination));
                }

                return ("host", normalizedHost, port);

            case "ip":
                if (port is < 1 or > 65535)
                {
                    throw new ArgumentException("invalid_port: Port must be between 1 and 65535.", nameof(port));
                }

                if (!TryNormalizeIpv4(destination, out string normalizedIp, out IPAddress? ip))
                {
                    throw new ArgumentException("invalid_destination: IPv4 literal is malformed.", nameof(destination));
                }

                if (NetworkAddressClassifier.IsMetadata(normalizedIp))
                {
                    throw new ArgumentException("invalid_destination: Cloud metadata and platform control endpoints cannot be allowed.", nameof(destination));
                }

                if (NetworkAddressClassifier.IsWorkloadSubnet(normalizedIp))
                {
                    throw new ArgumentException("invalid_destination: Workload subnet addresses (10.247.0.0/16) cannot be added to rules.", nameof(destination));
                }

                if (NetworkAddressClassifier.IsSinkholeRange(normalizedIp))
                {
                    throw new ArgumentException("invalid_destination: DNS sinkhole range (198.18.0.0/15) cannot be added to rules.", nameof(destination));
                }

                if (NetworkAddressClassifier.IsNonPublicV4(ip!))
                {
                    bool isLoopback = string.Equals(normalizedIp, "127.0.0.1", StringComparison.Ordinal) || ip!.GetAddressBytes()[0] == 127;
                    bool permittedPrivate = allowLan && (NetworkAddressClassifier.IsRfc1918AllowableWithLan(normalizedIp)
                        || (allowLoopback && allowed && isAdmin && isLoopback));
                    if (!permittedPrivate)
                    {
                        throw new ArgumentException("invalid_destination: Non-public IPv4 address is not permitted without an administrator LAN grant.", nameof(destination));
                    }
                }

                return ("ip", normalizedIp, port);

            default:
                throw new ArgumentException($"invalid_dest_kind: Unsupported destination kind '{destKind}'.", nameof(destKind));
        }
    }
}
