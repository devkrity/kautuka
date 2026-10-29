using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Kautuka.UpdateService.Discovery;
using Kautuka.UpdateService.Hosting;
using Kautuka.UpdateService.Models;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kautuka.IntegrationTests;

public sealed class ManifestUrlReleaseDiscoveryClientTests
{
    private static readonly JsonSerializerOptions manifestSerializerOptions = new() { WriteIndented = true };
    private const string sourceSha = "0123456789abcdef0123456789abcdef01234567";
    private const string releaseBase = "https://github.com/devkrity/kautuka/releases/download/v1.2.3-preview.4";
    private const string previewPointer = "https://raw.githubusercontent.com/devkrity/kautuka/release-channel/preview.json";
    private const string stablePointer = "https://raw.githubusercontent.com/devkrity/kautuka/release-channel/stable.json";

    [Fact]
    public async Task DiscoverAsyncReadsOnlySelectedSignedPointerAndBindsExactTagAssetsAsync()
    {
        using ECDsa signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = signingKey.ExportSubjectPublicKeyInfoPem();
        byte[] manifest = CreateManifest(signingKey, "preview", "v1.2.3-preview.4", sourceSha);
        string signature = Convert.ToBase64String(signingKey.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        string publicKeyPath = await WritePublicKeyAsync(publicKey);
        var handler = new MapHttpMessageHandler(new Dictionary<string, HttpResponseMessage>
        {
            [previewPointer] = JsonResponse(manifest, "\"pointer-1\""),
            [$"{releaseBase}/kautuka-platform-release.sig"] = TextResponse(signature),
        });

        try
        {
            var client = CreateClient(handler, publicKeyPath);
            var result = await client.DiscoverAsync(
                new("devkrity", "kautuka", "preview", null),
                TestContext.Current.CancellationToken);

            Assert.NotNull(result.AvailableRelease);
            Assert.Equal("v1.2.3-preview.4", result.AvailableRelease!.Tag);
            Assert.Equal("1.2.3-preview.4", result.AvailableRelease.Version);
            Assert.Equal("preview", result.AvailableRelease.Channel);
            Assert.Equal(sourceSha, result.AvailableRelease.SourceSha);
            Assert.Equal(previewPointer, result.AvailableRelease.PlatformReleaseAssetUrl);
            Assert.Equal($"{releaseBase}/kautuka-platform-release.sig", result.AvailableRelease.PlatformReleaseSignatureAssetUrl);
            Assert.Equal($"{releaseBase}/kautuka-linux-amd64.tar.gz", result.AvailableRelease.PlatformArtifactUrl);
            Assert.Equal(new[] { previewPointer, $"{releaseBase}/kautuka-platform-release.sig" }, handler.Requests.Select(request => request.RequestUri!.AbsoluteUri));
            Assert.All(handler.Requests, request => Assert.Null(request.Headers.Authorization));
        }
        finally
        {
            File.Delete(publicKeyPath);
        }
    }

    [Fact]
    public async Task DiscoverAsyncReadsStablePointerAndRequiresAnExactStableTagAsync()
    {
        using ECDsa signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKey = signingKey.ExportSubjectPublicKeyInfoPem();
        const string stableTag = "v1.3.0";
        string stableBase = $"https://github.com/devkrity/kautuka/releases/download/{stableTag}";
        byte[] manifest = CreateManifest(signingKey, "stable", stableTag, sourceSha);
        string signature = Convert.ToBase64String(signingKey.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        string publicKeyPath = await WritePublicKeyAsync(publicKey);
        var handler = new MapHttpMessageHandler(new Dictionary<string, HttpResponseMessage>
        {
            [stablePointer] = JsonResponse(manifest),
            [$"{stableBase}/kautuka-platform-release.sig"] = TextResponse(signature),
        });

        try
        {
            ReleaseDiscoveryResult result = await CreateClient(handler, publicKeyPath).DiscoverAsync(
                new("devkrity", "kautuka", "stable", null),
                TestContext.Current.CancellationToken);

            Assert.NotNull(result.AvailableRelease);
            Assert.Equal(stableTag, result.AvailableRelease!.Tag);
            Assert.Equal("stable", result.AvailableRelease.Channel);
            Assert.Equal(stablePointer, result.AvailableRelease.PlatformReleaseAssetUrl);
            Assert.Equal($"{stableBase}/kautuka-linux-amd64.tar.gz", result.AvailableRelease.PlatformArtifactUrl);
            Assert.Equal(new[] { stablePointer, $"{stableBase}/kautuka-platform-release.sig" }, handler.Requests.Select(request => request.RequestUri!.AbsoluteUri));
        }
        finally
        {
            File.Delete(publicKeyPath);
        }
    }

    [Fact]
    public async Task DiscoverAsyncRejectsTamperedPointerBeforeOfferingCandidateAsync()
    {
        using ECDsa signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKeyPath = await WritePublicKeyAsync(signingKey.ExportSubjectPublicKeyInfoPem());
        byte[] manifest = CreateManifest(signingKey, "preview", "v1.2.3-preview.4", sourceSha);
        string signature = Convert.ToBase64String(signingKey.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        byte[] tampered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(manifest).Replace(sourceSha, "fedcba9876543210fedcba9876543210fedcba98", StringComparison.Ordinal));
        var handler = new MapHttpMessageHandler(new Dictionary<string, HttpResponseMessage>
        {
            [previewPointer] = JsonResponse(tampered),
            [$"{releaseBase}/kautuka-platform-release.sig"] = TextResponse(signature),
        });

        try
        {
            var result = await CreateClient(handler, publicKeyPath).DiscoverAsync(
                new("devkrity", "kautuka", "preview", null),
                TestContext.Current.CancellationToken);

            Assert.Null(result.AvailableRelease);
            Assert.True(result.IsUnsupported);
            Assert.Equal(2, handler.Requests.Count);
        }
        finally
        {
            File.Delete(publicKeyPath);
        }
    }

    [Theory]
    [InlineData("stable", "v1.2.3-preview.4", "preview", "1.2.3-preview.4")]
    [InlineData("preview", "v1.2.3-preview.04", "preview", "1.2.3-preview.04")]
    [InlineData("preview", "v1.2.3-preview.4", "stable", "1.2.3-preview.4")]
    public async Task DiscoverAsyncRejectsWrongChannelOrMalformedTag(string requestedChannel, string tag, string manifestChannel, string version)
    {
        using ECDsa signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKeyPath = await WritePublicKeyAsync(signingKey.ExportSubjectPublicKeyInfoPem());
        byte[] manifest = CreateManifest(signingKey, manifestChannel, tag, sourceSha, version);
        string signature = Convert.ToBase64String(signingKey.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        string pointer = requestedChannel == "stable" ? stablePointer : previewPointer;
        var handler = new MapHttpMessageHandler(new Dictionary<string, HttpResponseMessage>
        {
            [pointer] = JsonResponse(manifest),
            [$"https://github.com/devkrity/kautuka/releases/download/{tag}/kautuka-platform-release.sig"] = TextResponse(signature),
        });

        try
        {
            var result = await CreateClient(handler, publicKeyPath).DiscoverAsync(
                new("devkrity", "kautuka", requestedChannel, null),
                TestContext.Current.CancellationToken);
            Assert.Null(result.AvailableRelease);
        }
        finally
        {
            File.Delete(publicKeyPath);
        }
    }

    [Fact]
    public async Task DiscoverAsyncRejectsNonExactSignatureOrArtifactUrlBeforeFetchingSignatureAsync()
    {
        using ECDsa signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKeyPath = await WritePublicKeyAsync(signingKey.ExportSubjectPublicKeyInfoPem());
        byte[] manifest = CreateManifest(signingKey, "preview", "v1.2.3-preview.4", sourceSha, signatureUrl: "https://example.invalid/manifest.sig");
        var handler = new MapHttpMessageHandler(new Dictionary<string, HttpResponseMessage>
        {
            [previewPointer] = JsonResponse(manifest),
        });

        try
        {
            var result = await CreateClient(handler, publicKeyPath).DiscoverAsync(
                new("devkrity", "kautuka", "preview", null),
                TestContext.Current.CancellationToken);
            Assert.Null(result.AvailableRelease);
            Assert.True(result.IsUnsupported);
            Assert.Single(handler.Requests);
        }
        finally
        {
            File.Delete(publicKeyPath);
        }
    }

    [Fact]
    public async Task DiscoverAsyncRejectsNonExactArtifactUrlBeforeFetchingSignatureAsync()
    {
        using ECDsa signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKeyPath = await WritePublicKeyAsync(signingKey.ExportSubjectPublicKeyInfoPem());
        byte[] manifest = CreateManifest(signingKey, "preview", "v1.2.3-preview.4", sourceSha, artifactUrl: "https://example.invalid/archive.tar.gz");
        var handler = new MapHttpMessageHandler(new Dictionary<string, HttpResponseMessage>
        {
            [previewPointer] = JsonResponse(manifest),
        });

        try
        {
            var result = await CreateClient(handler, publicKeyPath).DiscoverAsync(
                new("devkrity", "kautuka", "preview", null),
                TestContext.Current.CancellationToken);
            Assert.Null(result.AvailableRelease);
            Assert.True(result.IsUnsupported);
            Assert.Single(handler.Requests);
        }
        finally
        {
            File.Delete(publicKeyPath);
        }
    }

    [Fact]
    public async Task DiscoverAsyncUsesPointerEtagForNotModifiedResponseAsync()
    {
        var handler = new MapHttpMessageHandler(new Dictionary<string, HttpResponseMessage>
        {
            [previewPointer] = new(HttpStatusCode.NotModified),
        });
        var client = CreateClient(handler, "unused");

        ReleaseDiscoveryResult result = await client.DiscoverAsync(
            new("devkrity", "kautuka", "preview", "\"pointer-1\""),
            TestContext.Current.CancellationToken);

        Assert.True(result.NotModified);
        Assert.Single(handler.Requests);
        Assert.Contains(handler.Requests[0].Headers.IfNoneMatch, value => value.Tag == "\"pointer-1\"");
    }

    private static ManifestUrlReleaseDiscoveryClient CreateClient(HttpMessageHandler handler, string publicKeyPath)
        => new(
            new StubHttpClientFactory(handler),
            Options.Create(new UpdateOptions
            {
                RepositoryOwner = "devkrity",
                RepositoryName = "kautuka",
                PreviewReleaseManifestUrl = previewPointer,
                StableReleaseManifestUrl = stablePointer,
                ManifestPublicKeyPath = publicKeyPath,
                PlatformAssetName = "kautuka-linux-amd64.tar.gz",
                PlatformReleaseAssetName = "kautuka-platform-release.json",
                PlatformReleaseSignatureAssetName = "kautuka-platform-release.sig",
            }),
            NullLogger<ManifestUrlReleaseDiscoveryClient>.Instance);

    private static byte[] CreateManifest(
        ECDsa key,
        string channel,
        string tag,
        string sha,
        string? version = null,
        string? signatureUrl = null,
        string? artifactUrl = null)
    {
        string releaseBaseForTag = $"https://github.com/devkrity/kautuka/releases/download/{tag}";
        var manifest = new
        {
            schemaVersion = 2,
            releaseId = tag,
            releaseTag = tag,
            channel,
            version = version ?? tag[1..],
            sourceSha = sha,
            publishedAt = "2026-10-29T00:00:00Z",
            signature = new
            {
                algorithm = "ecdsa-p256-sha256",
                path = "kautuka-platform-release.sig",
                url = signatureUrl ?? $"{releaseBaseForTag}/kautuka-platform-release.sig",
                format = "base64",
            },
            artifacts = new[]
            {
                new
                {
                    name = "kautuka-linux-amd64.tar.gz",
                    platform = "linux-x64",
                    path = "kautuka-linux-amd64.tar.gz",
                    url = artifactUrl ?? $"{releaseBaseForTag}/kautuka-linux-amd64.tar.gz",
                    sha256 = new string('a', 64),
                    size = 42,
                },
            },
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, manifestSerializerOptions);
        _ = key;
        return bytes;
    }

    private static async Task<string> WritePublicKeyAsync(string pem)
    {
        string path = Path.Combine(Path.GetTempPath(), $"kautuka-manifest-public-{Guid.NewGuid():N}.pem");
        await File.WriteAllTextAsync(path, pem, TestContext.Current.CancellationToken);
        return path;
    }

    private static HttpResponseMessage JsonResponse(byte[] body, string? etag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body),
        };
        response.Content.Headers.ContentType = new("application/json");
        if (etag is not null) response.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse(etag);
        return response;
    }

    private static HttpResponseMessage TextResponse(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class MapHttpMessageHandler(IReadOnlyDictionary<string, HttpResponseMessage> responses) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            string url = request.RequestUri!.AbsoluteUri;
            if (!responses.TryGetValue(url, out HttpResponseMessage? response))
            {
                throw new InvalidOperationException($"Unexpected request URL: {url}");
            }

            return Task.FromResult(response);
        }
    }
}
