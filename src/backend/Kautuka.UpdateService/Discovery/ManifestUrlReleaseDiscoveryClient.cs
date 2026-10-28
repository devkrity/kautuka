using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

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
        string? manifestUrl = ResolveManifestUrl(request.ReleaseChannel);
        if (string.IsNullOrWhiteSpace(manifestUrl)
            || !Uri.TryCreate(manifestUrl, UriKind.Absolute, out Uri? manifestUri))
        {
            return ReleaseDiscoveryResult.NoRelease();
        }

        HttpClient client = httpClientFactory.CreateClient("kautuka.updateservice.github");
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("KautukaUpdateService", "1.0"));

        using HttpRequestMessage manifestRequest = new(HttpMethod.Get, manifestUri);
        if (!string.IsNullOrWhiteSpace(request.IfNoneMatch))
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

        string? eTag = response.Headers.ETag?.Tag;
        byte[] manifestBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        PlatformReleaseDescriptor? descriptor = JsonSerializer.Deserialize(
            manifestBytes,
            PlatformReleaseDescriptorJsonContext.Default.PlatformReleaseDescriptor);

        if (descriptor is null)
        {
            return ReleaseDiscoveryResult.Failed("Manifest release discovery returned an empty descriptor.");
        }

        if (!string.IsNullOrWhiteSpace(descriptor.Channel)
            && !string.Equals(descriptor.Channel, request.ReleaseChannel, StringComparison.OrdinalIgnoreCase))
        {
            return ReleaseDiscoveryResult.NoRelease(eTag);
        }

        PlatformReleaseArtifactDescriptor? platformArtifact = ResolvePlatformArtifact(descriptor);
        if (platformArtifact is null)
        {
            return ReleaseDiscoveryResult.Unsupported(
                $"Manifest '{manifestUri}' does not contain a platform artifact for '{options.PlatformAssetName}'.",
                eTag);
        }

        string? platformArtifactUrl = ResolveAssetUrl(manifestUri, platformArtifact.Url, platformArtifact.Path);
        string? signatureAssetUrl = ResolveAssetUrl(manifestUri, descriptor.Signature?.Url, descriptor.Signature?.Path);
        string releaseId = FirstNonEmpty(
            descriptor.Version,
            string.IsNullOrWhiteSpace(descriptor.ReleaseTag) ? null : NormalizeVersion(descriptor.ReleaseTag),
            descriptor.ReleaseId,
            manifestUri.ToString());
        string releaseTag = FirstNonEmpty(descriptor.ReleaseTag, releaseId);
        string version = FirstNonEmpty(descriptor.Version, NormalizeVersion(releaseTag), releaseId);
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
                SourceUrl: manifestUri.ToString(),
                PublishedAtUtc: publishedAtUtc,
                Tag: releaseTag),
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

    private string? ResolveManifestUrl(string releaseChannel)
    {
        if (string.Equals(releaseChannel, "preview", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(options.PreviewReleaseManifestUrl))
        {
            return options.PreviewReleaseManifestUrl;
        }

        if (string.Equals(releaseChannel, "stable", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(options.StableReleaseManifestUrl))
        {
            return options.StableReleaseManifestUrl;
        }

        return options.ReleaseManifestUrl;
    }

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
