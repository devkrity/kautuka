using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

public sealed record RuntimeImageAllowlist(
    int SchemaVersion,
    IReadOnlyList<RuntimeImageEntry> Images)
{
    public static RuntimeImageAllowlist Empty { get; } = new(1, []);

    [JsonIgnore]
    public FrozenSet<string> ImageReferences { get; } = Images
        .Select(static entry => entry.Image.Trim())
        .Where(static image => image.Length > 0)
        .ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Node, Python, uv, and native binary connectors, and the local kautuka-base tag, run the
    /// published kautuka-base digest when runtime-images.json records one.
    /// Without that entry, a stored Fedora pin uses the local kautuka-base tag.
    /// </summary>
    public string SelectReleasedSandboxImage(string configuredImage, string? serverType = null)
    {
        ArgumentNullException.ThrowIfNull(configuredImage);
        string image = configuredImage.Trim();
        RuntimeImageEntry? released = Images.FirstOrDefault(static entry =>
            string.Equals(entry.Id, "kautuka-base", StringComparison.Ordinal)
            && entry.Image.Contains("@sha256:", StringComparison.Ordinal));
        if (released is null)
        {
            return UseLocalSandboxImage(image);
        }

        if (IsSandboxServerType(serverType) || IsReplaceableSandboxBase(image))
        {
            return released.Image;
        }

        return image;
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

    private static string UseLocalSandboxImage(string image)
    {
        if (IsLocalKautukaBase(image))
        {
            return image;
        }

        if (IsReplaceableSandboxBase(image) && image.Contains("@sha256:", StringComparison.Ordinal))
        {
            return "localhost/kautuka-base:latest";
        }

        return image;
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
}

public sealed record RuntimeImageEntry(
    string Id,
    string Image,
    string? Archive);

public static class RuntimeImageAllowlistLoader
{
    public const string FileName = "runtime-images.json";

    public static RuntimeImageAllowlist Load(string path, bool requirePresent, ILogger? logger = null)
    {
        if (!File.Exists(path))
        {
            if (requirePresent)
            {
                throw new InvalidOperationException(
                    $"Runtime image allowlist '{path}' is required in this environment.");
            }

            logger?.LogWarning("Runtime image allowlist '{Path}' was not found; first-boot image admit will use digest checks only.", path);
            return RuntimeImageAllowlist.Empty;
        }

        try
        {
            using FileStream stream = File.OpenRead(path);
            RuntimeImageAllowlist? loaded = JsonSerializer.Deserialize(stream, RuntimeImageAllowlistJsonContext.Default.RuntimeImageAllowlist);
            if (loaded is null || loaded.Images is null || loaded.Images.Count == 0)
            {
                if (requirePresent)
                {
                    throw new InvalidOperationException($"Runtime image allowlist '{path}' did not contain any images.");
                }

                return RuntimeImageAllowlist.Empty;
            }

            foreach (RuntimeImageEntry entry in loaded.Images)
            {
                if (string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.Image))
                {
                    throw new InvalidOperationException($"Runtime image allowlist '{path}' contains an entry with an empty id or image.");
                }

                ContainerImageProvenancePolicy.EnsureTrusted(entry.Image, $"Allowlist entry '{entry.Id}'");
            }

            return loaded;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            if (requirePresent)
            {
                throw new InvalidOperationException($"Failed to load runtime image allowlist '{path}'.", ex);
            }

            logger?.LogError(ex, "Failed to load runtime image allowlist from {Path}.", path);
            return RuntimeImageAllowlist.Empty;
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RuntimeImageAllowlist))]
[JsonSerializable(typeof(RuntimeImageEntry))]
internal sealed partial class RuntimeImageAllowlistJsonContext : JsonSerializerContext;
