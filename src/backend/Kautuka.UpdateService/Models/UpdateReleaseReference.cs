namespace Kautuka.UpdateService.Models;

public sealed record UpdateReleaseReference(
    string ReleaseId,
    string Version,
    string Channel,
    string ReleaseFingerprint,
    string? PlatformArtifactUrl,
    string? PlatformReleaseAssetUrl,
    string? PlatformReleaseSignatureAssetUrl,
    string? SourceUrl,
    DateTimeOffset? PublishedAtUtc,
    string? Tag);
