using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Kautuka.Telemetry.Security;
using Kautuka.UpdateService.Abstractions;
using Kautuka.UpdateService.Hosting;
using Kautuka.UpdateService.Models;

using Microsoft.Extensions.Options;

namespace Kautuka.UpdateService.Discovery;

internal sealed class ManifestUrlReleaseDiscoveryClient(
    IHttpClientFactory httpClientFactory,
    IOptions<UpdateOptions> options,
    ILogger<ManifestUrlReleaseDiscoveryClient> logger) : IReleaseDiscoveryClient
{
    private readonly IHttpClientFactory httpClientFactory = httpClientFactory;
    private readonly UpdateOptions options = options.Value;
    private readonly ILogger<ManifestUrlReleaseDiscoveryClient> logger = logger;

    public async Task<ReleaseDiscoveryResult> DiscoverAsync(ReleaseDiscoveryRequest request, CancellationToken cancellationToken)
    {
        string? manifestUrl = ResolveManifestUrl();
        string? selectedReleaseTag = null;
        string? discoveryETag = null;
        HttpClient client = httpClientFactory.CreateClient("kautuka.updateservice.github");
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("KautukaUpdateService", "1.0"));
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        if (string.IsNullOrWhiteSpace(manifestUrl))
        {
            var resolution = await ResolveLatestReleaseAsync(client, request, cancellationToken).ConfigureAwait(false);
            manifestUrl = resolution.ManifestUrl;
            selectedReleaseTag = resolution.ReleaseTag;
            discoveryETag = resolution.ETag;
            ReleaseDiscoveryResult? failure = resolution.Failure;
            if (failure is not null)
            {
                return failure;
            }
        }

        if (string.IsNullOrWhiteSpace(manifestUrl)
            || !Uri.TryCreate(manifestUrl, UriKind.Absolute, out Uri? manifestUri))
        {
            return ReleaseDiscoveryResult.NoRelease();
        }

        using HttpRequestMessage manifestRequest = new(HttpMethod.Get, manifestUri);
        if (selectedReleaseTag is null && !string.IsNullOrWhiteSpace(request.IfNoneMatch))
        {
            manifestRequest.Headers.IfNoneMatch.ParseAdd(request.IfNoneMatch);
        }

        using HttpResponseMessage response = await client.SendAsync(
            manifestRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return ReleaseDiscoveryResult.NoChange(response.Headers.ETag?.Tag ?? request.IfNoneMatch);
        }

        if (!response.IsSuccessStatusCode)
        {
            return ReleaseDiscoveryResult.Failed(
                $"Manifest release discovery failed with HTTP {(int)response.StatusCode}.");
        }

        string? eTag = response.Headers.ETag?.Tag ?? discoveryETag;
        byte[] manifestBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        PlatformReleaseDescriptor? descriptor = JsonSerializer.Deserialize(
            manifestBytes,
            PlatformReleaseDescriptorJsonContext.Default.PlatformReleaseDescriptor);

        if (descriptor is null)
        {
            return ReleaseDiscoveryResult.Failed("Manifest release discovery returned an empty descriptor.");
        }

        if (!string.Equals(descriptor.Channel, request.ReleaseChannel, StringComparison.OrdinalIgnoreCase)
            || (!options.AllowUnsignedLocalFeeds && !ReleaseChannelPolicy.TryParseTag(
                descriptor.ReleaseTag,
                request.ReleaseChannel,
                string.Equals(request.ReleaseChannel, "preview", StringComparison.OrdinalIgnoreCase),
                out _))
            || string.IsNullOrWhiteSpace(descriptor.SourceSha)
            || descriptor.SourceSha.Length != 40
            || !descriptor.SourceSha.All(Uri.IsHexDigit))
        {
            return ReleaseDiscoveryResult.NoRelease(eTag);
        }

        string releaseTag = descriptor.ReleaseTag ?? string.Empty;
        if (selectedReleaseTag is not null && !string.Equals(selectedReleaseTag, releaseTag, StringComparison.Ordinal))
        {
            return ReleaseDiscoveryResult.Unsupported("Signed release manifest tag does not match the selected published GitHub Release.", eTag);
        }
        string version;
        if (ReleaseChannelPolicy.TryParseTag(
            releaseTag,
            request.ReleaseChannel,
            string.Equals(request.ReleaseChannel, "preview", StringComparison.OrdinalIgnoreCase),
            out ReleaseTagVersion parsedVersion))
        {
            version = parsedVersion.ToVersionString();
            if (!string.Equals(descriptor.Version, version, StringComparison.Ordinal))
            {
                return ReleaseDiscoveryResult.NoRelease(eTag);
            }
        }
        else if (options.AllowUnsignedLocalFeeds && !string.IsNullOrWhiteSpace(descriptor.Version))
        {
            version = descriptor.Version;
        }
        else
        {
            return ReleaseDiscoveryResult.NoRelease(eTag);
        }

        if (!options.AllowUnsignedLocalFeeds && selectedReleaseTag is null)
        {
            return ReleaseDiscoveryResult.Unsupported("Production release discovery must select a published GitHub Release.", eTag);
        }

        PlatformReleaseArtifactDescriptor? platformArtifact = ResolvePlatformArtifact(descriptor);
        if (platformArtifact is null)
        {
            return ReleaseDiscoveryResult.Unsupported(
                $"Manifest '{manifestUri}' does not contain a platform artifact for '{options.PlatformAssetName}'.",
                eTag);
        }

        string releaseId = releaseTag;
        if (!options.AllowUnsignedLocalFeeds
            && !string.Equals(descriptor.ReleaseId, releaseTag, StringComparison.Ordinal))
        {
            return ReleaseDiscoveryResult.Unsupported("Signed release manifest releaseId does not match its exact release tag.", eTag);
        }
        string? platformArtifactUrl = ResolveAssetUrl(manifestUri, platformArtifact.Url, platformArtifact.Path);
        string? signatureAssetUrl = ResolveAssetUrl(manifestUri, descriptor.Signature?.Url, descriptor.Signature?.Path);
        if (!options.AllowUnsignedLocalFeeds && !HasExactTagAssetUrls(descriptor, request, releaseTag, platformArtifactUrl, signatureAssetUrl))
        {
            return ReleaseDiscoveryResult.Unsupported(
                "Signed release manifest does not bind its signature and platform artifact to the exact canonical GitHub Release tag.",
                eTag);
        }

        if (!options.AllowUnsignedLocalFeeds)
        {
            if (string.IsNullOrWhiteSpace(options.ManifestPublicKeyPath) || !File.Exists(options.ManifestPublicKeyPath))
            {
                return ReleaseDiscoveryResult.Unsupported("No trusted public key is configured for signed release discovery.", eTag);
            }

            if (string.IsNullOrWhiteSpace(signatureAssetUrl))
            {
                return ReleaseDiscoveryResult.Unsupported("Signed release manifest has no exact-tag signature URL.", eTag);
            }

            byte[] signatureBytes;
            try
            {
                signatureBytes = await DownloadBytesAsync(client, signatureAssetUrl, cancellationToken).ConfigureAwait(false);
                string publicKey = await File.ReadAllTextAsync(options.ManifestPublicKeyPath, cancellationToken).ConfigureAwait(false);
                string signature = Encoding.UTF8.GetString(signatureBytes);
                if (!PlatformReleaseSignatureVerifier.TryVerify(manifestBytes, signature, publicKey, out string? verificationError))
                {
                    return ReleaseDiscoveryResult.Unsupported(verificationError ?? "Release manifest signature verification failed.", eTag);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
            {
                return ReleaseDiscoveryResult.Failed("Release signature verification could not complete.");
            }
        }

        string fingerprint = ComputeReleaseFingerprint(manifestUri, manifestBytes, platformArtifactUrl, signatureAssetUrl);
        DateTimeOffset? publishedAtUtc = TryParsePublishedAt(descriptor.PublishedAt);

        logger.LogDebug(
            "Discovered manifest-backed release {ReleaseTag} for channel {Channel} from {ManifestUrl}.",
            releaseTag,
            request.ReleaseChannel,
            manifestUri);

        return ReleaseDiscoveryResult.Available(
            new UpdateReleaseReference(
                ReleaseId: releaseId,
                Version: version,
                Channel: request.ReleaseChannel,
                ReleaseFingerprint: fingerprint,
                PlatformArtifactUrl: platformArtifactUrl,
                PlatformReleaseAssetUrl: manifestUri.ToString(),
                PlatformReleaseSignatureAssetUrl: signatureAssetUrl,
                SourceUrl: $"https://github.com/{options.RepositoryOwner ?? "devkrity"}/{options.RepositoryName ?? "kautuka"}/releases/tag/{Uri.EscapeDataString(releaseTag)}",
                PublishedAtUtc: publishedAtUtc,
                Tag: releaseTag)
            {
                SourceSha = descriptor.SourceSha,
            },
            eTag);
    }

    internal static PlatformReleaseArtifactDescriptor? ResolvePlatformArtifact(PlatformReleaseDescriptor descriptor)
    {
        if (descriptor.Artifacts.Count == 0)
        {
            return null;
        }

        PlatformReleaseArtifactDescriptor? byName = descriptor.Artifacts.FirstOrDefault(artifact =>
            string.Equals(artifact.Name, "kautuka-platform-linux-x64.tar.gz", StringComparison.OrdinalIgnoreCase));
        if (byName is not null)
        {
            return byName;
        }

        PlatformReleaseArtifactDescriptor? byPlatform = descriptor.Artifacts.FirstOrDefault(static artifact =>
            string.Equals(artifact.Platform, "linux-x64", StringComparison.OrdinalIgnoreCase));
        return byPlatform ?? descriptor.Artifacts[0];
    }

    private async Task<(string? ManifestUrl, string? ReleaseTag, string? ETag, ReleaseDiscoveryResult? Failure)> ResolveLatestReleaseAsync(
        HttpClient client,
        ReleaseDiscoveryRequest request,
        CancellationToken cancellationToken)
    {
        string owner = options.RepositoryOwner ?? request.RepositoryOwner;
        string repository = options.RepositoryName ?? request.RepositoryName;
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repository))
        {
            return (null, null, null, ReleaseDiscoveryResult.Failed("GitHub release repository is not configured."));
        }

        var candidates = new List<GitHubReleaseCandidate>();
        string? firstPageETag = null;
        const int pageSize = 100;
        for (int page = 1; page <= 11; page++)
        {
            string url = $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/releases?per_page={pageSize}&page={page}";
            HttpResponseMessage response;
            try
            {
                response = await client.GetAsync(new Uri(url, UriKind.Absolute), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException)
            {
                return (null, null, null, ReleaseDiscoveryResult.Failed("GitHub release discovery request failed."));
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    return (null, null, null, ReleaseDiscoveryResult.Failed($"GitHub release discovery failed with HTTP {(int)response.StatusCode}."));
                }

                if (page == 1)
                {
                    firstPageETag = response.Headers.ETag?.Tag;
                }

                using JsonDocument document = await JsonDocument.ParseAsync(
                    await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    return (null, null, firstPageETag, ReleaseDiscoveryResult.Failed("GitHub release discovery returned an invalid response."));
                }

                JsonElement[] rows = document.RootElement.EnumerateArray().ToArray();
                if (page == 11)
                {
                    if (rows.Length != 0)
                    {
                        return (null, null, firstPageETag, ReleaseDiscoveryResult.Failed("GitHub release history exceeds the safe 1,000-release discovery limit."));
                    }

                    break;
                }

                foreach (JsonElement row in rows)
                {
                    if (row.ValueKind != JsonValueKind.Object
                        || !row.TryGetProperty("tag_name", out JsonElement tagValue)
                        || tagValue.ValueKind != JsonValueKind.String
                        || !row.TryGetProperty("draft", out JsonElement draftValue)
                        || draftValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                        || !row.TryGetProperty("prerelease", out JsonElement prereleaseValue)
                        || prereleaseValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                        || !row.TryGetProperty("published_at", out JsonElement publishedValue)
                        || publishedValue.ValueKind is not (JsonValueKind.String or JsonValueKind.Null))
                    {
                        return (null, null, firstPageETag, ReleaseDiscoveryResult.Failed("GitHub release discovery contained malformed release metadata."));
                    }

                    string? tag = GetString(row, "tag_name");
                    bool draft = draftValue.ValueKind == JsonValueKind.True;
                    bool prerelease = prereleaseValue.ValueKind == JsonValueKind.True;
                    bool published = !string.IsNullOrWhiteSpace(GetString(row, "published_at"));
                    if (draft || !published || !ReleaseChannelPolicy.TryParseTag(tag, request.ReleaseChannel, prerelease, out ReleaseTagVersion parsed))
                    {
                        logger.LogDebug("Skipping GitHub release {ReleaseTag}: draft, unpublished, or not a strict {Channel} release.", tag, request.ReleaseChannel);
                        continue;
                    }

                    candidates.Add(new GitHubReleaseCandidate(tag!, parsed));
                }

                if (rows.Length < pageSize)
                {
                    break;
                }
            }
        }

        GitHubReleaseCandidate? selected;
        try
        {
            selected = ReleaseChannelPolicy.SelectLatest(
                candidates,
                request.ReleaseChannel,
                static candidate => candidate.Tag,
                static _ => false,
                candidate => candidate.Version.IsPreview);
        }
        catch (InvalidOperationException ex)
        {
            return (null, null, firstPageETag, ReleaseDiscoveryResult.Failed(ex.Message));
        }

        if (selected is null)
        {
            return (null, null, firstPageETag, ReleaseDiscoveryResult.NoRelease(firstPageETag));
        }

        string manifestUrl = $"https://github.com/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repository)}/releases/download/{Uri.EscapeDataString(selected.Tag)}/{Uri.EscapeDataString(options.PlatformReleaseAssetName)}";
        return (manifestUrl, selected.Tag, firstPageETag, null);
    }

    private static string? GetString(JsonElement row, string property)
        => row.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private sealed record GitHubReleaseCandidate(string Tag, ReleaseTagVersion Version);

    private string? ResolveManifestUrl()
        => options.ReleaseManifestUrl;

    private static string? ResolveAssetUrl(Uri manifestUri, string? absoluteUrl, string? relativePath)
    {
        if (!string.IsNullOrWhiteSpace(absoluteUrl))
        {
            return absoluteUrl;
        }

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        return new Uri(manifestUri, relativePath).ToString();
    }

    private bool HasExactTagAssetUrls(
        PlatformReleaseDescriptor descriptor,
        ReleaseDiscoveryRequest request,
        string tag,
        string? platformArtifactUrl,
        string? signatureAssetUrl)
    {
        string owner = options.RepositoryOwner ?? request.RepositoryOwner;
        string repository = options.RepositoryName ?? request.RepositoryName;
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repository))
        {
            return false;
        }

        string releaseBase = $"https://github.com/{owner}/{repository}/releases/download/{Uri.EscapeDataString(tag)}";
        string expectedSignature = $"{releaseBase}/{Uri.EscapeDataString(options.PlatformReleaseSignatureAssetName)}";
        string expectedArtifact = $"{releaseBase}/{Uri.EscapeDataString(options.PlatformAssetName)}";
        return string.Equals(descriptor.Signature?.Url, expectedSignature, StringComparison.Ordinal)
            && string.Equals(descriptor.Signature?.Path, options.PlatformReleaseSignatureAssetName, StringComparison.Ordinal)
            && string.Equals(signatureAssetUrl, expectedSignature, StringComparison.Ordinal)
            && string.Equals(platformArtifactUrl, expectedArtifact, StringComparison.Ordinal)
            && descriptor.Artifacts.Count(artifact => string.Equals(artifact.Name, options.PlatformAssetName, StringComparison.Ordinal)) == 1
            && string.Equals(descriptor.Artifacts.Single(artifact => string.Equals(artifact.Name, options.PlatformAssetName, StringComparison.Ordinal)).Url, expectedArtifact, StringComparison.Ordinal);
    }

    private static async Task<byte[]> DownloadBytesAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.GetAsync(new Uri(url, UriKind.Absolute), HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ComputeReleaseFingerprint(Uri manifestUri, byte[] manifestBytes, string? artifactUrl, string? signatureUrl)
    {
        byte[] hash = SHA256.HashData(
            Encoding.UTF8.GetBytes(
                string.Join('|', manifestUri, Convert.ToHexStringLower(SHA256.HashData(manifestBytes)), artifactUrl ?? string.Empty, signatureUrl ?? string.Empty)));
        return Convert.ToHexStringLower(hash);
    }

    private static string FirstNonEmpty(params string?[] candidates)
        => candidates.First(static candidate => !string.IsNullOrWhiteSpace(candidate))!;

    private static string NormalizeVersion(string value)
        => value.Trim().TrimStart('v', 'V');

    private static DateTimeOffset? TryParsePublishedAt(string? value)
        => DateTimeOffset.TryParse(value, out DateTimeOffset parsed) ? parsed : null;
}
