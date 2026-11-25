using System.Text.Json.Nodes;

namespace Kautuka.Gateway.Domain.Providers;

internal sealed record ProviderTestDefinition(string Id, string Type, JsonObject Settings, bool UseStoredCredential);

internal interface IProviderValidationService
{
    Task<GatewayProviderTestResult> TestProviderAsync(ProviderTestDefinition definition, CancellationToken cancellationToken = default);
}
