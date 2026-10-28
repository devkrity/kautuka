using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Kautuka.UpdateService.Abstractions;
using Kautuka.UpdateService.Hosting;
using Kautuka.UpdateService.Models;

using Microsoft.Extensions.Options;

namespace Kautuka.UpdateService.Discovery;

internal sealed class GitHubReleaseDiscoveryClient(
    IHttpClientFactory httpClientFactory,
    IOptions<UpdateOptions> options,
    ILogger<GitHubReleaseDiscoveryClient> logger) : IReleaseDiscoveryClient
{
    private const string gitHubApiBaseAddress = "https://api.github.com/";
    private readonly IHttpClientFactory httpClientFactory = httpClientFactory;
    private readonly UpdateOptions options = options.Value;
    private readonly ILogger<GitHubReleaseDiscoveryClient> logger = logger;

    public async Task<ReleaseDiscoveryResult> DiscoverAsync(ReleaseDiscoveryRequest request, CancellationToken cancellationToken)
    {
        HttpClient client = httpClientFactory.CreateClient("kautuka.updateservice.github");
        client.BaseAddress ??= new Uri(gitHubApiBaseAddress, UriKind.Absolute);
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.UserAgent.Clear();
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("KautukaUpdateService", "1.0"));
        client.DefaultRequestHeaders.Authorization = null;

        using HttpRequestMessage releasesRequest = new(
            HttpMethod.Get,
            $"repos/{request.RepositoryOwner}/{request.RepositoryName}/releases?per_page=10");

        if (!string.IsNullOrWhiteSpace(request.IfNoneMatch))
        {
            releasesRequest.Headers.IfNoneMatch.ParseAdd(request.IfNoneMatch);
        }

        using HttpResponseMessage releaseResponse = await client.SendAsync(
            releasesRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (releaseResponse.StatusCode == HttpStatusCode.NotModified)
        {
            return ReleaseDiscoveryResult.NoChange(request.IfNoneMatch);
        }

        if (releaseResponse.StatusCode == HttpStatusCode.Forbidden
            || (int)releaseResponse.StatusCode == 429)
        {
            string retryAfter = releaseResponse.Headers.RetryAfter?.ToString() ?? "unspecified";
            return ReleaseDiscoveryResult.Failed($"GitHub release discovery is throttled. Retry-After: {retryAfter}.");
        }

        if (!releaseResponse.IsSuccessStatusCode)
        {
            return ReleaseDiscoveryResult.Failed(
                $"GitHub release discovery failed with HTTP {(int)releaseResponse.StatusCode}.");
        }

        string? eTag = releaseResponse.Headers.ETag?.Tag;
        await using var releaseStream = await releaseResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        GitHubReleasePayload[]? releases = await JsonSerializer.DeserializeAsync(
            releaseStream,
            GitHubReleaseJsonContext.Default.GitHubReleasePayloadArray,
            cancellationToken).ConfigureAwait(false);

        GitHubReleasePayload? release = SelectRelease(releases ?? [], request.ReleaseChannel);
        if (release is null)
        {
            return ReleaseDiscoveryResult.NoRelease(eTag);
        }

        GitHubReleaseAssetPayload? platformAssetArchive = release.Assets.FirstOrDefault(
            asset => string.Equals(asset.Name, options.PlatformAssetName, StringComparison.OrdinalIgnoreCase));
        GitHubReleaseAssetPayload? platformAsset = release.Assets.FirstOrDefault(
            asset => string.Equals(asset.Name, options.PlatformReleaseAssetName, StringComparison.OrdinalIgnoreCase));
        GitHubReleaseAssetPayload? platformSignatureAsset = release.Assets.FirstOrDefault(
            asset => string.Equals(asset.Name, options.PlatformReleaseSignatureAssetName, StringComparison.OrdinalIgnoreCase));

        if (platformAssetArchive is null
            && (platformAsset is null || string.IsNullOrWhiteSpace(platformAsset.BrowserDownloadUrl)))
        {
            return ReleaseDiscoveryResult.Unsupported(
                $"Release '{release.TagName}' does not contain either the platform artifact '{options.PlatformAssetName}' or the platform release descriptor '{options.PlatformReleaseAssetName}'.",
                eTag);
        }

        string version = NormalizeVersion(release.TagName);
        string fingerprint = ComputeReleaseFingerprint(release, platformAssetArchive, platformAsset, platformSignatureAsset);

        logger.LogDebug(
            "Discovered candidate release {ReleaseTag} for channel {Channel} with platform artifact {AssetName}.",
            release.TagName,
            request.ReleaseChannel,
            platformAssetArchive?.Name ?? platformAsset?.Name ?? options.PlatformReleaseAssetName);

        return ReleaseDiscoveryResult.Available(
            new UpdateReleaseReference(
                ReleaseId: release.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Version: version,
                Channel: request.ReleaseChannel,
                ReleaseFingerprint: fingerprint,
                PlatformArtifactUrl: platformAssetArchive?.BrowserDownloadUrl,
                PlatformReleaseAssetUrl: platformAsset?.BrowserDownloadUrl,
                PlatformReleaseSignatureAssetUrl: platformSignatureAsset?.BrowserDownloadUrl,
                SourceUrl: release.HtmlUrl,
                PublishedAtUtc: release.PublishedAt,
                Tag: release.TagName),
            eTag);
    }

    internal static GitHubReleasePayload? SelectRelease(IReadOnlyList<GitHubReleasePayload> releases, string releaseChannel)
    {
        string normalizedChannel = releaseChannel.Trim();

        foreach (GitHubReleasePayload release in releases)
        {
            if (release.Draft)
            {
                continue;
            }

            if (string.Equals(normalizedChannel, "stable", StringComparison.OrdinalIgnoreCase))
            {
                if (!release.Prerelease)
                {
                    return release;
                }

                continue;
            }

            if (!release.Prerelease)
            {
                continue;
            }

            if (MatchesChannel(release, normalizedChannel))
            {
                return release;
            }
        }

        return null;
    }

    private static bool MatchesChannel(GitHubReleasePayload release, string releaseChannel)
        => ContainsChannelToken(release, releaseChannel);

    private static bool ContainsChannelToken(GitHubReleasePayload release, string channel)
        => release.TagName.Contains(channel, StringComparison.OrdinalIgnoreCase)
            || release.Name.Contains(channel, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeVersion(string tagName)
    {
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return "unknown";
        }

        return tagName.Trim().TrimStart('v', 'V');
    }

    private static string ComputeReleaseFingerprint(
        GitHubReleasePayload release,
        GitHubReleaseAssetPayload? managerAsset,
        GitHubReleaseAssetPayload? platformAsset,
        GitHubReleaseAssetPayload? platformSignatureAsset)
    {
        string material = string.Join(
            '|',
            release.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            release.TagName,
            release.HtmlUrl,
            release.PublishedAt?.UtcDateTime.ToString("O") ?? string.Empty,
            managerAsset?.Name ?? string.Empty,
            managerAsset?.BrowserDownloadUrl ?? string.Empty,
            platformAsset?.Name ?? string.Empty,
            platformAsset?.BrowserDownloadUrl ?? string.Empty,
            platformSignatureAsset?.Name ?? string.Empty,
            platformSignatureAsset?.BrowserDownloadUrl ?? string.Empty);

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexStringLower(hash);
    }
}

internal sealed record GitHubReleasePayload(
    long Id,
    string TagName,
    string Name,
    bool Draft,
    bool Prerelease,
    string HtmlUrl,
    DateTimeOffset? PublishedAt,
    GitHubReleaseAssetPayload[] Assets);

internal sealed record GitHubReleaseAssetPayload(
    string Name,
    string BrowserDownloadUrl);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GitHubReleasePayload[]))]
internal sealed partial class GitHubReleaseJsonContext : JsonSerializerContext
{
}
