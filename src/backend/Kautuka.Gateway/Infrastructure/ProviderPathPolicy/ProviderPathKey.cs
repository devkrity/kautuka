namespace Kautuka.Gateway.Infrastructure.ProviderPathPolicy;

/// <summary>
/// Identifies a specific provider/endpoint/client/model path through the AI pipeline.
/// Finer-grained than ModelCapabilityKey — captures the actual runtime client type.
/// </summary>
internal sealed record ProviderPathKey(
    string ProviderType,
    string EndpointKey,
    string ClientType,
    string ModelKey)
{
    public string ToStoreKey() =>
        $"{ProviderType}::{EndpointKey}::{ClientType}::{ModelKey}";
}
