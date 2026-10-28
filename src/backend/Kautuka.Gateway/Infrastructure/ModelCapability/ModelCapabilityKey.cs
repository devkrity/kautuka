namespace Kautuka.Gateway.Infrastructure.ModelCapability;

internal sealed record ModelCapabilityKey(
    string ProviderId,
    Uri BaseUrl,
    string ModelId,
    string? ModelDigest,
    int ProbeSchemaVersion)
{
    public string ToStoreKey() =>
        $"{ProviderId}::{BaseUrl.AbsoluteUri}::{ModelId}::{ProbeSchemaVersion}";
}
