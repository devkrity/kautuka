namespace Kautuka.Gateway.Infrastructure.ModelCapability;

/// <summary>
/// Capabilities declared by the provider via metadata/introspection API.
/// These are "best effort" — provider may not expose all capabilities.
/// </summary>
internal sealed class DeclaredCapabilities
{
    public bool? SupportsStreaming { get; init; }
    public bool? SupportsFunctionCalling { get; init; }
    public bool? SupportsStructuredOutput { get; init; }
    public bool? IsThinkingModel { get; init; }
    public bool? SupportsReasoningChannel { get; init; }
    public string? ModelFamily { get; init; }
    public string? ModelDigest { get; init; }
}
