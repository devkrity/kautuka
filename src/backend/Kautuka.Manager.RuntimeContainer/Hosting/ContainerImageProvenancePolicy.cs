namespace Kautuka.Manager.RuntimeContainer.Hosting;

/// <summary>
/// Prevents mutable registry tags from entering the trusted runtime plane. Images built and
/// named by this appliance are local; every externally sourced image must identify immutable
/// content by digest. First-boot infrastructure images must also appear on the signed
/// Kautuka runtime-image allowlist when one is loaded.
/// </summary>
internal static class ContainerImageProvenancePolicy
{
    private const string digestMarker = "@sha256:";

    public static void EnsureTrusted(string image, string subject)
        => EnsureTrusted(image, subject, allowedImages: null, requireListed: false);

    public static void EnsureTrusted(
        string image,
        string subject,
        IReadOnlySet<string>? allowedImages,
        bool requireListed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(image);

        string normalized = image.Trim();
        EnsureSingleLine(normalized, subject);
        if (IsLocalImage(normalized))
        {
            return;
        }

        if (!HasSha256Digest(normalized))
        {
            throw new InvalidOperationException(
                $"{subject} image '{normalized}' is a mutable registry reference. " +
                "External runtime images must be pinned with an @sha256:<64-hex> digest.");
        }

        if (!requireListed || allowedImages is null || allowedImages.Count == 0)
        {
            return;
        }

        if (allowedImages.Contains(normalized))
        {
            return;
        }

        throw new InvalidOperationException(
            $"{subject} image '{normalized}' is not on the signed Kautuka runtime-image allowlist.");
    }

    internal static void EnsureSingleLine(string image, string subject)
    {
        if (image.Contains('\n', StringComparison.Ordinal)
            || image.Contains('\r', StringComparison.Ordinal)
            || image.Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{subject} image reference must be a single line.");
        }
    }

    internal static bool HasSha256Digest(string image)
    {
        int markerIndex = image.LastIndexOf(digestMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex <= 0)
        {
            return false;
        }

        ReadOnlySpan<char> digest = image.AsSpan(markerIndex + digestMarker.Length);
        if (digest.Length != 64)
        {
            return false;
        }

        foreach (char character in digest)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsLocalImage(string image)
    {
        if (image.StartsWith("localhost/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (image.Contains('/', StringComparison.Ordinal))
        {
            return false;
        }

        // A short name is the appliance's own image. Any other short name is a
        // mutable registry tag (python:latest) and must not skip the digest pin.
        int tagIndex = image.IndexOf(':', StringComparison.Ordinal);
        string name = tagIndex >= 0 ? image[..tagIndex] : image;
        return name.StartsWith("kautuka-", StringComparison.Ordinal);
    }
}
