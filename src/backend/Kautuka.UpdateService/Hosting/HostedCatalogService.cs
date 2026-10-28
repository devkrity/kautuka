using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

using Kautuka.UpdateService.Abstractions;
using Kautuka.UpdateService.Models;
using Kautuka.UpdateService.State;

using Microsoft.Extensions.Options;

namespace Kautuka.UpdateService.Hosting;

internal sealed class HostedCatalogService(
    IHostedCatalogStateStore stateStore,
    IHttpClientFactory httpClientFactory,
    IOptions<UpdateOptions> options,
    ILogger<HostedCatalogService> logger) : IHostedCatalogService
{
    private readonly IHostedCatalogStateStore stateStore = stateStore;
    private readonly IHttpClientFactory httpClientFactory = httpClientFactory;
    private readonly UpdateOptions options = options.Value;
    private readonly ILogger<HostedCatalogService> logger = logger;
    private readonly SemaphoreSlim refreshGate = new(1, 1);

    public async ValueTask<HostedCatalogSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        => await stateStore.GetAsync(cancellationToken).ConfigureAwait(false);

    public async Task RefreshSnapshotAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.CatalogManifestUrl)
            || !Uri.TryCreate(options.CatalogManifestUrl, UriKind.Absolute, out Uri? manifestUri))
        {
            logger.LogDebug("Skipping hosted catalog refresh because no catalog manifest URL is configured.");
            return;
        }

        if (!await refreshGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            logger.LogDebug("Skipping hosted catalog refresh because another refresh attempt is already in flight.");
            return;
        }

        try
        {
            HostedCatalogSnapshot current = await stateStore.GetAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset now = DateTimeOffset.UtcNow;

            HttpClient client = httpClientFactory.CreateClient("kautuka.updateservice.github");
            client.DefaultRequestHeaders.UserAgent.Clear();
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("KautukaUpdateService", "1.0"));

            using HttpRequestMessage request = new(HttpMethod.Get, manifestUri);
            if (!string.IsNullOrWhiteSpace(current.SourceEtag))
            {
                request.Headers.IfNoneMatch.ParseAdd(current.SourceEtag);
            }

            using HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                HostedCatalogSnapshot unchanged = current with
                {
                    RefreshedAtUtc = now,
                    LastRefreshAttemptUtc = now,
                    LastRefreshSuccessUtc = now,
                    SourceManifestUrl = manifestUri.ToString(),
                    LastError = null,
                };

                await stateStore.SaveAsync(unchanged, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                await SaveFailureAsync(
                    current,
                    manifestUri,
                    now,
                    $"Hosted catalog refresh failed with HTTP {(int)response.StatusCode}.",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            byte[] manifestBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            string? signatureUrl = null;
            HostedCatalogManifestDescriptor? descriptor = JsonSerializer.Deserialize(
                manifestBytes,
                HostedCatalogManifestDescriptorJsonContext.Default.HostedCatalogManifestDescriptor);

            if (descriptor is null)
            {
                await SaveFailureAsync(current, manifestUri, now, "Hosted catalog manifest returned an empty descriptor.", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (descriptor.Signature is not null)
            {
                signatureUrl = ResolveAssetUrl(manifestUri, descriptor.Signature.Url, descriptor.Signature.Path);
                string? verificationError = await VerifyManifestSignatureAsync(manifestBytes, signatureUrl, cancellationToken).ConfigureAwait(false);
                if (verificationError is not null)
                {
                    await SaveFailureAsync(current, manifestUri, now, verificationError, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
            else if (ReleaseTrustPolicy.RequiresVerification(options))
            {
                await SaveFailureAsync(
                    current,
                    manifestUri,
                    now,
                    "Hosted catalog manifest is missing signature metadata. Fail-closed verification rejects unsigned catalogs.",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (ReleaseTrustPolicy.RequiresVerification(options))
            {
                HostedCatalogManifestEntryDescriptor? missingHash = descriptor.Entries
                    .FirstOrDefault(static entry => string.IsNullOrWhiteSpace(entry.ArtifactSha256));
                if (missingHash is not null)
                {
                    await SaveFailureAsync(
                        current,
                        manifestUri,
                        now,
                        $"Hosted catalog entry '{missingHash.Id}' is missing artifact SHA-256.",
                        cancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            HostedCatalogSnapshot updated = new(
                SchemaVersion: Math.Max(1, descriptor.SchemaVersion),
                RefreshedAtUtc: now,
                LastRefreshAttemptUtc: now,
                LastRefreshSuccessUtc: now,
                SourceManifestUrl: manifestUri.ToString(),
                SourceEtag: response.Headers.ETag?.Tag ?? current.SourceEtag,
                LastError: null,
                Entries:
                [
                    .. descriptor.Entries.Select(entry => new HostedCatalogEntry(
                        Id: entry.Id,
                        Kind: entry.Kind,
                        Title: entry.Title,
                        Summary: entry.Summary,
                        Publisher: entry.Publisher,
                        Version: entry.Version,
                        ArtifactUrl: ResolveAssetUrl(manifestUri, entry.ArtifactUrl, entry.ArtifactPath),
                        ArtifactSha256: entry.ArtifactSha256,
                        TrustKind: ResolveTrustKind(entry),
                        Recommended: entry.Recommended))
                ]);

            await stateStore.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            HostedCatalogSnapshot current = await stateStore.GetAsync(CancellationToken.None).ConfigureAwait(false);
            await SaveFailureAsync(
                current,
                options.CatalogManifestUrl,
                DateTimeOffset.UtcNow,
                ex.Message,
                CancellationToken.None).ConfigureAwait(false);
            logger.LogWarning(ex, "Hosted catalog refresh failed.");
        }
        finally
        {
            refreshGate.Release();
        }
    }

    private async Task SaveFailureAsync(
        HostedCatalogSnapshot current,
        Uri manifestUri,
        DateTimeOffset now,
        string error,
        CancellationToken cancellationToken)
    {
        HostedCatalogSnapshot failed = current with
        {
            LastRefreshAttemptUtc = now,
            SourceManifestUrl = manifestUri.ToString(),
            LastError = error,
        };

        await stateStore.SaveAsync(failed, cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveFailureAsync(
        HostedCatalogSnapshot current,
        string? manifestUrl,
        DateTimeOffset now,
        string error,
        CancellationToken cancellationToken)
    {
        HostedCatalogSnapshot failed = current with
        {
            LastRefreshAttemptUtc = now,
            SourceManifestUrl = manifestUrl,
            LastError = error,
        };

        await stateStore.SaveAsync(failed, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> VerifyManifestSignatureAsync(byte[] manifestBytes, string? signatureUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.CatalogManifestPublicKeyPath))
        {
            return "Hosted catalog manifest signature metadata was present but no catalog public key path is configured.";
        }

        if (string.IsNullOrWhiteSpace(signatureUrl)
            || !Uri.TryCreate(signatureUrl, UriKind.Absolute, out Uri? signatureUri))
        {
            return "Hosted catalog manifest signature metadata was present but no signature URL could be resolved.";
        }

        HttpClient client = httpClientFactory.CreateClient("kautuka.updateservice.github");
        string signatureContent = await client.GetStringAsync(signatureUri, cancellationToken).ConfigureAwait(false);
        string publicKeyPem = await File.ReadAllTextAsync(options.CatalogManifestPublicKeyPath, cancellationToken).ConfigureAwait(false);

        return PlatformReleaseSignatureVerifier.TryVerify(manifestBytes, signatureContent, publicKeyPem, out string? verificationError)
            ? null
            : verificationError;
    }

    private static string ResolveTrustKind(HostedCatalogManifestEntryDescriptor entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.TrustKind))
        {
            return entry.TrustKind;
        }

        return string.IsNullOrWhiteSpace(entry.ArtifactSha256)
            ? "third_party_unverified"
            : "verified_by_kautuka";
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
}
