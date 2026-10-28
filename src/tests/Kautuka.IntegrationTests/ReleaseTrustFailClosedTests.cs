using System.Security.Cryptography;
using System.Text;

using Kautuka.Telemetry.Security;
using Kautuka.UpdateService.Abstractions;
using Kautuka.UpdateService.Hosting;
using Kautuka.UpdateService.Models;
using Kautuka.UpdateService.State;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Kautuka.IntegrationTests;

public sealed class ReleaseTrustFailClosedTests
{
    [Fact]
    public void OptionsValidationRequiresManifestPublicKeyWhenFailClosed()
    {
        var validator = new UpdateOptionsValidation();
        ValidateOptionsResult result = validator.Validate(
            Options.DefaultName,
            new UpdateOptions
            {
                Enabled = true,
                StateRootPath = Path.GetTempPath(),
                RepositoryOwner = "kautuka",
                RepositoryName = "kautuka",
                AllowUnsignedLocalFeeds = false,
            });

        Assert.True(result.Failed);
        Assert.Contains("ManifestPublicKeyPath", result.FailureMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionsValidationAllowsMissingKeyWhenUnsignedLocalFeedsEnabled()
    {
        var validator = new UpdateOptionsValidation();
        ValidateOptionsResult result = validator.Validate(
            Options.DefaultName,
            new UpdateOptions
            {
                Enabled = true,
                StateRootPath = Path.GetTempPath(),
                RepositoryOwner = "kautuka",
                RepositoryName = "kautuka",
                AllowUnsignedLocalFeeds = true,
            });

        Assert.False(result.Failed);
    }

    [Fact]
    public void HashVerifierRejectsMissingSha256WhenRequired()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                ReleaseArtifactHashVerifier.Validate(path, expectedSha256: null, expectedSize: 4, requireHash: true, artifactName: "pkg"));
            Assert.Contains("missing SHA-256", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CatalogRefreshRejectsMissingSignatureWhenFailClosedAsync()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"kautuka-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        string publicKeyPath = Path.Combine(tempRoot, "pub.pem");
        using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await File.WriteAllTextAsync(publicKeyPath, ecdsa.ExportSubjectPublicKeyInfoPem(), TestContext.Current.CancellationToken).ConfigureAwait(true);

        const string catalogUrl = "http://127.0.0.1:20313/catalog/manifest";
        const string catalogJson = """
            {
              "schemaVersion": 1,
              "entries": [
                {
                  "id": "skill-a",
                  "kind": "skill",
                  "title": "A",
                  "summary": "s",
                  "publisher": "kautuka",
                  "version": "1.0.0",
                  "artifactUrl": "http://127.0.0.1:20313/a.zip",
                  "artifactSha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
                }
              ]
            }
            """;

        var store = new MemoryCatalogStore();
        var options = Options.Create(new UpdateOptions
        {
            Enabled = true,
            StateRootPath = tempRoot,
            CatalogManifestUrl = catalogUrl,
            CatalogManifestPublicKeyPath = publicKeyPath,
            AllowUnsignedLocalFeeds = false,
        });

        var service = new HostedCatalogService(
            store,
            new MapHttpClientFactory(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [catalogUrl] = catalogJson,
            }),
            options,
            NullLogger<HostedCatalogService>.Instance);

        await service.RefreshSnapshotAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        HostedCatalogSnapshot snapshot = await service.GetSnapshotAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Contains("missing signature", snapshot.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(snapshot.Entries);
    }

    [Fact]
    public void HashVerifierRejectsMismatch()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
                ReleaseArtifactHashVerifier.Validate(
                    path,
                    expectedSha256: "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
                    expectedSize: 4,
                    requireHash: true,
                    artifactName: "pkg"));
            Assert.Contains("hash mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ApplyPreparedReleaseRejectsMissingSignatureWhenFailClosedAsync()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"kautuka-trust-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        string publicKeyPath = Path.Combine(tempRoot, "pub.pem");

        using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await File.WriteAllTextAsync(publicKeyPath, ecdsa.ExportSubjectPublicKeyInfoPem(), TestContext.Current.CancellationToken).ConfigureAwait(true);

        const string manifestUrl = "http://127.0.0.1:20311/manifest";
        const string artifactUrl = "http://127.0.0.1:20311/kautuka-platform-linux-x64.tar.gz";
        const string manifestJson = """
            {
              "schemaVersion": 2,
              "releaseId": "0.1.0-trust-nosig",
              "version": "0.1.0-trust-nosig",
              "channel": "preview",
              "artifacts": [
                {
                  "name": "kautuka-platform-linux-x64.tar.gz",
                  "platform": "linux-x64",
                  "url": "http://127.0.0.1:20311/kautuka-platform-linux-x64.tar.gz",
                  "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "size": 4
                }
              ]
            }
            """;

        UpdateCoordinator coordinator = CreateCoordinator(
            tempRoot,
            publicKeyPath,
            new UpdateReleaseReference(
                ReleaseId: "0.1.0-trust-nosig",
                Version: "0.1.0-trust-nosig",
                Channel: "preview",
                ReleaseFingerprint: "fp",
                PlatformArtifactUrl: artifactUrl,
                PlatformReleaseAssetUrl: manifestUrl,
                PlatformReleaseSignatureAssetUrl: null,
                SourceUrl: manifestUrl,
                PublishedAtUtc: null,
                Tag: "v0.1.0-trust-nosig"),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [manifestUrl] = manifestJson,
                [artifactUrl] = "dead",
            });

