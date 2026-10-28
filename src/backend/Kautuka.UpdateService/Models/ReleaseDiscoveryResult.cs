namespace Kautuka.UpdateService.Models;

public sealed record ReleaseDiscoveryResult(
    bool NotModified,
    UpdateReleaseReference? AvailableRelease,
    string? ETag,
    string? FailureReason,
    bool IsUnsupported)
{
    public static ReleaseDiscoveryResult NoChange(string? eTag = null)
        => new(true, null, eTag, null, false);

    public static ReleaseDiscoveryResult NoRelease(string? eTag = null)
        => new(false, null, eTag, null, false);

    public static ReleaseDiscoveryResult Failed(string reason)
        => new(false, null, null, reason, false);

    public static ReleaseDiscoveryResult Unsupported(string reason, string? eTag = null)
        => new(false, null, eTag, reason, true);

    public static ReleaseDiscoveryResult Available(UpdateReleaseReference release, string? eTag = null)
        => new(false, release, eTag, null, false);
}
