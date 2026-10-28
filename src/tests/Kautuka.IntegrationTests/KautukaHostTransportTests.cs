using System.Security.Cryptography.X509Certificates;

using Kautuka.Hosting.Hosting;
using Kautuka.Telemetry.Security;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Kautuka.IntegrationTests;

/// <summary>
/// Covers the transport seam. Before this suite the choice of transport had no tests at
/// all, which is how Manager came to bind a wildcard in prod while its siblings did not.
/// </summary>
public sealed class KautukaHostTransportTests
{
    [Fact]
    public void DefaultTransportIsUnixSocketWhereThePlatformSupportsIt()
    {
        // Sockets are the default rather than an opt-in, so a host cannot fall back to a
        // shared-namespace TCP port by omission.
        IConfiguration configuration = CreateConfiguration();

        KautukaHostNetworkLayout layout = KautukaHostNetworkPolicy.Resolve(configuration);

        Assert.Equal(
            System.Net.Sockets.Socket.OSSupportsUnixDomainSockets
                ? KautukaTransport.UnixSocket
                : KautukaTransport.Tcp,
            layout.Manager.Transport);
    }

    [Fact]
    public void TcpOptInRestoresTheHistoricalLoopbackAddresses()
    {
        IConfiguration configuration = CreateConfiguration(transport: "tcp");

        KautukaHostNetworkLayout layout = KautukaHostNetworkPolicy.Resolve(configuration);

        Assert.Equal(new Uri("https://127.0.0.1:20001"), layout.ManagerGrpcBaseUri);
        Assert.Equal(new Uri("https://127.0.0.1:20002"), layout.CredentialsGrpcBaseUri);
        Assert.Equal(new Uri("https://127.0.0.1:20003"), layout.ChannelHostGrpcBaseUri);
        Assert.Equal(new Uri("https://127.0.0.1:20004"), layout.UpdateServiceGrpcBaseUri);
        Assert.Equal(new Uri("http://127.0.0.1:20000"), layout.GatewayPublicBaseUri);
        Assert.All(
            new[] { layout.Manager, layout.Credentials, layout.ChannelHost, layout.UpdateService, layout.Gateway },
            endpoint => Assert.Null(endpoint.SocketPath));
    }

    [Fact]
    public void EverySocketIsDistinctAndSitsUnderTheConfiguredRoot()
    {
        using var temp = new TempDirectory();
        IConfiguration configuration = CreateConfiguration(socketRoot: temp.Path);

        KautukaHostNetworkLayout layout = KautukaHostNetworkPolicy.Resolve(configuration);

        string[] paths =
        [
            layout.Manager.SocketPath!,
            layout.Credentials.SocketPath!,
            layout.ChannelHost.SocketPath!,
            layout.UpdateService.SocketPath!,
            layout.Gateway.SocketPath!,
        ];

        Assert.Equal(paths.Length, paths.Distinct(StringComparer.Ordinal).Count());
        Assert.All(paths, path => Assert.StartsWith(temp.Path, path, StringComparison.Ordinal));
    }

    [Fact]
    public void SocketBaseUriKeepsTheLocalhostHostSoTlsStillValidates()
    {
        using var temp = new TempDirectory();
        IConfiguration configuration = CreateConfiguration(socketRoot: temp.Path);

        KautukaHostNetworkLayout layout = KautukaHostNetworkPolicy.Resolve(configuration);

        // TLS validates the hostname even with no network in the path, and the generated
        // server certificates carry a "localhost" SAN. A different placeholder host here
        // would fail the handshake rather than anything obviously transport related.
        Assert.Equal("localhost", layout.Manager.BaseUri.Host);
        Assert.True(layout.Manager.BaseUri.IsDefaultPort);
    }

    [Fact]
    public void OverlongSocketRootFailsWithAnActionableMessage()
    {
        // sun_path truncates silently at the syscall boundary, so this has to be caught
        // while the path is still a string.
        string longRoot = Path.Combine(Path.GetTempPath(), new string('k', 120));
        IConfiguration configuration = CreateConfiguration(socketRoot: longRoot);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => KautukaHostNetworkPolicy.Resolve(configuration));

