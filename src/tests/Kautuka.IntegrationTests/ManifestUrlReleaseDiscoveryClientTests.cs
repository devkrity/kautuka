using System.Text;

using Kautuka.UpdateService.Discovery;
using Kautuka.UpdateService.Hosting;
using Kautuka.UpdateService.Models;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kautuka.IntegrationTests;

public sealed class ManifestUrlReleaseDiscoveryClientTests
{
    [Fact]
    public async Task DiscoverAsyncReturnsManifestBackedReleaseAsync()
    {
        const string manifestUrl = "http://127.0.0.1:20300/manifest";
        const string manifestJson = """
        {
          "schemaVersion": 2,
                    "releaseId": "local-feature-autopilot-enhancements-20260525",
                    "releaseTag": "v0.1.0-local",
          "channel": "preview",
          "version": "0.1.0-local",
          "publishedAt": "2026-05-05T00:00:00Z",
          "signature": {
            "algorithm": "ecdsa-p256-sha256",
            "path": "kautuka-platform-release.sig",
            "format": "base64"
          },
          "artifacts": [
            {
              "name": "kautuka-platform-linux-x64.tar.gz",
              "platform": "linux-x64",
              "path": "kautuka-platform-linux-x64.tar.gz",
              "sha256": "abc123",
              "size": 42
            }
                    ]
        }
        """;

        var options = Options.Create(new UpdateOptions
        {
            ReleaseManifestUrl = manifestUrl,
            PlatformAssetName = "kautuka-platform-linux-x64.tar.gz"
        });

        var client = new ManifestUrlReleaseDiscoveryClient(
            new StubHttpClientFactory(new StubHttpMessageHandler(manifestUrl, manifestJson)),
            options,
            NullLogger<ManifestUrlReleaseDiscoveryClient>.Instance);

        ReleaseDiscoveryResult result = await client.DiscoverAsync(
            new ReleaseDiscoveryRequest("shukla-amit", "kautuka", "preview", null),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.NotNull(result.AvailableRelease);
        Assert.Equal("0.1.0-local", result.AvailableRelease!.ReleaseId);
        Assert.Equal("0.1.0-local", result.AvailableRelease.Version);
        Assert.Equal(manifestUrl, result.AvailableRelease.PlatformReleaseAssetUrl);
        Assert.Equal("http://127.0.0.1:20300/kautuka-platform-linux-x64.tar.gz", result.AvailableRelease.PlatformArtifactUrl);
        Assert.Equal("http://127.0.0.1:20300/kautuka-platform-release.sig", result.AvailableRelease.PlatformReleaseSignatureAssetUrl);
    }

    [Fact]
    public async Task DiscoverAsyncUsesChannelSpecificManifestUrlWhenConfiguredAsync()
    {
        const string stableManifestUrl = "https://kautuka.dev/manifest/stable";
        const string manifestJson = """
                {
                    "schemaVersion": 2,
                    "releaseTag": "v0.1.1",
                    "channel": "stable",
                    "version": "0.1.1",
                    "publishedAt": "2026-05-05T00:00:00Z",
                    "artifacts": [
                        {
                            "name": "kautuka-platform-linux-x64.tar.gz",
                            "platform": "linux-x64",
                            "path": "kautuka-platform-linux-x64.tar.gz",
                            "sha256": "abc123",
                            "size": 42
                        }
                    ]
                }
                """;

        var options = Options.Create(new UpdateOptions
        {
            PreviewReleaseManifestUrl = "https://kautuka.dev/manifest/preview",
            StableReleaseManifestUrl = stableManifestUrl,
            PlatformAssetName = "kautuka-platform-linux-x64.tar.gz"
        });

        var client = new ManifestUrlReleaseDiscoveryClient(
                new StubHttpClientFactory(new StubHttpMessageHandler(stableManifestUrl, manifestJson)),
                options,
                NullLogger<ManifestUrlReleaseDiscoveryClient>.Instance);

        ReleaseDiscoveryResult result = await client.DiscoverAsync(
                new ReleaseDiscoveryRequest("shukla-amit", "kautuka", "stable", null),
                TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(stableManifestUrl, result.AvailableRelease?.PlatformReleaseAssetUrl);
        Assert.Equal("https://kautuka.dev/manifest/kautuka-platform-linux-x64.tar.gz", result.AvailableRelease?.PlatformArtifactUrl);
    }

    private sealed class StubHttpClientFactory(System.Net.Http.HttpMessageHandler handler) : IHttpClientFactory
    {
        public System.Net.Http.HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StubHttpMessageHandler(string expectedUrl, string responseBody) : System.Net.Http.HttpMessageHandler
    {
        private readonly string expectedUrl = expectedUrl;
        private readonly string responseBody = responseBody;

        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(expectedUrl, request.RequestUri?.ToString());

            return Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(responseBody, Encoding.UTF8, "application/json")
            });
        }
    }
}
