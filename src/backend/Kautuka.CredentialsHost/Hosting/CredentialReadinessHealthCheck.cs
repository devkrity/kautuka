using Kautuka.CredentialsHost.Database;
using Kautuka.CredentialsHost.Security;

using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Kautuka.CredentialsHost.Hosting;

internal sealed class CredentialReadinessHealthCheck(CredentialsDatabase database, CredentialKeyRing keyRing) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            string? result = (string?)await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(result, "ok", StringComparison.Ordinal)) return HealthCheckResult.Unhealthy("Credential database integrity check failed.");
            if (string.IsNullOrEmpty(keyRing.ActiveKeyId)) return HealthCheckResult.Unhealthy("Credential encryption key is unavailable.");
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Credential dependency check failed.", exception);
        }
    }
}
