using System.Diagnostics.CodeAnalysis;

using Kautuka.UpdateService.Models;

namespace Kautuka.UpdateService.Hosting;

/// <summary>
/// Fail-closed release/catalog trust rules. Production never sets
/// <see cref="UpdateOptions.AllowUnsignedLocalFeeds"/>. Development may opt in
/// explicitly for unsigned local feeds without weakening production.
/// </summary>
internal static class ReleaseTrustPolicy
{
    public static void EnsureSafeReleaseId(string releaseId)
    {
        if (string.IsNullOrWhiteSpace(releaseId)
            || releaseId is "." or ".."
            || releaseId.IndexOfAny(['/', '\\', '\0', ':']) >= 0
            || !string.Equals(releaseId, Path.GetFileName(releaseId), StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Release id '{releaseId}' is not a single path segment.");
        }
    }

    public static bool RequiresVerification(UpdateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return !options.AllowUnsignedLocalFeeds;
    }

    public static void EnsureApprovedSourceUrl(string assetUrl, UpdateOptions options, string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetUrl);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);

        if (DescribeRejection(assetUrl, purpose) is { } rejection)
        {
            throw new InvalidOperationException(rejection);
        }
    }

    /// <summary>
    /// Reports the first release source URL that <see cref="EnsureApprovedSourceUrl"/> would
    /// reject, so discovery can refuse a release instead of offering it.
    /// </summary>
    /// <remarks>
    /// Discovery verifies the manifest's signature but never looked at the URLs inside it,
    /// while apply checks every one of them. A feed published over plain http on a LAN
    /// address was therefore advertised as Available and then failed at apply with
    /// "Platform release signature URL must be https or loopback http", leaving the appliance
    /// offering an update it could never install.
    /// </remarks>
    public static bool TryFindUnapprovedSourceUrl(
        UpdateReleaseReference release,
        UpdateOptions options,
        [NotNullWhen(true)] out string? rejection)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(options);

        // The artifact URL apply settles on can come from the platform release descriptor,
        // which discovery has not downloaded, so only the URLs already on the reference are
        // checkable here. Those are the two that failed in practice.
        (string? Url, string Purpose)[] candidates =
        [
            (release.PlatformReleaseAssetUrl, "Platform release manifest"),
            (release.PlatformReleaseSignatureAssetUrl, "Platform release signature"),
            (release.PlatformArtifactUrl, "Platform release artifact"),
        ];

        foreach ((string? url, string purpose) in candidates)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            rejection = DescribeRejection(url, purpose);
            if (rejection is not null)
            {
                return true;
            }
        }

        rejection = null;
        return false;
    }

    private static string? DescribeRejection(string assetUrl, string purpose)
    {
        if (!Uri.TryCreate(assetUrl, UriKind.Absolute, out Uri? uri))
        {
            return $"{purpose} URL is not an absolute URI: {assetUrl}";
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (uri.IsLoopback
            && string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
        {
            // Signed (or explicitly opted-in unsigned) local feeds serve over loopback HTTP.
            return null;
        }

        return $"{purpose} URL must be https or loopback http. Rejected: {assetUrl}";
    }
}
