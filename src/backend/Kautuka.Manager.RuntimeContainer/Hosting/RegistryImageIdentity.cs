using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

/// <summary>Links a pinned registry index to one supported platform manifest.</summary>
internal sealed record RegistryImageIdentity(string SourceReference, string SelectedReference, string Platform)
{
    private const string ociManifest = "application/vnd.oci.image.manifest.v1+json";
    private const string dockerManifest = "application/vnd.docker.distribution.manifest.v2+json";
    private const string ociIndex = "application/vnd.oci.image.index.v1+json";
    private const string dockerIndex = "application/vnd.docker.distribution.manifest.list.v2+json";
    private const int maxMetadataBytes = 4 * 1024 * 1024;

    internal static RegistryImageIdentity Select(string sourceReference, ReadOnlyMemory<byte> rawManifest, string platform)
    {
        ValidateSource(sourceReference, platform);
        int separator = sourceReference.LastIndexOf('@');
        using JsonDocument document = VerifiedDocument(rawManifest, sourceReference[(separator + 1)..]);
        JsonElement root = document.RootElement;
        string? mediaType = root.GetProperty("mediaType").GetString();
        if (mediaType is ociManifest or dockerManifest)
            return new(sourceReference, sourceReference, platform);
        if (mediaType is not (ociIndex or dockerIndex))
            throw new InvalidOperationException("Unsupported registry manifest type.");
        string architecture = platform[6..];
        string? selected = null;
        foreach (JsonElement descriptor in root.GetProperty("manifests").EnumerateArray())
        {
            if (!descriptor.TryGetProperty("platform", out JsonElement candidate)
                || candidate.GetProperty("os").GetString() != "linux"
                || candidate.GetProperty("architecture").GetString() != architecture) continue;
            if (candidate.TryGetProperty("variant", out JsonElement variant)
                && !string.IsNullOrEmpty(variant.GetString())
                && !(architecture == "arm64" && variant.GetString() == "v8"))
                continue;
            if (selected is not null || descriptor.GetProperty("mediaType").GetString() is not (ociManifest or dockerManifest))
                throw new InvalidOperationException("Registry platform selection is ambiguous or nested.");
            selected = descriptor.GetProperty("digest").GetString();
            if (!RuntimeImageEntry.IsLocalId(selected))
                throw new InvalidOperationException("Registry manifests must use SHA-256 identities.");
        }
        if (selected is null) throw new InvalidOperationException("The registry index has no supported target platform.");
        return new(sourceReference, sourceReference[..separator] + "@" + selected, platform);
    }

    // Call before helper creation or any registry IO. Authorization of this
    // repository and its network destinations remains the caller's responsibility.
    internal static void ValidateSource(string sourceReference, string platform)
    {
        if (!ContainerImageProvenancePolicy.HasSha256Digest(sourceReference)
            || ContainerImageProvenancePolicy.IsLocalImage(sourceReference)
            || sourceReference.Any(static character => char.IsWhiteSpace(character) || char.IsControl(character) || character > 127)
            || sourceReference.Contains("://", StringComparison.Ordinal)
            || sourceReference.Count(static character => character == '@') != 1
            || platform is not ("linux/amd64" or "linux/arm64"))
            throw new InvalidOperationException("Registry acquisition requires a pinned repository and supported Linux platform.");
        int slash = sourceReference.IndexOf('/', StringComparison.Ordinal);
        int at = sourceReference.LastIndexOf('@');
        if (slash <= 0 || slash >= at - 1) throw new InvalidOperationException("A registry hostname and repository are required.");
        string registry = sourceReference[..slash];
        if ((!registry.Contains('.', StringComparison.Ordinal) && !registry.Contains(':', StringComparison.Ordinal))
            || !Uri.TryCreate("https://" + registry + "/", UriKind.Absolute, out Uri? uri)
            || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.HostNameType is not (UriHostNameType.Dns or UriHostNameType.IPv4))
            throw new InvalidOperationException("Registry search names and noncanonical authorities are not supported.");
        foreach (char character in sourceReference.AsSpan(slash + 1, at - slash - 1))
            if (!(char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '/' or '.' or '_' or '-'))
                throw new InvalidOperationException("Unsupported registry repository spelling.");
    }

