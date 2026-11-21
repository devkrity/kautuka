using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Kautuka.Hosting.Hosting;

namespace Kautuka.Manager.Hosting;

/// <summary>Checks application service readiness over its authenticated local transport.</summary>
internal static class ApplianceServiceReadiness
{
    internal static async Task<bool> CheckWebHostAsync(CancellationToken cancellationToken, int port = KautukaHostNetworkPolicy.WebHostPort)
    {
        using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(3) };
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(3) };
        return await CheckHttpAsync(client, new Uri($"http://127.0.0.1:{port}/health", UriKind.Absolute), cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<bool> CheckGatewayAsync(string socketPath, CancellationToken cancellationToken)
    {
        var endpoint = new KautukaEndpoint
        {
            Name = "gateway",
            Transport = KautukaTransport.UnixSocket,
            Port = 0,
            SocketPath = socketPath,
            BaseUri = new Uri("http://localhost", UriKind.Absolute),
        };
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            ConnectCallback = KautukaHostNetworkPolicy.CreateConnectCallback(endpoint),
            ConnectTimeout = TimeSpan.FromSeconds(3),
        };
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(3) };
        return await CheckHttpAsync(client, new Uri(endpoint.BaseUri, "/health"), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> CheckHttpAsync(HttpClient client, Uri address, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await client.GetAsync(address,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch (HttpRequestException) { return false; }
    }

    internal static async Task<bool> CheckAsync(string socketPath, string caPath, string clientPfxPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux())
            return false;
        try
        {
            using X509Certificate2 ca = X509CertificateLoader.LoadCertificateFromFile(caPath);
            using X509Certificate2 clientCertificate = X509CertificateLoader.LoadPkcs12FromFile(clientPfxPath, password: null);
            if (!clientCertificate.HasPrivateKey) return false;
            var endpoint = new KautukaEndpoint
            {
                Name = "appliance-readiness",
                Transport = KautukaTransport.UnixSocket,
                Port = 0,
                SocketPath = socketPath,
                BaseUri = new Uri("https://localhost", UriKind.Absolute),
            };
            var policy = new X509ChainPolicy
            {
                TrustMode = X509ChainTrustMode.CustomRootTrust,
                RevocationMode = X509RevocationMode.NoCheck,
                DisableCertificateDownloads = true,
            };
            policy.CustomTrustStore.Add(ca);
            policy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
            using var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                ConnectCallback = KautukaHostNetworkPolicy.CreateConnectCallback(endpoint),
                ConnectTimeout = TimeSpan.FromSeconds(3),
                SslOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    CertificateChainPolicy = policy,
                    ClientCertificates = new X509CertificateCollection { clientCertificate },
                },
            };
            using var client = new HttpClient(handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(3) };
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("https://localhost/health", UriKind.Absolute))
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            return response.StatusCode == HttpStatusCode.OK && response.Version == HttpVersion.Version20;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or HttpRequestException or AuthenticationException or System.Net.Sockets.SocketException)
        {
            return false;
        }
    }
}
