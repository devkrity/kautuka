namespace Kautuka.Gateway.RuntimePlane.Configuration;

internal sealed record ConfigSnapshot(
    long Revision,
    string ContentHash,
    string PayloadJson,
    DateTimeOffset CapturedAt);

internal interface IConfigSnapshotStore
{
    Task<ConfigSnapshot> EnsurePersistedAsync(ConfigSnapshot snapshot, CancellationToken ct);

    Task<ConfigSnapshot?> GetAsync(long revision, CancellationToken ct);

    Task<bool> ExistsAsync(long revision, string contentHash, CancellationToken ct);
}

internal interface IConfigSnapshotCapture
{
    Task<ConfigSnapshot> CaptureCurrentAsync(CancellationToken ct);
}
