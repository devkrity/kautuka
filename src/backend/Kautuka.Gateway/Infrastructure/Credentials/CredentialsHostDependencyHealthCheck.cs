using Grpc.Core;

using Kautuka.Protos;

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Kautuka.Gateway.Infrastructure.Credentials;

internal sealed class CredentialsHostDependencyHealthCheck(CredentialResolverService resolver) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            CredentialHealthResponse response = await resolver.GetHealthAsync(cancellationToken).ConfigureAwait(false);
            DateTimeOffset certificateExpiry = DateTimeOffset.FromUnixTimeSeconds(response.ActiveClientCertificateNotAfterUnixSeconds);
            if (certificateExpiry <= DateTimeOffset.UtcNow)
                return HealthCheckResult.Unhealthy("CredentialsHost client identity is expired.");
            if (certificateExpiry <= DateTimeOffset.UtcNow.AddDays(3))
                return HealthCheckResult.Degraded("CredentialsHost client identity requires renewal.");
            return response.State switch
            {
                CredentialHealthState.Ready => HealthCheckResult.Healthy(),
                CredentialHealthState.IntegrityFailed => HealthCheckResult.Unhealthy("CredentialsHost reported an integrity failure."),
                _ => HealthCheckResult.Degraded("CredentialsHost reported degraded readiness.")
            };
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.DataLoss)
        {
            return HealthCheckResult.Unhealthy("CredentialsHost reported an integrity failure.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("CredentialsHost is unavailable.", exception);
        }
    }
}
