using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Manager.Protos;
using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Telemetry.Security;

namespace Kautuka.IntegrationTests.Fixtures;

internal static class CoreServiceManagerFixture
{
    internal static async Task RunAsync(JsonElement input, PodmanCommandRunner runner, ContainerEgressRuntime egress,
        RuntimeImageAllowlist inventory, JsonObject report, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Core service fixture requires Linux.");
        string root = input.GetProperty("stateRoot").GetString()!;
        foreach ((string service, string file) in new[] { ("manager", ManagerClientIdentities.GatewayCertificateFileName), ("updateService", ".updateservice-client.pfx") })
        {
            string canonical = Path.Combine(root, service);
            var (server, ca) = LocalCaMtlsBootstrap.EnsureAndLoad(canonical, service, [new LocalMtlsClientCertificate(
                service == "manager" ? ManagerClientIdentities.GatewayCommonName : "Kautuka Gateway Client", file)]);
            server.Dispose(); ca.Dispose();
            LocalCaMtlsBootstrap.PublishGatewayClientIdentity(canonical, file);
        }
        string channelSecurity = Path.Combine(root, "security", "channel");
        ChannelSecurityMaterialProvisioner.Provision(new ChannelSecurityMaterialDirectories(
            Path.Combine(channelSecurity, "gateway-private"), Path.Combine(channelSecurity, "gateway-trust"),
            Path.Combine(channelSecurity, "channel-private"), Path.Combine(channelSecurity, "channel-trust")));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int webPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var options = new CoreServiceOptions { Enabled = true, WebHostPort = webPort };
        string writerLock = Path.Combine(root, "db-writer.lock");
        await File.WriteAllTextAsync(writerLock, "", ct);
        string sockets = Path.Combine(Path.GetDirectoryName(root)!, "core-sockets");
        var paths = new CoreServicePaths(root, sockets, Path.Combine(root, "workspaces"), Path.Combine(root, "core-config"),
            writerLock, Path.Combine(root, "maintenance"));
        using var supervisor = new CoreServiceSupervisor(runner, egress, inventory, paths, options);
        try
        {
            if (!await supervisor.EnsureReadyAsync(ct))
            {
                string logs = "";
                foreach (string name in new[] { "kautuka-credentialshost", "kautuka-channelhost", "kautuka-gateway", "kautuka-webhost" })
                {
                    var result = await runner.RunAsync(["logs", name], 10, ct);
                    logs += name + ": " + result.StdOut + result.StdErr;
                }
                throw new InvalidOperationException("Core service bootstrap failed: " + logs);
            }
            using JsonDocument first = JsonDocument.Parse((await runner.RunAsync(["inspect", "kautuka-credentialshost", "kautuka-channelhost", "kautuka-gateway", "kautuka-webhost"], 10, ct)).StdOut);
            Dictionary<string, string> ids = ServiceIds(first.RootElement);
            JsonElement credentials = first.RootElement.EnumerateArray().Single(item => item.GetProperty("Config").GetProperty("Labels").GetProperty("kautuka.core-service").GetString() == "credentialshost");
            var interfaces = await runner.RunAsync(["run", "--rm", "--pull=never", "--network", "container:" + credentials.GetProperty("Id").GetString(),
                "--label", "kautuka.preparation-helper=true", "--cap-drop=ALL", "--security-opt=no-new-privileges", "--read-only", "--user", "65533:65533",
                "--entrypoint", "/usr/sbin/ip", input.GetProperty("helper").GetString()!, "-j", "addr"], 15, ct);
            if (interfaces.ExitCode != 0) throw new InvalidOperationException("Cannot inspect CredentialsHost's live namespace: " + interfaces.StdErr);
            using JsonDocument links = JsonDocument.Parse(interfaces.StdOut);
            if (links.RootElement.EnumerateArray().Any(item => item.GetProperty("ifname").GetString() != "lo"))
                throw new InvalidOperationException("CredentialsHost has a non-loopback interface: " + interfaces.StdOut);
            if (credentials.GetProperty("HostConfig").GetProperty("NetworkMode").GetString() != "none")
                throw new InvalidOperationException("CredentialsHost network metadata: mode=" + credentials.GetProperty("HostConfig").GetProperty("NetworkMode").GetString()
                    + "; networks=" + credentials.GetProperty("NetworkSettings").GetProperty("Networks").GetRawText() + "; interfaces=" + interfaces.StdOut);
            report["coreCredentialsInterfaces"] = JsonNode.Parse(interfaces.StdOut);
            report["coreCredentialsNetworkMetadata"] = JsonNode.Parse(credentials.GetProperty("NetworkSettings").GetProperty("Networks").GetRawText());
            await CheckCredentialRpcAsync(root, sockets, ct);
            await CheckWebAsync(webPort, null, ct);
            if (await ProbeExclusiveWriterLockAsync(writerLock, ct) != 1)
                throw new InvalidOperationException("Container writers do not hold the host updater's lock inode");
            if (!await supervisor.EnsureReadyAsync(ct)) throw new InvalidOperationException("Core services could not be adopted");
            using JsonDocument adopted = JsonDocument.Parse((await runner.RunAsync(["inspect", "kautuka-credentialshost", "kautuka-channelhost", "kautuka-gateway", "kautuka-webhost"], 10, ct)).StdOut);
            Dictionary<string, string> adoptedIds = ServiceIds(adopted.RootElement);
            if (ids.Any(pair => adoptedIds[pair.Key] != pair.Value))
                throw new InvalidOperationException("Unchanged core services were replaced during adoption");
            string certificateDirectory = Path.Combine(root, "security", "webhost");
            Directory.CreateDirectory(certificateDirectory);
            using RSA key = RSA.Create(2048);
            var request = new CertificateRequest("CN=Kautuka WebHost Fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddIpAddress(IPAddress.Loopback); san.AddDnsName("localhost");
            request.CertificateExtensions.Add(san.Build());
            using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            await File.WriteAllBytesAsync(Path.Combine(certificateDirectory, "edge.pfx"), certificate.Export(X509ContentType.Pkcs12), ct);
            options.WebHostCertificateDirectory = certificateDirectory;
            if (!await supervisor.EnsureReadyAsync(ct)) throw new InvalidOperationException("TLS WebHost did not become ready");
            await CheckWebAsync(webPort, certificate, ct);
            using JsonDocument tlsInspection = JsonDocument.Parse((await runner.RunAsync(["inspect", "kautuka-credentialshost", "kautuka-channelhost", "kautuka-gateway", "kautuka-webhost"], 10, ct)).StdOut);
            Dictionary<string, string> tlsIds = ServiceIds(tlsInspection.RootElement);
            if (tlsIds["webhost"] == ids["webhost"] || ids.Any(pair => pair.Key != "webhost" && tlsIds[pair.Key] != pair.Value))
                throw new InvalidOperationException("Enabling TLS did not replace only WebHost");
            report["coreWebPublishedHttpAndTls"] = true;
            report["coreWebForwardedGatewaySetupStatus"] = true;
            report["coreTlsReplacedOnlyWebHost"] = true;
            report["coreCredentialsNetworkless"] = true;
            report["coreCredentialsAuthenticatedHealthRpc"] = true;
            report["coreServicesHeldHostWriterLock"] = true;
            report["coreServicesAdoptedSameIds"] = true;
        }
        finally { await supervisor.StopAsync(CancellationToken.None); }
        if (await ProbeExclusiveWriterLockAsync(writerLock, ct) != 0)
            throw new InvalidOperationException("Stopping core services did not release the updater's writer lock");
        report["coreServiceStopReleasedWriterLock"] = true;
        await Console.Error.WriteLineAsync("fixture: four actual application containers and HTTP/TLS publication passed");
    }

    private static Dictionary<string, string> ServiceIds(JsonElement inspection)
    {
        Dictionary<string, string> ids = inspection.EnumerateArray().ToDictionary(
            item => item.GetProperty("Config").GetProperty("Labels").GetProperty("kautuka.core-service").GetString()!,
            item => item.GetProperty("Id").GetString()!, StringComparer.Ordinal);
        if (ids.Count != 4 || new[] { "credentialshost", "channelhost", "gateway", "webhost" }.Any(service => !ids.ContainsKey(service)))
            throw new InvalidOperationException("The core fixture did not inspect exactly the four expected service identities");
        return ids;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5400", Justification = "Local readiness pins the installed certificate; online revocation is not part of this check.")]
    private static async Task CheckWebAsync(int port, X509Certificate2? certificate, CancellationToken ct)
    {
        using var handler = new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false };
        if (certificate is not null)
            handler.ServerCertificateCustomValidationCallback = (_, peer, _, _) => peer is not null
                && peer.GetCertHashString(HashAlgorithmName.SHA256) == certificate.GetCertHashString(HashAlgorithmName.SHA256);
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(10),
            BaseAddress = new Uri((certificate is null ? "http" : "https") + "://127.0.0.1:" + port)
        };
        using HttpResponseMessage health = await client.GetAsync(new Uri("/health", UriKind.Relative), ct);
        if (health.StatusCode != HttpStatusCode.OK || await health.Content.ReadAsStringAsync(ct) != "{\"status\":\"ok\"}")
            throw new InvalidOperationException("The published WebHost did not serve its health response");
        using HttpResponseMessage setup = await client.GetAsync(new Uri("/api/auth/setup/status", UriKind.Relative), ct);
        using JsonDocument body = JsonDocument.Parse(await setup.Content.ReadAsStringAsync(ct));
        if (setup.StatusCode != HttpStatusCode.OK || !body.RootElement.TryGetProperty("needsSetup", out JsonElement needsSetup) || !needsSetup.GetBoolean())
            throw new InvalidOperationException("WebHost did not forward the first-run Gateway setup response over its Unix socket");
    }

