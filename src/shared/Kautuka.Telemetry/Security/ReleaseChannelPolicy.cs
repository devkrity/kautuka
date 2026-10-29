using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Kautuka.Telemetry.Security;

/// <summary>
/// Defines the public Stable and Preview GitHub release tag formats and their ordering.
/// </summary>
public static partial class ReleaseChannelPolicy
{
    private static readonly Regex stableTagPattern = new(
        "^v(?<major>0|[1-9][0-9]*)\\.(?<minor>0|[1-9][0-9]*)\\.(?<patch>0|[1-9][0-9]*)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex previewTagPattern = new(
        "^v(?<major>0|[1-9][0-9]*)\\.(?<minor>0|[1-9][0-9]*)\\.(?<patch>0|[1-9][0-9]*)-preview\\.(?<preview>0|[1-9][0-9]*)$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static bool TryParseTag(string? tag, string? channel, bool isPrerelease, out ReleaseTagVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(channel))
        {
            return false;
        }

        bool preview = string.Equals(channel, "preview", StringComparison.OrdinalIgnoreCase);
        if (!preview && !string.Equals(channel, "stable", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (preview != isPrerelease)
        {
            return false;
        }

        Match match = (preview ? previewTagPattern : stableTagPattern).Match(tag);
        if (!match.Success
            || !BigInteger.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out BigInteger major)
            || !BigInteger.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out BigInteger minor)
            || !BigInteger.TryParse(match.Groups["patch"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out BigInteger patch))
        {
            return false;
        }

        BigInteger previewNumber = BigInteger.Zero;
        if (preview && !BigInteger.TryParse(match.Groups["preview"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out previewNumber))
        {
            return false;
        }

        version = new ReleaseTagVersion(major, minor, patch, previewNumber, preview);
        return true;
    }

    public static Uri CreateChannelPointerUrl(string owner, string repository, string channel)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        if (!string.Equals(channel, "stable", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(channel, "preview", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Channel must be stable or preview.", nameof(channel));
        }

        string normalizedChannel = string.Equals(channel, "preview", StringComparison.OrdinalIgnoreCase) ? "preview" : "stable";
        return new Uri($"https://raw.githubusercontent.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/release-channel/{normalizedChannel}.json", UriKind.Absolute);
    }

    public static T? SelectLatest<T>(
        IEnumerable<T> releases,
        string channel,
        Func<T, string?> getTag,
        Func<T, bool> isDraft,
        Func<T, bool> isPrerelease)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(releases);
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(getTag);
        ArgumentNullException.ThrowIfNull(isDraft);
        ArgumentNullException.ThrowIfNull(isPrerelease);

        T? selected = null;
        ReleaseTagVersion selectedVersion = default;
        foreach (T release in releases)
        {
            if (isDraft(release)
                || !TryParseTag(getTag(release), channel, isPrerelease(release), out ReleaseTagVersion version))
            {
                continue;
            }

            if (selected is null || version.CompareTo(selectedVersion) > 0)
            {
                selected = release;
                selectedVersion = version;
            }
        }

        return selected;
    }
}

public readonly record struct ReleaseTagVersion(
    BigInteger Major,
    BigInteger Minor,
    BigInteger Patch,
    BigInteger Preview,
    bool IsPreview)
{
    public int CompareTo(ReleaseTagVersion other)
    {
        int compare = Major.CompareTo(other.Major);
        if (compare != 0) return compare;
        compare = Minor.CompareTo(other.Minor);
        if (compare != 0) return compare;
        compare = Patch.CompareTo(other.Patch);
        if (compare != 0) return compare;
        return Preview.CompareTo(other.Preview);
    }

    public string ToVersionString()
        => IsPreview
            ? string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}-preview.{Preview}")
            : string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
}
