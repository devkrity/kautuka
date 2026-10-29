using Kautuka.UpdateService.Hosting;
using Kautuka.UpdateService.Models;

namespace Kautuka.IntegrationTests;

public sealed class GitHubReleaseManifestBindingTests
{
    private const string tag = "v2.4.0-preview.3";
    private const string sourceSha = "0123456789abcdef0123456789abcdef01234567";
    private const string manifestUrl = "https://github.com/devkrity/kautuka/releases/download/v2.4.0-preview.3/kautuka-platform-release.json";
    private const string signatureUrl = "https://github.com/devkrity/kautuka/releases/download/v2.4.0-preview.3/kautuka-platform-release.sig";
    private const string artifactUrl = "https://github.com/devkrity/kautuka/releases/download/v2.4.0-preview.3/kautuka-linux-amd64.tar.gz";

    [Fact]
    public void SignedManifestIdentityAndExactAssetsMustMatchDiscoveredRelease()
    {
        UpdateReleaseReference candidate = Candidate();
        PlatformReleaseDescriptor descriptor = Descriptor();
        PlatformReleaseArtifactDescriptor artifact = Artifact(artifactUrl);

        UpdateCoordinator.ValidateDescriptorMatchesCandidate(candidate, descriptor, artifact, requireVerification: true);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("channel")]
    [InlineData("tag")]
    [InlineData("source")]
    [InlineData("artifact-url")]
    [InlineData("signature-url")]
    public void SignedManifestMismatchIsRejectedBeforeArtifactDownload(string mismatch)
    {
        UpdateReleaseReference candidate = Candidate();
        PlatformReleaseDescriptor descriptor = Descriptor(
            version: mismatch == "version" ? "2.4.0-preview.2" : null,
            channel: mismatch == "channel" ? "stable" : null,
            releaseTag: mismatch == "tag" ? "v2.4.0-preview.2" : null,
            sourceSha: mismatch == "source" ? "fedcba9876543210fedcba9876543210fedcba98" : null,
            signatureUrl: mismatch == "signature-url"
                ? "https://github.com/devkrity/kautuka/releases/download/v2.4.0-preview.2/kautuka-platform-release.sig"
                : null);
        PlatformReleaseArtifactDescriptor artifact = mismatch == "artifact-url"
            ? Artifact("https://github.com/devkrity/kautuka/releases/download/v2.4.0-preview.2/kautuka-linux-amd64.tar.gz")
            : Artifact(artifactUrl);

        Assert.Throws<InvalidOperationException>(() =>
            UpdateCoordinator.ValidateDescriptorMatchesCandidate(candidate, descriptor, artifact, requireVerification: true));
    }

    private static UpdateReleaseReference Candidate()
        => new(
            ReleaseId: "42",
            Version: "2.4.0-preview.3",
            Channel: "preview",
            ReleaseFingerprint: "fingerprint",
            PlatformArtifactUrl: artifactUrl,
            PlatformReleaseAssetUrl: manifestUrl,
            PlatformReleaseSignatureAssetUrl: signatureUrl,
            SourceUrl: "https://github.com/devkrity/kautuka/releases/tag/v2.4.0-preview.3",
            PublishedAtUtc: DateTimeOffset.UtcNow,
            Tag: tag)
        {
            SourceSha = sourceSha,
        };

    private static PlatformReleaseDescriptor Descriptor(
        string? version = null,
        string? channel = null,
        string? releaseTag = null,
        string? sourceSha = null,
        string? signatureUrl = null)
        => new()
        {
            SchemaVersion = 2,
            ReleaseId = "42",
            ReleaseTag = releaseTag ?? tag,
            Channel = channel ?? "preview",
            Version = version ?? "2.4.0-preview.3",
            SourceSha = sourceSha ?? GitHubReleaseManifestBindingTests.sourceSha,
            Signature = new PlatformReleaseSignatureDescriptor(
                "ecdsa-p256-sha256",
                "kautuka-platform-release.sig",
                signatureUrl ?? GitHubReleaseManifestBindingTests.signatureUrl,
                "base64"),
            Artifacts = [Artifact(artifactUrl)],
        };

    private static PlatformReleaseArtifactDescriptor Artifact(string url)
        => new("kautuka-linux-amd64.tar.gz", "linux-x64", null, url, new string('a', 64), 123);
}
