namespace Kautuka.Gateway.Infrastructure.Configuration;

internal sealed class ModelDefinition
{
    public string Id { get; init; } = string.Empty;

    public string ProviderId { get; init; } = string.Empty;

    public string ModelName { get; init; } = string.Empty;

    public Dictionary<string, object?> Settings { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public GatewayModelCapabilities? Capabilities { get; init; }
}