        UpdateStatusSnapshot status = await coordinator.ApplyPreparedReleaseAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.NotEqual(UpdateWorkflowState.Applying, status.State);
        Assert.Contains("signature URL was missing", status.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplyPreparedReleaseRejectsMissingSha256WhenFailClosedAsync()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"kautuka-trust-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        string publicKeyPath = Path.Combine(tempRoot, "pub.pem");

        using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] manifestBytes = Encoding.UTF8.GetBytes("""
            {
              "schemaVersion": 2,
              "releaseId": "0.1.0-trust-nohash",
              "releaseTag": "v0.1.0-trust-nohash",
              "version": "0.1.0-trust-nohash",
              "channel": "preview",
              "signature": { "algorithm": "ecdsa-p256-sha256", "path": "kautuka-platform-release.sig", "url": "http://127.0.0.1:20312/kautuka-platform-release.sig" },
              "artifacts": [
                {
                  "name": "kautuka-platform-linux-x64.tar.gz",
                  "platform": "linux-x64",
                  "url": "http://127.0.0.1:20312/kautuka-platform-linux-x64.tar.gz",
                  "sha256": "",
                  "size": 4
                }
              ]
            }
            """);
        string signature = Convert.ToBase64String(ecdsa.SignData(manifestBytes, HashAlgorithmName.SHA256));
        await File.WriteAllTextAsync(publicKeyPath, ecdsa.ExportSubjectPublicKeyInfoPem(), TestContext.Current.CancellationToken).ConfigureAwait(true);

        const string manifestUrl = "http://127.0.0.1:20312/manifest";
        const string signatureUrl = "http://127.0.0.1:20312/kautuka-platform-release.sig";
        const string artifactUrl = "http://127.0.0.1:20312/kautuka-platform-linux-x64.tar.gz";

