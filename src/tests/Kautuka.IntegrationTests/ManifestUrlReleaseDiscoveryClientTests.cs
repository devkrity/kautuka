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
    private const string sourceSha = "0123456789abcdef0123456789abcdef01234567";
    private const string api = "https://api.github.com/repos/devkrity/kautuka/releases?per_page=100&page=";

    [Fact]
    public async Task DiscoverAsyncSelectsHighestStrictPreviewSemVerAndBindsExactReleaseAsync()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        const string tag = "v1.2.3-preview.10";
        string releaseBase = $"https://github.com/devkrity/kautuka/releases/download/{tag}";
        byte[] manifest = CreateManifest(key, "preview", tag);
        string signature = Sign(key, manifest);
        string publicKeyPath = await WritePublicKeyAsync(key.ExportSubjectPublicKeyInfoPem());
        var handler = new RouteHttpMessageHandler(request => request.RequestUri!.AbsoluteUri switch
        {
            api + "1" => JsonResponse(ReleaseRows(
                ("v9.0.0", false, false, true),
                ("v1.2.3-preview.10", false, true, true),
                ("v1.2.3-preview.9", false, true, true),
                ("v9.9.9-preview.100", true, true, true),
                ("beta-v99-preview.1", false, true, true),
                ("v8.0.0-preview.1", false, false, true))),
            var url when url == $"{releaseBase}/kautuka-platform-release.json" => JsonResponse(manifest),
            var url when url == $"{releaseBase}/kautuka-platform-release.sig" => TextResponse(signature),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}"),
        });

        try
        {
            ReleaseDiscoveryResult result = await CreateClient(handler, publicKeyPath).DiscoverAsync(
                new("devkrity", "kautuka", "preview", null),
                TestContext.Current.CancellationToken);

            Assert.NotNull(result.AvailableRelease);
            Assert.Equal(tag, result.AvailableRelease!.Tag);
            Assert.Equal("1.2.3-preview.10", result.AvailableRelease.Version);
            Assert.Equal("preview", result.AvailableRelease.Channel);
            Assert.Equal(sourceSha, result.AvailableRelease.SourceSha);
            Assert.Equal($"{releaseBase}/kautuka-platform-release.json", result.AvailableRelease.PlatformReleaseAssetUrl);
            Assert.Equal($"{releaseBase}/kautuka-platform-release.sig", result.AvailableRelease.PlatformReleaseSignatureAssetUrl);
            Assert.Equal($"{releaseBase}/kautuka-linux-amd64.tar.gz", result.AvailableRelease.PlatformArtifactUrl);
            Assert.All(handler.Requests, request => Assert.Null(request.Headers.Authorization));
        }
        finally
        {
            File.Delete(publicKeyPath);
        }
    }

    [Fact]
    public async Task DiscoverAsyncSelectsHighestStableSemVerRegardlessOfPublicationOrderAsync()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        const string tag = "v2.0.0";
        string releaseBase = $"https://github.com/devkrity/kautuka/releases/download/{tag}";
        byte[] manifest = CreateManifest(key, "stable", tag);
        string publicKeyPath = await WritePublicKeyAsync(key.ExportSubjectPublicKeyInfoPem());
        var handler = new RouteHttpMessageHandler(request => request.RequestUri!.AbsoluteUri switch
        {
            api + "1" => JsonResponse(ReleaseRows(
                ("v2.0.0", false, false, true),
                ("v1.9.9", false, false, true),
                ("v99.0.0-preview.1", false, true, true),
                ("v100.0.0", false, false, false))),
            var url when url == $"{releaseBase}/kautuka-platform-release.json" => JsonResponse(manifest),
            var url when url == $"{releaseBase}/kautuka-platform-release.sig" => TextResponse(Sign(key, manifest)),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}"),
        });

        try
        {
            ReleaseDiscoveryResult result = await CreateClient(handler, publicKeyPath).DiscoverAsync(
                new("devkrity", "kautuka", "stable", null),
                TestContext.Current.CancellationToken);

            Assert.NotNull(result.AvailableRelease);
            Assert.Equal(tag, result.AvailableRelease!.Tag);
            Assert.Equal("stable", result.AvailableRelease.Channel);
        }
        finally
        {
            File.Delete(publicKeyPath);
        }
    }

    [Fact]
    public async Task DiscoverAsyncRejectsManifestForDifferentSelectedTagBeforeSignatureDownloadAsync()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        const string selectedTag = "v1.2.3-preview.4";
        string selectedBase = $"https://github.com/devkrity/kautuka/releases/download/{selectedTag}";
        byte[] manifest = CreateManifest(key, "preview", "v1.2.3-preview.3");
        string publicKeyPath = await WritePublicKeyAsync(key.ExportSubjectPublicKeyInfoPem());
        var handler = new RouteHttpMessageHandler(request => request.RequestUri!.AbsoluteUri switch
        {
            api + "1" => JsonResponse(ReleaseRows((selectedTag, false, true, true))),
            var url when url == $"{selectedBase}/kautuka-platform-release.json" => JsonResponse(manifest),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}"),
        });

        try
        {
            ReleaseDiscoveryResult result = await CreateClient(handler, publicKeyPath).DiscoverAsync(
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

    [Fact]
    public async Task DiscoverAsyncRejectsTamperedSelectedManifestAsync()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        const string tag = "v1.2.3-preview.4";
        string releaseBase = $"https://github.com/devkrity/kautuka/releases/download/{tag}";
        byte[] signed = CreateManifest(key, "preview", tag);
        byte[] tampered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(signed).Replace(sourceSha, new string('f', 40), StringComparison.Ordinal));
        string publicKeyPath = await WritePublicKeyAsync(key.ExportSubjectPublicKeyInfoPem());
        var handler = new RouteHttpMessageHandler(request => request.RequestUri!.AbsoluteUri switch
        {
            api + "1" => JsonResponse(ReleaseRows((tag, false, true, true))),
            var url when url == $"{releaseBase}/kautuka-platform-release.json" => JsonResponse(tampered),
            var url when url == $"{releaseBase}/kautuka-platform-release.sig" => TextResponse(Sign(key, signed)),
            _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}"),
        });

        try
        {
            ReleaseDiscoveryResult result = await CreateClient(handler, publicKeyPath).DiscoverAsync(
                new("devkrity", "kautuka", "preview", null),
                TestContext.Current.CancellationToken);
            Assert.Null(result.AvailableRelease);
            Assert.True(result.IsUnsupported);
        }
        finally
        {
            File.Delete(publicKeyPath);
        }
    }

    [Fact]
    public async Task DiscoverAsyncFailsClosedWhenSameVersionAppearsTwiceAsync()
    {
        string publicKeyPath = await WritePublicKeyAsync("unused");
        var handler = new RouteHttpMessageHandler(request => request.RequestUri!.AbsoluteUri == api + "1"
            ? JsonResponse(ReleaseRows(("v1.2.3-preview.4", false, true, true), ("v1.2.3-preview.4", false, true, true)))
            : throw new InvalidOperationException($"Unexpected request: {request.RequestUri}"));
        try
        {
            ReleaseDiscoveryResult result = await CreateClient(handler, publicKeyPath).DiscoverAsync(
                new("devkrity", "kautuka", "preview", null), TestContext.Current.CancellationToken);
            Assert.Null(result.AvailableRelease);
            Assert.NotNull(result.FailureReason);
            Assert.Single(handler.Requests);
        }
        finally
        {
            File.Delete(publicKeyPath);
        }
    }

    [Fact]
    public async Task DiscoverAsyncFailsClosedOnMalformedReleaseMetadataAsync()
    {
        string publicKeyPath = await WritePublicKeyAsync("unused");
        var handler = new RouteHttpMessageHandler(request => request.RequestUri!.AbsoluteUri == api + "1"
            ? JsonResponse(new[] { new { tag_name = "v1.0.0-preview.1", draft = "false", prerelease = true, published_at = "2026-10-29T00:00:00Z" } })
            : throw new InvalidOperationException($"Unexpected request: {request.RequestUri}"));
        try
        {
            ReleaseDiscoveryResult result = await CreateClient(handler, publicKeyPath).DiscoverAsync(
                new("devkrity", "kautuka", "preview", null), TestContext.Current.CancellationToken);
            Assert.Null(result.AvailableRelease);
            Assert.Contains("malformed release metadata", result.FailureReason, StringComparison.Ordinal);
            Assert.Single(handler.Requests);
        }
        finally
        {
            File.Delete(publicKeyPath);
        }
    }

    [Fact]
    public async Task DiscoverAsyncAcceptsExactly1000AndRejects1001ReleasesAsync()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string publicKeyPath = await WritePublicKeyAsync(key.ExportSubjectPublicKeyInfoPem());
        var thousand = Enumerable.Range(0, 1000).Select(index => ($"v1.0.{index}", false, false, true)).ToArray();
        const string selectedTag = "v1.0.999";
        string releaseBase = $"https://github.com/devkrity/kautuka/releases/download/{selectedTag}";
        byte[] manifest = CreateManifest(key, "stable", selectedTag);
        var exactHandler = new RouteHttpMessageHandler(request =>
        {
            string requestUrl = request.RequestUri!.AbsoluteUri;
            if (requestUrl == $"{releaseBase}/kautuka-platform-release.json") return JsonResponse(manifest);
            if (requestUrl == $"{releaseBase}/kautuka-platform-release.sig") return TextResponse(Sign(key, manifest));
            int page = int.Parse(request.RequestUri!.Query.Split("&page=")[1], System.Globalization.CultureInfo.InvariantCulture);
            return JsonResponse(page == 11 ? Array.Empty<object>() : thousand.Skip((page - 1) * 100).Take(100).Select(item => (object)ReleaseRow(item.Item1, item.Item2, item.Item3, item.Item4)).ToArray());
        });
        var overflowHandler = new RouteHttpMessageHandler(request =>
        {
            int page = int.Parse(request.RequestUri!.Query.Split("&page=")[1], System.Globalization.CultureInfo.InvariantCulture);
            if (page == 11) return JsonResponse(new[] { ReleaseRow("v999.0.0", false, false, true) });
            return JsonResponse(thousand.Skip((page - 1) * 100).Take(100).Select(item => (object)ReleaseRow(item.Item1, item.Item2, item.Item3, item.Item4)).ToArray());
        });

        try
        {
            ReleaseDiscoveryResult exact = await CreateClient(exactHandler, publicKeyPath).DiscoverAsync(
                new("devkrity", "kautuka", "stable", null), TestContext.Current.CancellationToken);
            Assert.NotNull(exact.AvailableRelease);
            Assert.Equal(selectedTag, exact.AvailableRelease!.Tag);
            Assert.Equal(13, exactHandler.Requests.Count);

            ReleaseDiscoveryResult overflow = await CreateClient(overflowHandler, publicKeyPath).DiscoverAsync(
                new("devkrity", "kautuka", "stable", null), TestContext.Current.CancellationToken);
            Assert.Null(overflow.AvailableRelease);
            Assert.NotNull(overflow.FailureReason);
            Assert.Equal(11, overflowHandler.Requests.Count);
        }
        finally
        {
            File.Delete(publicKeyPath);
        }
    }

    private static ManifestUrlReleaseDiscoveryClient CreateClient(HttpMessageHandler handler, string publicKeyPath)
        => new(new StubHttpClientFactory(handler), Options.Create(new UpdateOptions
        {
            RepositoryOwner = "devkrity",
            RepositoryName = "kautuka",
            ManifestPublicKeyPath = publicKeyPath,
            PlatformAssetName = "kautuka-linux-amd64.tar.gz",
            PlatformReleaseAssetName = "kautuka-platform-release.json",
            PlatformReleaseSignatureAssetName = "kautuka-platform-release.sig",
        }), NullLogger<ManifestUrlReleaseDiscoveryClient>.Instance);

    private static byte[] CreateManifest(ECDsa key, string channel, string tag)
    {
        string releaseBase = $"https://github.com/devkrity/kautuka/releases/download/{tag}";
        var manifest = new
        {
            schemaVersion = 2,
            releaseId = tag,
            releaseTag = tag,
            channel,
            version = tag[1..],
            sourceSha,
            publishedAt = "2026-10-29T00:00:00Z",
            signature = new { algorithm = "ecdsa-p256-sha256", path = "kautuka-platform-release.sig", url = $"{releaseBase}/kautuka-platform-release.sig", format = "base64" },
            artifacts = new[] { new { name = "kautuka-linux-amd64.tar.gz", platform = "linux-x64", path = "kautuka-linux-amd64.tar.gz", url = $"{releaseBase}/kautuka-linux-amd64.tar.gz", sha256 = new string('a', 64), size = 42 } },
        };
        _ = key;
        return JsonSerializer.SerializeToUtf8Bytes(manifest);
    }

    private static string Sign(ECDsa key, byte[] bytes)
        => Convert.ToBase64String(key.SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));

    private static object[] ReleaseRows(params (string Tag, bool Draft, bool Prerelease, bool Published)[] rows)
        => rows.Select(row => (object)ReleaseRow(row.Tag, row.Draft, row.Prerelease, row.Published)).ToArray();

    private static object ReleaseRow(string tag, bool draft, bool prerelease, bool published)
        => new { tag_name = tag, draft, prerelease, published_at = published ? "2026-10-29T00:00:00Z" : null };

    private static async Task<string> WritePublicKeyAsync(string pem)
    {
        string path = Path.Combine(Path.GetTempPath(), $"kautuka-manifest-public-{Guid.NewGuid():N}.pem");
        await File.WriteAllTextAsync(path, pem, TestContext.Current.CancellationToken);
        return path;
    }

    private static HttpResponseMessage JsonResponse<T>(T value)
        => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage JsonResponse(byte[] value)
        => new(HttpStatusCode.OK) { Content = new ByteArrayContent(value) { Headers = { ContentType = new("application/json") } } };

    private static HttpResponseMessage TextResponse(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) };

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RouteHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> route) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(route(request));
        }
    }
}