        Assert.Contains("Kautuka:SocketRoot", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ConnectCallbackIsSuppliedOnlyForSockets()
    {
        using var temp = new TempDirectory();

        KautukaHostNetworkLayout socketLayout = KautukaHostNetworkPolicy.Resolve(
            CreateConfiguration(socketRoot: temp.Path));
        KautukaHostNetworkLayout tcpLayout = KautukaHostNetworkPolicy.Resolve(
            CreateConfiguration(transport: "tcp"));

        Assert.NotNull(KautukaHostNetworkPolicy.CreateConnectCallback(socketLayout.Manager));
        Assert.Null(KautukaHostNetworkPolicy.CreateConnectCallback(tcpLayout.Manager));
    }

    [Fact]
    public async Task MutualTlsOverAUnixSocketAcceptsAPolicyBuiltClient()
    {
        if (!System.Net.Sockets.Socket.OSSupportsUnixDomainSockets)
            return;

        using var temp = new TempDirectory();
        IConfiguration configuration = CreateConfiguration(socketRoot: temp.Path);
        KautukaEndpoint endpoint = KautukaHostNetworkPolicy.Resolve(configuration).Manager;

        // Real bootstrap rather than throwaway certificates, so this also guards the
        // pairing between the placeholder host and the SANs the product actually issues.
        string certRoot = Path.Combine(temp.Path, "certs");
        (X509Certificate2 serverCert, X509Certificate2 caCert) =
            LocalCaMtlsBootstrap.EnsureAndLoad(certRoot, "Manager", "client.pfx");
        using X509Certificate2 clientCert = X509CertificateLoader.LoadPkcs12FromFile(
            Path.Combine(certRoot, "client.pfx"),
            null,
            X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(endpoint, listen =>
        {
            listen.Protocols = HttpProtocols.Http2;
            listen.UseHttps(serverCert, https =>
            {
                https.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
                https.ClientCertificateValidation = (cert, _, _) =>
                    LocalCaMtlsBootstrap.ValidateClientCert(cert, caCert);
            });
        }));

        await using WebApplication app = builder.Build();
        app.MapGet("/probe", () => "reached");
        await app.StartAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        try
        {
            Assert.True(File.Exists(endpoint.SocketPath!));

            using var handler = new SocketsHttpHandler
            {
                ConnectCallback = KautukaHostNetworkPolicy.CreateConnectCallback(endpoint),
                SslOptions = new System.Net.Security.SslClientAuthenticationOptions
                {
                    ClientCertificates = [clientCert],
                    TargetHost = "localhost",
                    RemoteCertificateValidationCallback = (_, cert, _, _) =>
                        cert is not null
                        && LocalCaMtlsBootstrap.ValidateClientCert(
                            X509CertificateLoader.LoadCertificate(cert.GetRawCertData()),
                            caCert),
                },
            };

            using var client = new HttpClient(handler)
            {
                BaseAddress = endpoint.BaseUri,
                DefaultRequestVersion = System.Net.HttpVersion.Version20,
                DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            };

            string body = await client
                .GetStringAsync(new Uri("/probe", UriKind.Relative), TestContext.Current.CancellationToken)
                .ConfigureAwait(true);

            Assert.Equal("reached", body);
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            serverCert.Dispose();
            caCert.Dispose();
        }
    }

    [Fact]
    public void StaleSocketFileIsRemovedSoTheNextBindDoesNotReportAnAddressConflict()
    {
        using var temp = new TempDirectory();
        KautukaEndpoint endpoint = KautukaHostNetworkPolicy.Resolve(
            CreateConfiguration(socketRoot: temp.Path)).Manager;

        KautukaHostNetworkPolicy.EnsureSocketDirectory(endpoint);
        File.WriteAllText(endpoint.SocketPath!, string.Empty);

        KautukaHostNetworkPolicy.ClearStaleSocket(endpoint);

        Assert.False(File.Exists(endpoint.SocketPath!));
    }

    /// <summary>
    /// Connecting to a Unix socket needs write permission on the socket file, and the units
    /// set a restrictive <c>UMask=</c> for state and log protection, so a bound socket comes
    /// out owner-only. Windows does not enforce those bits, so this regressed silently there
    /// while every cross-service hop in the appliance was refused.
    /// </summary>
    [Fact]
    public void BoundSocketIsGroupAccessibleSoAClientInTheGroupCanConnect()
    {
        // The early return is what CA1416 recognises as a platform guard; SkipWhen alone is
        // not, and reporting a skip keeps this from looking like a pass on Windows.
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix file modes are not enforced on Windows.");
            return;
        }

        using var temp = new TempDirectory();
        KautukaEndpoint endpoint = KautukaHostNetworkPolicy.Resolve(
            CreateConfiguration(socketRoot: temp.Path)).Manager;

        KautukaHostNetworkPolicy.EnsureSocketDirectory(endpoint);
        File.WriteAllText(endpoint.SocketPath!, string.Empty);
        File.SetUnixFileMode(endpoint.SocketPath!, UnixFileMode.UserRead | UnixFileMode.UserWrite);

        KautukaHostNetworkPolicy.GrantSocketGroupAccess(endpoint);

        UnixFileMode mode = File.GetUnixFileMode(endpoint.SocketPath!);

        Assert.True(mode.HasFlag(UnixFileMode.GroupWrite), "a peer in the client group must be able to connect");
        Assert.True(mode.HasFlag(UnixFileMode.GroupRead));

        // The directory is the access gate, so the socket must not be reachable by everyone.
        Assert.False(mode.HasFlag(UnixFileMode.OtherWrite));
        Assert.False(mode.HasFlag(UnixFileMode.OtherRead));
    }

    private static IConfiguration CreateConfiguration(string? transport = null, string? socketRoot = null)
    {
        var values = new Dictionary<string, string?> { ["Kautuka:Mode"] = "dev" };

        if (transport is not null)
            values[KautukaHostNetworkPolicy.TransportConfigurationKey] = transport;

        if (socketRoot is not null)
            values[KautukaHostNetworkPolicy.SocketRootConfigurationKey] = socketRoot;

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            // Short prefix on purpose: the socket path budget is 107 bytes.
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"kt{Guid.NewGuid():N}"[..24]);
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