        UpdateCoordinator coordinator = CreateCoordinator(
            tempRoot,
            publicKeyPath,
            new UpdateReleaseReference(
                ReleaseId: "0.1.0-trust-nohash",
                Version: "0.1.0-trust-nohash",
                Channel: "preview",
                ReleaseFingerprint: "fp",
                PlatformArtifactUrl: artifactUrl,
                PlatformReleaseAssetUrl: manifestUrl,
                PlatformReleaseSignatureAssetUrl: signatureUrl,
                SourceUrl: manifestUrl,
                PublishedAtUtc: null,
                Tag: "v0.1.0-trust-nohash"),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [manifestUrl] = Encoding.UTF8.GetString(manifestBytes),
                [signatureUrl] = signature + "\n",
                [artifactUrl] = "dead",
            });

        UpdateStatusSnapshot status = await coordinator.ApplyPreparedReleaseAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.NotEqual(UpdateWorkflowState.Applying, status.State);
        Assert.Contains("missing SHA-256", status.LastError, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("channel")]
    [InlineData("tag")]
    [InlineData("artifact-url")]
    [InlineData("signature-url")]
    public async Task ApplyPreparedReleaseRejectsSignedManifestThatDoesNotMatchDiscoveredCandidateAsync(string mismatch)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"kautuka-trust-candidate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        string publicKeyPath = Path.Combine(tempRoot, "pub.pem");
        using ECDsa ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        byte[] manifestBytes = Encoding.UTF8.GetBytes("""
            {
              "schemaVersion": 2,
              "releaseId": "1.0.1",
              "releaseTag": "v1.0.1",
              "version": "1.0.1",
              "channel": "preview",
              "signature": { "algorithm": "ecdsa-p256-sha256", "format": "base64", "path": "kautuka-platform-release.sig", "url": "http://127.0.0.1:20314/kautuka-platform-release.sig" },
              "artifacts": [
                {
                  "name": "kautuka-platform-linux-x64.tar.gz",
                  "platform": "linux-x64",
                  "url": "http://127.0.0.1:20314/kautuka-platform-linux-x64.tar.gz",
                  "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "size": 4
                }
              ]
            }
            """);
        string manifestUrl = "http://127.0.0.1:20314/kautuka-platform-release.json";
        string signedSignatureUrl = "http://127.0.0.1:20314/kautuka-platform-release.sig";
        string signedArtifactUrl = "http://127.0.0.1:20314/kautuka-platform-linux-x64.tar.gz";
        string candidateVersion = mismatch == "version" ? "1.0.2" : "1.0.1";
        string candidateChannel = mismatch == "channel" ? "stable" : "preview";
        string candidateTag = mismatch == "tag" ? "v1.0.2" : "v1.0.1";
        string candidateArtifactUrl = mismatch == "artifact-url" ? "http://127.0.0.1:20314/substituted.tar.gz" : signedArtifactUrl;
        string candidateSignatureUrl = mismatch == "signature-url" ? "http://127.0.0.1:20314/substituted.sig" : signedSignatureUrl;
        string signature = Convert.ToBase64String(ecdsa.SignData(manifestBytes, HashAlgorithmName.SHA256));
        await File.WriteAllTextAsync(publicKeyPath, ecdsa.ExportSubjectPublicKeyInfoPem(), TestContext.Current.CancellationToken).ConfigureAwait(true);

        UpdateCoordinator coordinator = CreateCoordinator(
            tempRoot,
            publicKeyPath,
            new UpdateReleaseReference(
                ReleaseId: candidateVersion,
                Version: candidateVersion,
                Channel: candidateChannel,
                ReleaseFingerprint: "candidate-fingerprint",
                PlatformArtifactUrl: candidateArtifactUrl,
                PlatformReleaseAssetUrl: manifestUrl,
                PlatformReleaseSignatureAssetUrl: candidateSignatureUrl,
                SourceUrl: manifestUrl,
                PublishedAtUtc: null,
                Tag: candidateTag),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [manifestUrl] = Encoding.UTF8.GetString(manifestBytes),
                [candidateSignatureUrl] = signature + "\n",
            });

        UpdateStatusSnapshot status = await coordinator.ApplyPreparedReleaseAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.NotEqual(UpdateWorkflowState.Applying, status.State);
        Assert.Contains("does not match", status.LastError, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(tempRoot, "updates"), "*.tar.gz", SearchOption.AllDirectories));
    }

    private static UpdateCoordinator CreateCoordinator(
        string tempRoot,
        string publicKeyPath,
        UpdateReleaseReference available,
        IReadOnlyDictionary<string, string> httpResponses)
    {
        var stateStore = new MemoryStateStore(
            UpdateStateRecord.CreateDefault() with
            {
                State = UpdateWorkflowState.Available,
                CurrentRelease = new UpdateReleaseReference(
                    ReleaseId: "0.0.0-test",
                    Version: "0.0.0-test",
                    Channel: "preview",
                    ReleaseFingerprint: "current",
                    PlatformArtifactUrl: null,
                    PlatformReleaseAssetUrl: null,
                    PlatformReleaseSignatureAssetUrl: null,
                    SourceUrl: null,
                    PublishedAtUtc: null,
                    Tag: "v0.0.0-test"),
                AvailableRelease = available,
            });

        var options = Options.Create(new UpdateOptions
        {
            Enabled = true,
            StateRootPath = tempRoot,
            ManifestPublicKeyPath = publicKeyPath,
            AllowUnsignedLocalFeeds = false,
            PlatformAssetName = "kautuka-platform-linux-x64.tar.gz",
        });

        return new UpdateCoordinator(
            stateStore,
            new MemoryPolicyStore("preview"),
            new EmptyDiscoveryClient(),
            new MapHttpClientFactory(httpResponses),
            new IdleHostIntegration(tempRoot),
            new UpdateOperationHistoryStore(options),
            options,
            NullLogger<UpdateCoordinator>.Instance);
    }

    private sealed class MemoryCatalogStore : IHostedCatalogStateStore
    {
        private HostedCatalogSnapshot current = HostedCatalogSnapshot.CreateDefault();

        public ValueTask<HostedCatalogSnapshot> GetAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(current);

        public ValueTask SaveAsync(HostedCatalogSnapshot snapshot, CancellationToken cancellationToken = default)
        {
            current = snapshot;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MemoryStateStore(UpdateStateRecord state) : IUpdateStateStore
    {
        private UpdateStateRecord current = state;

        public ValueTask<UpdateStateRecord> GetAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(current);

        public ValueTask SaveAsync(UpdateStateRecord next, CancellationToken cancellationToken = default)
        {
            current = next;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MemoryPolicyStore(string channel) : IUpdateRuntimePolicyStore
    {
        private UpdateRuntimePolicy current = UpdateRuntimePolicy.CreateDefault(channel);

        public ValueTask<UpdateRuntimePolicy> GetAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(current);

        public ValueTask<UpdateRuntimePolicy> SetReleaseChannelAsync(string releaseChannel, CancellationToken cancellationToken = default)
        {
            current = new UpdateRuntimePolicy(current.SchemaVersion, UpdateRuntimePolicy.NormalizeReleaseChannel(releaseChannel));
            return ValueTask.FromResult(current);
        }
    }

    private sealed class EmptyDiscoveryClient : IReleaseDiscoveryClient
    {
        public Task<ReleaseDiscoveryResult> DiscoverAsync(ReleaseDiscoveryRequest request, CancellationToken cancellationToken)
            => Task.FromResult(ReleaseDiscoveryResult.NoRelease());
    }

    private sealed class IdleHostIntegration(string runtimeRoot) : IUpdateServiceIntegration
    {
        public ValueTask<UpdateServiceContext> GetHostContextAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(new UpdateServiceContext(runtimeRoot, runtimeRoot, "0.0.0-test", "0.0.0-test", true, 0, true));

        public ValueTask ActivatePreparedUpdateAsync(UpdatePreparedArtifacts preparedArtifacts, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask ActivateRollbackAsync(UpdateReleaseReference release, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;

        public ValueTask<bool> TryContinuePendingActivationAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(false);

        public ValueTask RequestSupervisorReconcileAsync(CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    private sealed class MapHttpClientFactory(IReadOnlyDictionary<string, string> responses) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new MapHttpMessageHandler(responses), disposeHandler: true);
    }

    private sealed class MapHttpMessageHandler(IReadOnlyDictionary<string, string> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri?.ToString() ?? string.Empty;
            if (!responses.TryGetValue(url, out string? body))
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/octet-stream")
            });
        }
    }
}
