using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kautuka.Gateway.Domain.Connectors;

internal static class ReleasedSandboxImage
{
    public const string AllowlistId = "kautuka-base";
    public const string LocalImage = "localhost/kautuka-base:latest";
    public const string FileName = "runtime-images.json";

    public static string Current { get; } = Resolve(AppContext.BaseDirectory);

    /// <summary>
    /// Node, Python, uv, and native binary connectors, and the local kautuka-base tag, run the
    /// published kautuka-base digest when one is recorded. Keep the match in step
    /// with <c>RuntimeImageAllowlist.SelectReleasedSandboxImage</c>.
    /// </summary>
    public static string Select(string releasedImage, string? configuredImage, string? serverType = null)
    {
        string image = configuredImage?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(releasedImage))
        {
            return image;
        }

        bool releasedDigest = releasedImage.Contains("@sha256:", StringComparison.Ordinal);
        if (!IsSandboxServerType(serverType) && !IsReplaceableSandboxBase(image))
        {
            return image;
        }

        if (!releasedDigest && IsLocalKautukaBase(image))
        {
            return image;
        }

        if (!releasedDigest)
        {
            return IsReplaceableSandboxBase(image) && image.Contains("@sha256:", StringComparison.Ordinal)
                ? LocalImage
                : image;
        }

        return releasedImage;
    }

    private static bool IsSandboxServerType(string? serverType)
        => string.Equals(serverType, "node", StringComparison.Ordinal)
            || string.Equals(serverType, "python", StringComparison.Ordinal)
            || string.Equals(serverType, "uv", StringComparison.Ordinal)
            || string.Equals(serverType, "binary", StringComparison.Ordinal);

    private static bool IsReplaceableSandboxBase(string image)
    {
        const string localhostPrefix = "localhost/";
        string name = image.StartsWith(localhostPrefix, StringComparison.Ordinal)
            ? image[localhostPrefix.Length..]
            : image;
        if (string.Equals(name, "kautuka-base:latest", StringComparison.Ordinal)
            || string.Equals(name, "kautuka-base", StringComparison.Ordinal))
        {
            return true;
        }

        return image.StartsWith("quay.io/fedora/fedora-minimal", StringComparison.Ordinal);
    }

    private static bool IsLocalKautukaBase(string image)
    {
        const string localhostPrefix = "localhost/";
        string name = image.StartsWith(localhostPrefix, StringComparison.Ordinal)
            ? image[localhostPrefix.Length..]
            : image;
        return string.Equals(name, "kautuka-base:latest", StringComparison.Ordinal)
            || string.Equals(name, "kautuka-base", StringComparison.Ordinal);
    }

    private static string Resolve(string contentRoot)
    {
        string path = Path.Combine(contentRoot, FileName);
        if (!File.Exists(path))
        {
            return LocalImage;
        }

        try
        {
            using FileStream stream = File.OpenRead(path);
            SandboxImageFile? file = JsonSerializer.Deserialize(stream, SandboxImageJsonContext.Default.SandboxImageFile);
            SandboxImageEntry? entry = file?.Images.FirstOrDefault(
                candidate => string.Equals(candidate.Id, AllowlistId, StringComparison.Ordinal));
            if (entry is not null && entry.Image.Contains("@sha256:", StringComparison.Ordinal))
            {
                return entry.Image;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return LocalImage;
        }

        return LocalImage;
    }
}

internal sealed class SandboxImageFile
{
    public List<SandboxImageEntry> Images { get; set; } = [];
}

internal sealed class SandboxImageEntry
{
    public string Id { get; set; } = string.Empty;

    public string Image { get; set; } = string.Empty;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SandboxImageFile))]
internal sealed partial class SandboxImageJsonContext : JsonSerializerContext;
