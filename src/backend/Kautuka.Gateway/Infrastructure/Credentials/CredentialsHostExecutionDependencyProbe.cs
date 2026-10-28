using Grpc.Core;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Protos;

namespace Kautuka.Gateway.Infrastructure.Credentials;

internal sealed class CredentialsHostExecutionDependencyProbe : IAgentExecutionDependencyProbe
{
    private static readonly TimeSpan defaultProbeTimeout = TimeSpan.FromSeconds(3);
    private readonly CredentialResolverService credentialResolver;
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan probeTimeout;
    internal const string DependencyName = "credentials_host";
    internal const string ReadyCode = "CREDENTIALS_HOST_READY";
    internal const string UnavailableCode = "CREDENTIALS_HOST_UNAVAILABLE";
    internal const string IntegrityFailedCode = "CREDENTIALS_HOST_INTEGRITY_FAILED";
    internal const string DegradedCode = "CREDENTIALS_HOST_DEGRADED";

    private const string unavailableMessage =
        "Secure credential storage is unavailable, so agent execution cannot start. Check that CredentialsHost is running, then retry.";

    private const string integrityFailedMessage =
        "Secure credential storage failed its integrity check, so agent execution cannot start. Restore CredentialsHost from backup if needed, then retry.";

    private const string degradedMessage =
        "Secure credential storage is not ready, so agent execution cannot start. Check CredentialsHost health, then retry.";

    public CredentialsHostExecutionDependencyProbe(
        CredentialResolverService credentialResolver,
        TimeProvider timeProvider)
        : this(credentialResolver, timeProvider, defaultProbeTimeout)
    {
    }

    internal CredentialsHostExecutionDependencyProbe(
        CredentialResolverService credentialResolver,
        TimeProvider timeProvider,
        TimeSpan probeTimeout)
    {
        ArgumentNullException.ThrowIfNull(credentialResolver);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(probeTimeout, TimeSpan.Zero);

        this.credentialResolver = credentialResolver;
        this.timeProvider = timeProvider;
        this.probeTimeout = probeTimeout;
    }

    public string Dependency => DependencyName;

    public async Task<AgentExecutionDependencyStatus> CheckAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(probeTimeout);

        try
        {
            CredentialHealthResponse health = await credentialResolver.GetHealthAsync(timeout.Token).ConfigureAwait(false);
            DateTimeOffset now = timeProvider.GetUtcNow();
            DateTimeOffset certificateExpiry = DateTimeOffset.FromUnixTimeSeconds(
                health.ActiveClientCertificateNotAfterUnixSeconds);

            if (certificateExpiry <= now)
            {
                return Degraded("The Gateway client identity for CredentialsHost has expired.");
            }

            if (certificateExpiry <= now.AddDays(3))
            {
                return Degraded("The Gateway client identity for CredentialsHost requires renewal.");
            }

            return health.State switch
            {
                CredentialHealthState.Ready => new AgentExecutionDependencyStatus(
                    DependencyName,
                    Ready: true,
                    ReadyCode,
                    "Secure credential storage is ready."),
                CredentialHealthState.IntegrityFailed => IntegrityFailed(),
                _ => Degraded("CredentialsHost reported degraded readiness.")
            };
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.DataLoss)
        {
            return IntegrityFailed();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Unavailable("The CredentialsHost readiness check timed out.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Unavailable(ResolveUnavailableDetail(exception));
        }
    }

    private static AgentExecutionDependencyStatus Unavailable(string detail)
        => new(DependencyName, Ready: false, UnavailableCode, unavailableMessage, detail);

    private static AgentExecutionDependencyStatus IntegrityFailed()
        => new(
            DependencyName,
            Ready: false,
            IntegrityFailedCode,
            integrityFailedMessage,
            "CredentialsHost reported a credential-store integrity failure.");

    private static AgentExecutionDependencyStatus Degraded(string detail)
        => new(DependencyName, Ready: false, DegradedCode, degradedMessage, detail);

    private static string ResolveUnavailableDetail(Exception exception)
        => exception is RpcException rpcException
            ? rpcException.StatusCode switch
            {
                StatusCode.DeadlineExceeded => "The CredentialsHost readiness check timed out.",
                StatusCode.PermissionDenied or StatusCode.Unauthenticated =>
                    "Gateway could not authenticate to CredentialsHost.",
                _ => "Gateway could not reach CredentialsHost."
            }
            : "Gateway could not initialize or reach CredentialsHost.";
}
