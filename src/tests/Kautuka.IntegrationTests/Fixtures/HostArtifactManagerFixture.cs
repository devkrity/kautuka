using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Hosting.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting;

namespace Kautuka.IntegrationTests.Fixtures;

internal static class HostArtifactManagerFixture
{
    internal static async Task RunAsync(JsonElement input, PodmanCommandRunner runner, ContainerEgressRuntime runtime, JsonObject report, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Host artifact proof requires Linux socket permissions.");
        using X509Certificate2 authority = X509Certificate2.CreateFromPem(await File.ReadAllTextAsync(input.GetProperty("artifactCertificate").GetString()!, ct));
        using SocketsHttpHandler handler = KautukaArtifactHttpTransport.Create(runtime.ArtifactSocketPath);
        handler.SslOptions.CertificateChainPolicy = new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust, RevocationMode = X509RevocationMode.NoCheck };
        handler.SslOptions.CertificateChainPolicy.CustomTrustStore.Add(authority);
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.ConnectionClose = true;
        async Task AllowedAsync(string path)
        {
            if (await client.GetStringAsync(new Uri("https://artifact.kautuka.test/" + path), ct) != "artifact-ok")
                throw new InvalidOperationException("The typed host artifact client did not reach the controlled TLS server.");
        }
        async Task DeniedAsync(string url, bool requireProxyDenial = true)
        {
            try { await client.GetStringAsync(new Uri(url), ct); }
            catch (HttpRequestException error) when (!requireProxyDenial || (error.StatusCode == HttpStatusCode.Forbidden
                && (!url.StartsWith("https://", StringComparison.Ordinal) || error.HttpRequestError == HttpRequestError.ProxyTunnelError)))
            { return; }
            throw new InvalidOperationException("The host artifact client admitted a forbidden request: " + url);
        }
        await AllowedAsync("artifact");
        await AllowedAsync("redirect-allowed");
        await DeniedAsync("https://artifact.kautuka.test/redirect-denied");
        await DeniedAsync("https://artifact.kautuka.test:444/artifact");
        await DeniedAsync("http://artifact.kautuka.test/artifact");
        UnixFileMode mode = File.GetUnixFileMode(runtime.ArtifactSocketPath);
        if (mode != (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite))
            throw new InvalidOperationException("The artifact socket is not mode 0660.");
        PodmanCommandResult ownership = await runner.RunAsync(["unshare", "stat", "--format=%u:%g", runtime.ArtifactSocketPath], 10, ct);
        if (ownership.ExitCode != 0 || ownership.StdOut.Trim() != "65532:0")
            throw new InvalidOperationException("The artifact socket did not inherit the rootless runtime owner's mapped group.");
        PodmanCommandResult stop = await runner.RunAsync(["stop", "--time", "1", "kautuka-egress-envoy"], 15, ct);
        if (stop.ExitCode != 0) throw new InvalidOperationException("Could not stop Envoy for the host outage proof.");
        await DeniedAsync("https://artifact.kautuka.test/artifact", requireProxyDenial: false);
        await runtime.EnsureReadyAsync(ct);
        await AllowedAsync("artifact");
        report["hostArtifactTypedClientTls"] = true;
        report["hostArtifactRedirectPolicy"] = true;
        report["hostArtifactUnknownPortAndPlainHttpDenied"] = true;
        report["hostArtifactSocketGroupAndMode"] = true;
        report["hostArtifactOutageFailedClosedAndSocketRecreated"] = true;
        await Console.Error.WriteLineAsync("fixture: host artifact TLS, deny rules, permissions and Envoy restart passed");
    }
}
