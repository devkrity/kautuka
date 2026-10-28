using System.Text.Json;

namespace Kautuka.Gateway.Domain.Providers;

internal interface IProviderValidationService
{
    Task<GatewayProviderTestResult> TestProviderAsync(JsonElement body, CancellationToken cancellationToken = default);
}
