namespace Kautuka.UpdateService.Models;

public sealed record HostedCatalogEntry(
    string Id,
    string Kind,
    string Title,
    string Summary,
    string Publisher,
    string Version,
    string? ArtifactUrl,
    string? ArtifactSha256,
    string TrustKind,
    bool Recommended);

public sealed record HostedCatalogSnapshot(
    int SchemaVersion,
    DateTimeOffset? RefreshedAtUtc,
    DateTimeOffset? LastRefreshAttemptUtc,
    DateTimeOffset? LastRefreshSuccessUtc,
    string? SourceManifestUrl,
    string? SourceEtag,
    string? LastError,
    IReadOnlyList<HostedCatalogEntry> Entries)
{
    public static HostedCatalogSnapshot CreateDefault()
        => new(
            SchemaVersion: 1,
            RefreshedAtUtc: null,
            LastRefreshAttemptUtc: null,
            LastRefreshSuccessUtc: null,
            SourceManifestUrl: null,
            SourceEtag: null,
            LastError: null,
            Entries: []);
}
