using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Grpc.Core;
using Grpc.Net.Client;

using Kautuka.Hosting.Hosting;
using Kautuka.Manager.Protos;
using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;

namespace Kautuka.Manager.Hosting;

/// <summary>Checks application service readiness over its authenticated local transport.</summary>
internal static class ApplianceServiceReadiness
{
    internal static async Task<bool> CheckManagerAsync(string releaseId, bool verificationMode, CancellationToken cancellationToken)
    {
        using var configuration = new ConfigurationManager
        {
            ["Kautuka:Mode"] = "prod",
            [KautukaHostNetworkPolicy.TransportConfigurationKey] = "unix",
        };
        KautukaEndpoint endpoint = KautukaHostNetworkPolicy.Resolve(configuration).Manager;
        string stateRoot = KautukaHostPathPolicy.ResolveServiceStateRoot(configuration, "manager");
        try
        {
            using X509Certificate2 ca = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(stateRoot, ".ca.cer"));
            using X509Certificate2 clientCertificate = X509CertificateLoader.LoadPkcs12FromFile(
                Path.Combine(stateRoot, ManagerClientIdentities.UpdateServiceCertificateFileName), password: null);
            using var handler = CreateMutualTlsHandler(endpoint, ca, clientCertificate);
            using var channel = GrpcChannel.ForAddress(endpoint.BaseUri, new GrpcChannelOptions { HttpHandler = handler });
            using AsyncUnaryCall<ManagerStatusResponse> call = new ManagerService.ManagerServiceClient(channel).GetHostStatusAsync(
                new WorkspacePingRequest { Message = "appliance-readiness" },
                deadline: DateTime.UtcNow.AddSeconds(3), cancellationToken: cancellationToken);
            ManagerStatusResponse status = await call.ResponseAsync.ConfigureAwait(false);
            return status.Ok && status.CoreReady
                && string.Equals(status.ApplianceReleaseId, releaseId, StringComparison.Ordinal)
                && status.ApplianceVerificationMode == verificationMode;
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested &&
            error is RpcException or IOException or UnauthorizedAccessException or CryptographicException
                or HttpRequestException or AuthenticationException or System.Net.Sockets.SocketException)
        {
            return false;
        }
    }

    internal static async Task<bool> CheckTelemetryAsync(
        PodmanCommandRunner podman, string webHostContainerId, CancellationToken cancellationToken)
    {
        PodmanCommandResult inspection = await podman.RunAsync(
            ["inspect", "--format", "{{.State.Pid}}", webHostContainerId], 4, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (inspection.ExitCode != 0 ||
            !int.TryParse(inspection.StdOut.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0)
            return false;

        string url = FormattableString.Invariant(
            $"http://{WorkloadRulesetRenderer.EgressPodIp}:{WorkloadRulesetRenderer.TelemetryIngressPort}/api/default/v1");
        // Empty protobuf export requests exercise the real OTLP route without adding telemetry records.
        PodmanCommandResult probes = await podman.RunAsync(
            ["unshare", "nsenter", "-t", pid.ToString(CultureInfo.InvariantCulture), "-n", "curl",
             "--noproxy", "*", "--max-time", "2", "--fail-early", "--silent", "--output", "/dev/null",
             "--write-out", "%{http_code}\n", "--header", "Content-Type: application/x-protobuf", "--data-binary", "",
             url + "/logs", "--output", "/dev/null", url + "/metrics", "--output", "/dev/null", url + "/traces"],
            8, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return probes.ExitCode == 0 &&
            probes.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SequenceEqual(["200", "200", "200"], StringComparer.Ordinal);
    }

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
            using var handler = CreateMutualTlsHandler(endpoint, ca, clientCertificate);
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

    private static SocketsHttpHandler CreateMutualTlsHandler(KautukaEndpoint endpoint, X509Certificate2 ca, X509Certificate2 clientCertificate)
    {
        var policy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck,
            DisableCertificateDownloads = true,
        };
        policy.CustomTrustStore.Add(ca);
        policy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        return new SocketsHttpHandler
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
    }
}
