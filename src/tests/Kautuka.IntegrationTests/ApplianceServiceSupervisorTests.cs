using System.Text.Json;

using Kautuka.Hosting;
using Kautuka.Manager.Hosting;
using Kautuka.Manager.Protos;
using Kautuka.Telemetry.Security;
using Kautuka.Manager.RuntimeContainer.Hosting;

using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class ApplianceServiceSupervisorTests
{
    private const string Id = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string Role = "credentialshost";

    [Fact]
    public async Task RefusesToAdoptOrRemoveAContainerOwnedByAnotherProcess()
    {
        var podman = new PodmanCommandRunner((args, _) => Task.FromResult(args[1] switch
        {
            "exists" => new PodmanCommandResult(0, "", ""),
            "inspect" => new PodmanCommandResult(0, $$$"""[{"Id":"{{{Id}}}","Config":{"Labels":null}}]""", ""),
            _ => throw new InvalidOperationException($"Unexpected Podman call: {string.Join(' ', args)}"),
        }));
        var release = new VerifiedApplianceRelease("v1.2.3", "1.2.3", new ApplianceDeploymentDescriptor(1, 1,
            new Dictionary<string, string> { [Role] = "localhost/kautuka-credentials@sha256:" + Id }));
        using var supervisor = CreateSupervisor(podman, release, Path.GetTempPath());

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => supervisor.EnsureRunningAsync(Role, TestContext.Current.CancellationToken));
        Assert.Contains("outside appliance supervision", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LiveCredentialsAndChannelHostRestartWithScopedTrust()
    {
        string? fixture = Environment.GetEnvironmentVariable("KAUTUKA_CREDENTIALS_CONTAINER_TEST_ROOT");
        Assert.SkipUnless(OperatingSystem.IsLinux() && !string.IsNullOrEmpty(fixture),
            "Requires a dedicated Linux rootless Podman fixture and service archives.");
        string root = Path.GetFullPath(fixture!);
        string runtime = Path.Combine(root, "runtime");
        Assert.False(Directory.Exists(runtime));
        Directory.CreateDirectory(Path.Combine(runtime, "credentialsHost"));
        Directory.CreateDirectory(Path.Combine(runtime, Role));
        Directory.CreateDirectory(Path.Combine(runtime, "channelHost"));
        Directory.CreateDirectory(Path.Combine(runtime, "channelhost"));
        string manager = Path.Combine(runtime, "manager");
        Directory.CreateDirectory(manager);
        ChannelSecurityMaterialDirectories securityRoots = ProvisionTrust(runtime, manager);
        string writerLock = Path.Combine(runtime, "writer.lock");
        await File.WriteAllTextAsync(writerLock, string.Empty, TestContext.Current.CancellationToken);
        string images = Path.Combine(root, "service-images");
        byte[] deployment = await File.ReadAllBytesAsync(Path.Combine(images, "appliance-deployment.json"),
            TestContext.Current.CancellationToken);
        using var manifest = JsonDocument.Parse(deployment);
        string image = manifest.RootElement.GetProperty("services").GetProperty(Role).GetProperty("image").GetString()!;
        string channelImage = manifest.RootElement.GetProperty("services").GetProperty("channelhost").GetProperty("image").GetString()!;
        var release = new VerifiedApplianceRelease("v1.2.3", "1.2.3",
            new ApplianceDeploymentDescriptor(1, 1, new Dictionary<string, string> { [Role] = image, ["channelhost"] = channelImage }));
        var engine = new PodmanCommandRunner();
        string[] isolatedStore = ["--storage-driver", "vfs", "--root", Path.Combine(runtime, "store"),
            "--runroot", Path.Combine(runtime, "run")];
        var creates = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var podman = new PodmanCommandRunner((args, token) =>
        {
            if (args.Count > 2 && args[0] == "create")
            {
                string name = args[2];
                creates[name] = [.. args];
            }
            return engine.RunAsync(
                [.. isolatedStore, .. args.Select(arg => arg == "/run/kautuka-db-writer.lock:/run/kautuka-db-writer.lock:ro,z"
                    ? $"{writerLock}:/run/kautuka-db-writer.lock:ro,z" : arg)], 30, token);
        });
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        PodmanCommandResult load = await podman.RunAsync(["load", "--input", Path.Combine(images, Role + ".tar")], 30, cancellation);
        Assert.True(load.ExitCode == 0, load.StdErr);
        PodmanCommandResult channelLoad = await podman.RunAsync(["load", "--input", Path.Combine(images, "channelhost.tar")], 30, cancellation);
        Assert.True(channelLoad.ExitCode == 0, channelLoad.StdErr);

        using var supervisor = CreateSupervisor(podman, release, runtime);
        try
        {
            string first = await supervisor.EnsureRunningAsync(Role, cancellation);
            string socket = Path.Combine(runtime, Role, "credentialshost.sock");
            string transport = Path.Combine(runtime, "credentialsHost", "transport");
            await WaitForReadinessAsync(socket, Path.Combine(transport, ".ca.cer"), Path.Combine(transport, ".credentials-client.pfx"), cancellation);
            byte[] originalCa = await File.ReadAllBytesAsync(Path.Combine(transport, ".ca.cer"), cancellation);
            Assert.NotEmpty(originalCa);
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(runtime, "credentialsHost", "vault")));
            Assert.Equal(0, (await podman.RunAsync(["stop", first], 30, cancellation)).ExitCode);
            Assert.Equal(first, await supervisor.EnsureRunningAsync(Role, cancellation));
            await WaitForReadinessAsync(socket, Path.Combine(transport, ".ca.cer"), Path.Combine(transport, ".credentials-client.pfx"), cancellation);
            Assert.Equal(originalCa, await File.ReadAllBytesAsync(Path.Combine(transport, ".ca.cer"), cancellation));

            string channel = await supervisor.EnsureRunningAsync("channelhost", cancellation);
            string channelSocket = Path.Combine(runtime, "channelhost", "channelhost.sock");
            string channelCa = Path.Combine(securityRoots.GatewayTrustRoot, ChannelSecurityMaterialProvisioner.CaCertificate);
            string gatewayClient = Path.Combine(securityRoots.GatewayPrivateRoot, ChannelSecurityMaterialProvisioner.GatewayClientPfx);
            await WaitForReadinessAsync(channelSocket, channelCa, gatewayClient, cancellation);
            Assert.True(creates.TryGetValue("kautuka-appliance-channelhost", out string[]? channelArgs));
            Assert.Contains(channelArgs!, arg => arg.Contains(securityRoots.ChannelPrivateRoot, StringComparison.Ordinal));
            Assert.Contains(channelArgs!, arg => arg.Contains(securityRoots.ChannelTrustRoot, StringComparison.Ordinal));
            Assert.DoesNotContain(channelArgs!, arg => arg.Contains(securityRoots.GatewayPrivateRoot, StringComparison.Ordinal));
            Assert.DoesNotContain(channelArgs!, arg => arg.Contains(securityRoots.AuthorityRoot!, StringComparison.Ordinal));
            PodmanCommandResult channelInspect = await podman.RunAsync(["container", "inspect", channel], 15, cancellation);
            Assert.Equal(0, channelInspect.ExitCode);
            AssertChannelMounts(channelInspect.StdOut, securityRoots);
            Assert.Equal(0, (await podman.RunAsync(["stop", channel], 30, cancellation)).ExitCode);
            Assert.True(await ApplianceServiceReadiness.CheckAsync(socket, Path.Combine(transport, ".ca.cer"), Path.Combine(transport, ".credentials-client.pfx"), cancellation));
            Assert.Equal(channel, await supervisor.EnsureRunningAsync("channelhost", cancellation));
            await WaitForReadinessAsync(channelSocket, channelCa, gatewayClient, cancellation);
            Assert.True(await ApplianceServiceReadiness.CheckAsync(socket, Path.Combine(transport, ".ca.cer"), Path.Combine(transport, ".credentials-client.pfx"), cancellation));
        }
        catch (Exception ex)
        {
            PodmanCommandResult logs = await podman.RunAsync(["logs", "kautuka-appliance-credentialshost"], 10, CancellationToken.None);
            PodmanCommandResult channelLogs = await podman.RunAsync(["logs", "kautuka-appliance-channelhost"], 10, CancellationToken.None);
            throw new InvalidOperationException($"{ex.Message}\nCredentialsHost logs:\n{logs.StdOut}\n{logs.StdErr}\nChannelHost logs:\n{channelLogs.StdOut}\n{channelLogs.StdErr}", ex);
        }
        finally
        {
            await podman.RunAsync(["rm", "--force", "--ignore", "--time", "0", "kautuka-appliance-channelhost"], 20, CancellationToken.None);
            await podman.RunAsync(["rm", "--force", "--ignore", "--time", "0", "kautuka-appliance-credentialshost"],
                20, CancellationToken.None);
        }
    }

    private static ChannelSecurityMaterialDirectories ProvisionTrust(string runtime, string manager)
    {
        var (server, ca) = LocalCaMtlsBootstrap.EnsureAndLoad(manager, "Manager",
            [new LocalMtlsClientCertificate(ManagerClientIdentities.ChannelHostCommonName, ManagerClientIdentities.ChannelHostCertificateFileName)]);
        server.Dispose();
        ca.Dispose();
        string security = Path.Combine(runtime, "security");
        var roots = new ChannelSecurityMaterialDirectories(
            Path.Combine(security, "gateway-private"), Path.Combine(security, "gateway-trust"),
            Path.Combine(security, "channel-private"), Path.Combine(security, "channel-trust"),
            Path.Combine(security, "offline-authority"));
        ChannelSecurityMaterialProvisioner.Provision(roots);
        return roots;
    }
    private static void AssertChannelMounts(string json, ChannelSecurityMaterialDirectories roots)
    {
        using JsonDocument inspected = JsonDocument.Parse(json);
        var mounted = inspected.RootElement[0].GetProperty("Mounts").EnumerateArray()
            .Select(mount => (Source: mount.GetProperty("Source").GetString()!, ReadWrite: mount.GetProperty("RW").GetBoolean())).ToArray();
        Assert.Contains(mounted, mount => mount.Source == roots.ChannelPrivateRoot && !mount.ReadWrite);
        Assert.Contains(mounted, mount => mount.Source == roots.ChannelTrustRoot && !mount.ReadWrite);
        Assert.DoesNotContain(mounted, mount => mount.Source == roots.GatewayPrivateRoot || mount.Source == roots.AuthorityRoot);
    }
    private static ApplianceServiceSupervisor CreateSupervisor(PodmanCommandRunner podman, VerifiedApplianceRelease release, string root)
        => new(podman, release, root, root, root, NullLogger<ApplianceServiceSupervisor>.Instance);

    private static async Task WaitForReadinessAsync(string socket, string caPath, string clientPfxPath, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            if (await ApplianceServiceReadiness.CheckAsync(socket, caPath, clientPfxPath, cancellationToken))
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
        Assert.Fail("Service did not answer the authenticated health probe.");
    }
}
