using System.Security.Cryptography.X509Certificates;

using Kautuka.NetworkAccess.Client;

namespace Kautuka.Gateway.Infrastructure.Egress;

internal sealed class GatewayOutboundHttpTransport : IDisposable
{
    private const string certificateDirectory = "/run/kautuka-gateway-connect";
    private readonly GatewayConnectTransport? connect;
    private readonly X509Certificate2? trustedRoot;
    private readonly X509Certificate2? clientCertificate;

    internal GatewayOutboundHttpTransport(GatewayConnectTransport? connect = null)
    {
        this.connect = connect;
    }

    private GatewayOutboundHttpTransport(GatewayConnectTransport connect, X509Certificate2 trustedRoot, X509Certificate2 clientCertificate)
        : this(connect)
    {
        this.trustedRoot = trustedRoot;
        this.clientCertificate = clientCertificate;
    }

    internal static GatewayOutboundHttpTransport FromConfiguration(IConfiguration configuration)
    {
        if (configuration["Kautuka:Appliance:Enabled"] is not { } value || !bool.Parse(value))
        {
            return new GatewayOutboundHttpTransport();
        }

        X509Certificate2 trustedRoot = X509CertificateLoader.LoadCertificateFromFile($"{certificateDirectory}/ca.crt");
        X509Certificate2? clientCertificate = null;
        try
        {
            clientCertificate = X509CertificateLoader.LoadPkcs12FromFile($"{certificateDirectory}/client.pfx", null, X509KeyStorageFlags.EphemeralKeySet);
            var connect = new GatewayConnectTransport("10.247.0.2", 15004, "kautuka-connect.internal", trustedRoot, clientCertificate,
                connectTimeout: TimeSpan.FromSeconds(5));
            return new GatewayOutboundHttpTransport(connect, trustedRoot, clientCertificate);
        }
        catch
        {
            clientCertificate?.Dispose();
            trustedRoot.Dispose();
            throw;
        }
    }

    internal HttpMessageHandler? CreateScopedHandler(Func<GatewayEgressScope> resolveScope)
        => connect is null ? null : new ScopedConnectHandler(connect, resolveScope);

    internal HttpMessageHandler? CreateApplicationTransport()
        => CreateScopedHandler(static () => ResolveScope("http-request"));

    internal HttpMessageHandler CreateModelHandler(INetworkAccessChecker? checker)
    {
#pragma warning disable CA2000 // The returned delegating handler owns the selected transport.
        HttpMessageHandler transport = CreateScopedHandler(static () => ResolveScope("model"))
            ?? new SocketsHttpHandler { AllowAutoRedirect = false };
        return new NetworkAccessCheckHandler(checker ?? FailClosedNetworkAccessChecker.Instance, transport);
#pragma warning restore CA2000
    }

    private static GatewayEgressScope ResolveScope(string defaultPurpose)
    {
        NetworkAccessScope? caller = NetworkAccessContext.Current;
        if (caller?.UserId is not { Length: > 0 } user)
        {
            throw new HttpRequestException("Gateway egress requires a caller identity.");
        }

        return new GatewayEgressScope(user, caller.AgentId, caller.ConnectorId, caller.Purpose ?? defaultPurpose, caller.CorrelationId);
    }

    public void Dispose()
    {
        connect?.Dispose();
        clientCertificate?.Dispose();
        trustedRoot?.Dispose();
    }

    private sealed class ScopedConnectHandler(GatewayConnectTransport connect, Func<GatewayEgressScope> resolveScope) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => connect.SendAsync(request, resolveScope(), cancellationToken);
    }
}
