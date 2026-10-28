namespace Kautuka.Gateway.Infrastructure.Configuration;

internal sealed class ProviderDefinition
{
    public string Id { get; init; } = string.Empty;

    public string Type { get; init; } = string.Empty;

    public Dictionary<string, object?> Settings { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