    // Used with the preserved manifest and config read from the private archive.
    // The destination's own digest must never replace the original index binding.
    internal string VerifyConfigIdentity(ReadOnlyMemory<byte> rawManifest, ReadOnlyMemory<byte> rawConfig)
    {
        using JsonDocument manifest = VerifiedDocument(rawManifest, SelectedReference[(SelectedReference.LastIndexOf('@') + 1)..]);
        if (manifest.RootElement.GetProperty("mediaType").GetString() is not (ociManifest or dockerManifest))
            throw new InvalidOperationException("The acquired image is not a supported platform manifest.");
        string? configId = manifest.RootElement.GetProperty("config").GetProperty("digest").GetString();
        if (!RuntimeImageEntry.IsLocalId(configId)) throw new InvalidOperationException("Invalid registry config digest.");
        using JsonDocument config = VerifiedDocument(rawConfig, configId!);
        string actualPlatform = config.RootElement.GetProperty("os").GetString() + "/" + config.RootElement.GetProperty("architecture").GetString();
        if (actualPlatform != Platform) throw new InvalidOperationException("The acquired config has the wrong platform.");
        return configId!;
    }

    internal async Task<string> VerifyArchiveAsync(string archivePath, CancellationToken cancellationToken)
    {
        byte[] indexBytes = await ReadArchiveMetadataAsync(archivePath, "index.json", cancellationToken).ConfigureAwait(false);
        using JsonDocument index = JsonDocument.Parse(indexBytes, new JsonDocumentOptions { MaxDepth = 32 });
        JsonElement manifests = index.RootElement.GetProperty("manifests");
        string digest = SelectedReference[(SelectedReference.LastIndexOf('@') + 1)..];
        if (manifests.GetArrayLength() != 1 || manifests[0].GetProperty("digest").GetString() != digest)
            throw new InvalidOperationException("The acquired archive must contain only the selected immutable manifest.");
        byte[] manifestBytes = await ReadArchiveMetadataAsync(archivePath, "blobs/sha256/" + digest[7..], cancellationToken).ConfigureAwait(false);
        using JsonDocument manifest = VerifiedDocument(manifestBytes, digest);
        string? configId = manifest.RootElement.GetProperty("config").GetProperty("digest").GetString();
        if (!RuntimeImageEntry.IsLocalId(configId)) throw new InvalidOperationException("Invalid acquired config identity.");
        byte[] configBytes = await ReadArchiveMetadataAsync(archivePath, "blobs/sha256/" + configId![7..], cancellationToken).ConfigureAwait(false);
        return VerifyConfigIdentity(manifestBytes, configBytes);
    }

    private static async Task<byte[]> ReadArchiveMetadataAsync(string archivePath, string name, CancellationToken cancellationToken)
    {
        using FileStream input = File.OpenRead(archivePath);
        using var reader = new TarReader(input);
        byte[]? result = null;
        while (await reader.GetNextEntryAsync(copyData: false, cancellationToken).ConfigureAwait(false) is TarEntry entry)
        {
            if (entry.Name != name) continue;
            if (result is not null || entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)
                || entry.Length <= 0 || entry.Length > maxMetadataBytes || entry.DataStream is null)
                throw new InvalidOperationException("Acquired archive metadata is duplicated, oversized or not a regular file.");
            result = new byte[checked((int)entry.Length)];
            await entry.DataStream.ReadExactlyAsync(result, cancellationToken).ConfigureAwait(false);
        }
        return result ?? throw new InvalidOperationException("The acquired archive is missing required metadata.");
    }

    private static JsonDocument VerifiedDocument(ReadOnlyMemory<byte> bytes, string digest)
    {
        if (bytes.Length == 0 || bytes.Length > maxMetadataBytes
            || !digest.Equals("sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes.Span)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Registry metadata differs from its immutable digest or exceeds the metadata limit.");
        return JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
    }
}
