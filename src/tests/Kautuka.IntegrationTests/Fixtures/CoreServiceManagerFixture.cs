using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
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
        string writerLock = Path.Combine(root, "db-writer.lock");
        await File.WriteAllTextAsync(writerLock, "", ct);
        string sockets = Path.Combine(Path.GetDirectoryName(root)!, "core-sockets");
        var paths = new CoreServicePaths(root, sockets, Path.Combine(root, "workspaces"), Path.Combine(root, "core-config"),
            writerLock, Path.Combine(root, "maintenance"));
        using var supervisor = new CoreServiceSupervisor(runner, egress, inventory, paths, new CoreServiceOptions { Enabled = true });
        try
        {
            if (!await supervisor.EnsureReadyAsync(ct))
            {
                string logs = "";
                foreach (string name in new[] { "kautuka-credentialshost", "kautuka-gateway" })
                {
                    var result = await runner.RunAsync(["logs", name], 10, ct);
                    logs += name + ": " + result.StdOut + result.StdErr;
                }
                throw new InvalidOperationException("Core service bootstrap failed: " + logs);
            }
            using JsonDocument first = JsonDocument.Parse((await runner.RunAsync(["inspect", "kautuka-credentialshost", "kautuka-gateway"], 10, ct)).StdOut);
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
            if (await ProbeExclusiveWriterLockAsync(writerLock, ct) != 1)
                throw new InvalidOperationException("Container writers do not hold the host updater's lock inode");
            if (!await supervisor.EnsureReadyAsync(ct)) throw new InvalidOperationException("Core services could not be adopted");
            using JsonDocument adopted = JsonDocument.Parse((await runner.RunAsync(["inspect", "kautuka-credentialshost", "kautuka-gateway"], 10, ct)).StdOut);
            Dictionary<string, string> adoptedIds = ServiceIds(adopted.RootElement);
            if (ids.Any(pair => adoptedIds[pair.Key] != pair.Value))
                throw new InvalidOperationException("Unchanged core services were replaced during adoption");
            report["coreCredentialsNetworkless"] = true;
            report["coreCredentialsAuthenticatedHealthRpc"] = true;
            report["coreServicesHeldHostWriterLock"] = true;
            report["coreServicesAdoptedSameIds"] = true;
        }
        finally { await supervisor.StopAsync(CancellationToken.None); }
        if (await ProbeExclusiveWriterLockAsync(writerLock, ct) != 0)
            throw new InvalidOperationException("Stopping core services did not release the updater's writer lock");
        report["coreServiceStopReleasedWriterLock"] = true;
        await Console.Error.WriteLineAsync("fixture: actual CredentialsHost and Gateway containers passed");
    }

    private static Dictionary<string, string> ServiceIds(JsonElement inspection)
    {
        Dictionary<string, string> ids = inspection.EnumerateArray().ToDictionary(
            item => item.GetProperty("Config").GetProperty("Labels").GetProperty("kautuka.core-service").GetString()!,
            item => item.GetProperty("Id").GetString()!, StringComparer.Ordinal);
        if (ids.Count != 2 || !ids.ContainsKey("credentialshost") || !ids.ContainsKey("gateway"))
            throw new InvalidOperationException("The core fixture did not inspect exactly the two expected service identities");
        return ids;
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