    private static async Task CheckCredentialRpcAsync(string root, string sockets, CancellationToken ct)
    {
        string published = Path.Combine(root, "credentialsHost", "transport", "clients", "gateway");
        using var ca = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(published, ".ca.cer"));
        using var identity = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(published, ".credentials-client.pfx"), null);
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try { await socket.ConnectAsync(new UnixDomainSocketEndPoint(Path.Combine(sockets, "credentialshost", "credentialshost.sock")), token); }
                catch { socket.Dispose(); throw; }
                return new NetworkStream(socket, ownsSocket: true);
            },
        };
        handler.SslOptions.ClientCertificates = new X509CertificateCollection { identity };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, _) => certificate is X509Certificate2 peer
            && LocalCaMtlsBootstrap.ValidateExactServerCert(peer, ca, "Credentials Server", "localhost");
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://localhost/kautuka.credentials.v1.CredentialsService/GetHealth")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = new ByteArrayContent([0, 0, 0, 0, 0]),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        request.Headers.TryAddWithoutValidation("TE", "trailers");
        using HttpResponseMessage response = await client.SendAsync(request, ct);
        byte[] body = await response.Content.ReadAsByteArrayAsync(ct);
        if (response.StatusCode != HttpStatusCode.OK || body.Length < 5 || body[0] != 0
            || BinaryPrimitives.ReadInt32BigEndian(body.AsSpan(1, 4)) != body.Length - 5
            || !response.TrailingHeaders.TryGetValues("grpc-status", out IEnumerable<string>? status) || status.Single() != "0"
            || Kautuka.Protos.CredentialHealthResponse.Parser.ParseFrom(body.AsSpan(5)).State != Kautuka.Protos.CredentialHealthState.Ready)
            throw new InvalidOperationException("The networkless CredentialsHost did not serve an authenticated ready-health RPC");
    }

    private static async Task<int> ProbeExclusiveWriterLockAsync(string path, CancellationToken ct)
    {
        var start = new ProcessStartInfo("flock") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (string argument in new[] { "--exclusive", "--nonblock", path, "true" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start the native writer-lock probe");
        await process.WaitForExitAsync(ct);
        return process.ExitCode;
    }
}
