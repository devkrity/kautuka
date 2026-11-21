using System.Security.Cryptography.X509Certificates;

using Kautuka.NetworkAccess.Client;

namespace Kautuka.Gateway.Infrastructure.Egress;

internal sealed class GatewayModelHttpTransport : IDisposable
{
    private const string certificateDirectory = "/run/kautuka-gateway-connect";
    private readonly GatewayConnectTransport? connect;
    private readonly X509Certificate2? trustedRoot;
    private readonly X509Certificate2? clientCertificate;

    internal GatewayModelHttpTransport(GatewayConnectTransport? connect = null)
    {
        this.connect = connect;
    }

    private GatewayModelHttpTransport(GatewayConnectTransport connect, X509Certificate2 trustedRoot, X509Certificate2 clientCertificate)
        : this(connect)
    {
        this.trustedRoot = trustedRoot;
        this.clientCertificate = clientCertificate;
    }

    internal static GatewayModelHttpTransport FromConfiguration(IConfiguration configuration)
    {
        if (configuration["Kautuka:Appliance:Enabled"] is not { } value || !bool.Parse(value))
        {
            return new GatewayModelHttpTransport();
        }

        X509Certificate2 trustedRoot = X509CertificateLoader.LoadCertificateFromFile($"{certificateDirectory}/ca.crt");
        X509Certificate2? clientCertificate = null;
        try
        {
            clientCertificate = X509CertificateLoader.LoadPkcs12FromFile($"{certificateDirectory}/client.pfx", null, X509KeyStorageFlags.EphemeralKeySet);
            var connect = new GatewayConnectTransport("10.247.0.2", 15004, "kautuka-connect.internal", trustedRoot, clientCertificate);
            return new GatewayModelHttpTransport(connect, trustedRoot, clientCertificate);
        }
        catch
        {
            clientCertificate?.Dispose();
            trustedRoot.Dispose();
            throw;
        }
    }

    internal HttpMessageHandler CreateHandler(INetworkAccessChecker? checker)
    {
#pragma warning disable CA2000 // The returned delegating handler owns the selected transport.
        HttpMessageHandler transport = connect is null
            ? new SocketsHttpHandler { AllowAutoRedirect = false }
            : new ScopedConnectHandler(connect);
        return new NetworkAccessCheckHandler(checker ?? FailClosedNetworkAccessChecker.Instance, transport);
#pragma warning restore CA2000
    }

    public void Dispose()
    {
        connect?.Dispose();
        clientCertificate?.Dispose();
        trustedRoot?.Dispose();
    }

    private sealed class ScopedConnectHandler(GatewayConnectTransport connect) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            NetworkAccessScope? caller = NetworkAccessContext.Current;
            if (caller?.UserId is not { Length: > 0 } user)
            {
                throw new HttpRequestException("Gateway model egress requires a caller identity.");
            }

            var scope = new GatewayEgressScope(user, caller.AgentId, caller.ConnectorId, caller.Purpose ?? "model", caller.CorrelationId);
            return connect.SendAsync(request, scope, cancellationToken);
        }
    }
}
