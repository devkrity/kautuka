using System.Security.Cryptography.X509Certificates;

using Kautuka.CredentialsHost.Security;
using Kautuka.Telemetry.Security;

namespace Kautuka.CredentialsHost.Hosting;

internal sealed class CredentialClientCertificateRenewalService(
    CredentialStorageLayout layout,
    CredentialClientIdentityState identityState,
    TimeProvider timeProvider,
    ILogger<CredentialClientCertificateRenewalService> logger) : BackgroundService
{
    private static readonly TimeSpan checkInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan renewalWindow = TimeSpan.FromDays(7);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(checkInterval, timeProvider);
        do
        {
            (_, DateTimeOffset notAfter) = identityState.Current;
            if (notAfter <= timeProvider.GetUtcNow().Add(renewalWindow))
            {
                try
                {
                    using X509Certificate2 certificate = LocalCaMtlsBootstrap.RenewGatewayClientCertificate(
                        layout.TransportRoot,
                        "Credentials",
                        ".credentials-client.pfx");
                    CredentialStorageLayout.PublishGatewayReadableFile(
                        Path.Combine(layout.TransportRoot, ".credentials-client.pfx"));
                    identityState.Activate(certificate.Thumbprint, certificate.NotAfter);
                    CredentialClientCertificateRenewalLog.Renewed(logger, certificate.Thumbprint, certificate.NotAfter);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    CredentialClientCertificateRenewalLog.Failed(logger, exception, notAfter);
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}

internal static partial class CredentialClientCertificateRenewalLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "CredentialsHost Gateway client identity renewed: thumbprint={Thumbprint} notAfter={NotAfter}")]
    public static partial void Renewed(ILogger logger, string thumbprint, DateTime notAfter);

    [LoggerMessage(Level = LogLevel.Error, Message = "CredentialsHost Gateway client identity renewal failed; current identity expires at {NotAfter}")]
    public static partial void Failed(ILogger logger, Exception exception, DateTimeOffset notAfter);
}
