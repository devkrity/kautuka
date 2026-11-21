using System.Text.Json;

using Kautuka.Hosting;
using Kautuka.Manager.Hosting;
using Kautuka.Manager.Protos;
using Kautuka.Telemetry.Security;
using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.NetworkAccessController;

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
    public async Task GatewayCannotStartBeforeBoundaryAdmission()
    {
        int calls = 0;
        var podman = new PodmanCommandRunner((_, _) => { calls++; throw new InvalidOperationException("Unexpected Podman call."); });
        var release = new VerifiedApplianceRelease("v1", "1", new ApplianceDeploymentDescriptor(1, 1,
            new Dictionary<string, string> { ["gateway"] = "registry.example/gateway@sha256:" + Id }));
        using var supervisor = CreateSupervisor(podman, release, Path.GetTempPath());
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => supervisor.EnsureRunningAsync("gateway", TestContext.Current.CancellationToken));
        Assert.Contains("egress boundary", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task GatewayAllowlistBindsAndRevokesExactContainerAndImage()
    {
        string root = Path.Combine(Path.GetTempPath(), "gateway-admission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var podman = new PodmanCommandRunner((_, _) => throw new InvalidOperationException("Unexpected Podman call."));
        await using var topology = new EgressTopologyManager(podman, new WorkloadAddressAllocator(Path.Combine(root, "ip.json")),
            new WorkloadPostStartVerifier(podman), new WorkloadRegistry(), root,
            hookStateDirectory: root, gatewayConnectEnvoyDirectory: Path.Combine(root, "envoy"));
        try
        {
            topology.WriteInfraAllowlist([]);
            Assert.Throws<InvalidOperationException>(() => topology.AuthorizeApplianceService("appliance-gateway", Id, "not-an-image-id"));
            topology.AuthorizeApplianceService("appliance-gateway", Id, "sha256:" + Id);
            using (JsonDocument allowlist = ReadAllowlist(root))
            {
                JsonElement entry = Assert.Single(allowlist.RootElement.GetProperty("entries").EnumerateArray());
                Assert.Equal(Id, entry.GetProperty("id").GetString());
                Assert.Equal("appliance-gateway", entry.GetProperty("role").GetString());
                Assert.Equal("sha256:" + Id, entry.GetProperty("image_digest").GetString());
                Assert.Equal("10.247.0.4", Assert.Single(entry.GetProperty("expected_ipv4").EnumerateArray()).GetString());
            }
            Assert.Throws<InvalidOperationException>(() => topology.AuthorizeApplianceService("appliance-gateway", new string('a', 64), "sha256:" + Id));
            topology.RevokeApplianceService("appliance-gateway", Id);
            using JsonDocument emptyAllowlist = ReadAllowlist(root);
            Assert.Empty(emptyAllowlist.RootElement.GetProperty("entries").EnumerateArray());
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GatewayVerificationFailureStopsAndRevokesBeforeAdoption(bool alreadyRunning)
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Native hook fixture requires Linux paths.");
        string root = Path.Combine(Path.GetTempPath(), "gateway-verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string version = ProvisionHook(root, root);
        var calls = new List<string>();
        var podman = new PodmanCommandRunner((args, _) =>
        {
            string operation = args[0] == "--hooks-dir" ? args[2] : args[0];
            calls.Add(operation);
            if (operation is "start" or "inspect")
            {
                using JsonDocument allowlist = ReadAllowlist(root);
                JsonElement entry = Assert.Single(allowlist.RootElement.GetProperty("entries").EnumerateArray());
                Assert.Equal(Id, entry.GetProperty("id").GetString());
                Assert.Equal("sha256:" + Id, entry.GetProperty("image_digest").GetString());
            }
            return Task.FromResult(operation switch
            {
                "start" => new PodmanCommandResult(0, "", ""),
                "inspect" => new PodmanCommandResult(1, "", "namespace verification unavailable"),
                "stop" => new PodmanCommandResult(0, "", ""),
                _ => throw new InvalidOperationException("Unexpected Podman call."),
            });
        });
        await using var topology = new EgressTopologyManager(podman, new WorkloadAddressAllocator(Path.Combine(root, "ip.json")),
            new WorkloadPostStartVerifier(podman), new WorkloadRegistry(), root,
            hookStateDirectory: root, gatewayConnectEnvoyDirectory: Path.Combine(root, "envoy"));
        var release = new VerifiedApplianceRelease("v1", "1", new ApplianceDeploymentDescriptor(1, 1,
            new Dictionary<string, string> { ["gateway"] = "registry.example/gateway@sha256:" + Id }));
        using var supervisor = new ApplianceServiceSupervisor(podman, release, version, root, root,
            NullLogger<ApplianceServiceSupervisor>.Instance, topology: topology);
        try
        {
            topology.WriteInfraAllowlist([]);
            typeof(EgressTopologyManager).GetProperty(nameof(EgressTopologyManager.IsStartupGateOpen))!.SetValue(topology, true);
            object service = typeof(ApplianceServiceSupervisor).GetField("gateway", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(supervisor)!;
            service.GetType().GetProperty("ActiveContainer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(service, Id);
            var verify = typeof(ApplianceServiceSupervisor).GetMethod("VerifyNetworkedServiceAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            using JsonDocument wrongNetwork = JsonDocument.Parse($$"""{ "Id": "{{Id}}", "NetworkSettings": { "Networks": {} } }""");
            JsonElement? inspection = alreadyRunning ? wrongNetwork.RootElement : null;
            Task action = (Task)verify.Invoke(supervisor, [service, Id, "sha256:" + Id, inspection, TestContext.Current.CancellationToken])!;
            await Assert.ThrowsAsync<InvalidOperationException>(() => action);
            Assert.Equal(alreadyRunning ? ["stop"] : ["start", "inspect", "stop"], calls);
            using JsonDocument allowlist = ReadAllowlist(root);
            Assert.Empty(allowlist.RootElement.GetProperty("entries").EnumerateArray());
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void GatewayCreateProfileMountsOnlyScopedPeerMaterial()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Gateway appliance mounts require Linux paths.");
        string root = Path.Combine(Path.GetTempPath(), "gateway-mounts-" + Guid.NewGuid().ToString("N"));
        string state = Path.Combine(root, "state");
        string sockets = Path.Combine(root, "sockets");
        string connect = Path.Combine(root, "connect");
        string version = ProvisionHook(root, state);
        foreach (string path in new[] { Path.Combine(state, "gateway"), Path.Combine(state, "workspaces"), Path.Combine(state, "manager"),
            Path.Combine(state, "credentialsHost", "transport"), Path.Combine(state, "security", "channel", "gateway-private"),
            Path.Combine(state, "security", "channel", "gateway-trust"), Path.Combine(state, "security", "channel", "channel-private"),
            Path.Combine(state, "security", "channel", "offline-authority"), Path.Combine(sockets, "gateway"),
            Path.Combine(sockets, "manager"), Path.Combine(sockets, "credentialshost"), Path.Combine(sockets, "channelhost"), connect })
            Directory.CreateDirectory(path);
        foreach (string file in new[] { "manager/.ca.cer", "manager/" + ManagerClientIdentities.GatewayCertificateFileName,
            "credentialsHost/transport/.ca.cer", "credentialsHost/transport/.credentials-client.pfx" })
            File.WriteAllText(Path.Combine(state, file), "fixture");
        var podman = new PodmanCommandRunner((_, _) => throw new InvalidOperationException("Unexpected Podman call."));
        var release = new VerifiedApplianceRelease("v1", "1", new ApplianceDeploymentDescriptor(1, 1,
            new Dictionary<string, string> { ["gateway"] = "registry.example/gateway@sha256:" + Id }));
        using var supervisor = new ApplianceServiceSupervisor(podman, release, version, state, sockets,
            NullLogger<ApplianceServiceSupervisor>.Instance, gatewayConnectDirectory: connect);
        try
        {
            object service = typeof(ApplianceServiceSupervisor).GetField("gateway", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(supervisor)!;
            var build = typeof(ApplianceServiceSupervisor).GetMethod("BuildCreateArgs", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var args = Assert.IsType<List<string>>(build.Invoke(supervisor, [service, release.Deployment.Images["gateway"], "sha256:" + Id]));
            Assert.Contains("io.kautuka.role=appliance-gateway", args);
            Assert.Contains("io.kautuka.image-id=sha256:" + Id, args);
            Assert.Contains(args, item => item.Contains("kautuka-workload:ip=10.247.0.4", StringComparison.Ordinal));
            Assert.Contains(args, item => item.StartsWith(connect + ":/run/kautuka-gateway-connect:ro", StringComparison.Ordinal));
            Assert.Contains(args, item => item.StartsWith(Path.Combine(state, "security", "channel", "gateway-private") + ":", StringComparison.Ordinal));
            Assert.DoesNotContain(args, item => item.Contains(Path.Combine(state, "security", "channel", "channel-private"), StringComparison.Ordinal));
            Assert.DoesNotContain(args, item => item.Contains(Path.Combine(state, "security", "channel", "offline-authority"), StringComparison.Ordinal));
            Assert.DoesNotContain(args, item => item.StartsWith(Path.Combine(state, "credentialsHost") + ":", StringComparison.Ordinal));
            Assert.DoesNotContain(args, item => item.StartsWith(Path.Combine(state, "manager") + ":", StringComparison.Ordinal));
            string firstFingerprint = Assert.Single(args, item => item.StartsWith("io.kautuka.appliance.peer-credentials=", StringComparison.Ordinal));
            File.WriteAllText(Path.Combine(state, "manager", ".ca.cer"), "rotated");
            var rotated = Assert.IsType<List<string>>(build.Invoke(supervisor, [service, release.Deployment.Images["gateway"], "sha256:" + Id]));
            string rotatedFingerprint = Assert.Single(rotated, item => item.StartsWith("io.kautuka.appliance.peer-credentials=", StringComparison.Ordinal));
            Assert.NotEqual(firstFingerprint, rotatedFingerprint);
        }
        finally { Directory.Delete(root, true); }
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
        string sockets = Path.Combine(runtime, "sockets");
        foreach (string role in new[] { Role, "channelhost", "gateway", "manager" })
            Directory.CreateDirectory(Path.Combine(sockets, role));
        Directory.CreateDirectory(Path.Combine(runtime, "channelHost"));
        string manager = Path.Combine(runtime, "manager");
        Directory.CreateDirectory(manager);
        ChannelSecurityMaterialDirectories securityRoots = ProvisionTrust(runtime, manager);
        string writerLock = Path.Combine(runtime, "writer.lock");
        await File.WriteAllTextAsync(writerLock, string.Empty, TestContext.Current.CancellationToken);
        string images = Path.Combine(root, "service-images");
        string? gatewayHook = Environment.GetEnvironmentVariable("KAUTUKA_GATEWAY_CONTAINER_TEST_HOOK");
        bool gatewayEnabled = !string.IsNullOrWhiteSpace(gatewayHook) && File.Exists(Path.Combine(images, "gateway.tar"));
        bool webHostEnabled = gatewayEnabled && Environment.GetEnvironmentVariable("KAUTUKA_WEBHOST_CONTAINER_TEST") == "1";
        string? fixtureHooks = null;
        byte[] deployment = await File.ReadAllBytesAsync(Path.Combine(images, "appliance-deployment.json"),
            TestContext.Current.CancellationToken);
        using var manifest = JsonDocument.Parse(deployment);
        string image = manifest.RootElement.GetProperty("services").GetProperty(Role).GetProperty("image").GetString()!;
        string channelImage = manifest.RootElement.GetProperty("services").GetProperty("channelhost").GetProperty("image").GetString()!;
        var imageMap = new Dictionary<string, string> { [Role] = image, ["channelhost"] = channelImage };
        if (gatewayEnabled) imageMap["gateway"] = manifest.RootElement.GetProperty("services").GetProperty("gateway").GetProperty("image").GetString()!;
        if (webHostEnabled) imageMap["webhost"] = manifest.RootElement.GetProperty("services").GetProperty("webhost").GetProperty("image").GetString()!;
        var release = new VerifiedApplianceRelease("v1.2.3", "1.2.3", new ApplianceDeploymentDescriptor(1, 1, imageMap));
        var engine = new PodmanCommandRunner();
        string[] isolatedStore = gatewayEnabled
            ? ["--storage-driver", "vfs", "--root", Path.Combine(runtime, "store"), "--runroot", Path.Combine(runtime, "run"), "--network-config-dir", Path.Combine(runtime, "network-config")]
            : ["--storage-driver", "vfs", "--root", Path.Combine(runtime, "store"), "--runroot", Path.Combine(runtime, "run")];
        var creates = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var calls = new List<string[]>();
        var podman = new PodmanCommandRunner(async (args, token) =>
        {
            calls.Add([.. args]);
            if (args.Count > 2 && args[0] == "create")
            {
                string name = args[2];
                creates[name] = [.. args];
            }
            PodmanCommandResult result = await engine.RunAsync(
                [.. isolatedStore, .. args.Select((arg, index) => arg == "/run/kautuka-db-writer.lock:/run/kautuka-db-writer.lock:ro,z"
                    ? $"{writerLock}:/run/kautuka-db-writer.lock:ro,z"
                    : fixtureHooks is not null && index > 0 && args[index - 1] == "--hooks-dir" ? fixtureHooks
                    : webHostEnabled && arg == "127.0.0.1:20229:20229" ? "127.0.0.1::20229" : arg)], 30, token);
            if (result.ExitCode == 0 && fixtureHooks is not null && args.Count == 3 && args[0] == "container" && args[1] == "inspect"
                && args[2] is "kautuka-appliance-gateway" or "kautuka-appliance-webhost")
                return result with { StdOut = NormalizeFixtureCreateCommand(result.StdOut, isolatedStore, fixtureHooks, writerLock, runtime) };
            return result;
        });
        CancellationToken cancellation = TestContext.Current.CancellationToken;
        PodmanCommandResult load = await podman.RunAsync(["load", "--input", Path.Combine(images, Role + ".tar")], 30, cancellation);
        Assert.True(load.ExitCode == 0, load.StdErr);
        PodmanCommandResult channelLoad = await podman.RunAsync(["load", "--input", Path.Combine(images, "channelhost.tar")], 30, cancellation);
        Assert.True(channelLoad.ExitCode == 0, channelLoad.StdErr);

        using var supervisor = new ApplianceServiceSupervisor(podman, release, root, runtime, sockets, NullLogger<ApplianceServiceSupervisor>.Instance);
        try
        {
            string first = await supervisor.EnsureRunningAsync(Role, cancellation);
            string socket = Path.Combine(sockets, Role, "credentialshost.sock");
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
            string channelSocket = Path.Combine(sockets, "channelhost", "channelhost.sock");
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
            if (gatewayEnabled)
            {
                Assert.True(OperatingSystem.IsLinux());
                await RunGatewayFixtureAsync(root, runtime, sockets, images, gatewayHook!, podman, release, hooks => fixtureHooks = hooks, cancellation);
                Assert.Contains(calls, call => call.SequenceEqual(["image", "inspect", "--format", "{{.Id}}", release.Deployment.Images["gateway"]]));
                Assert.Equal(webHostEnabled ? 3 : 2, calls.Count(call => call.Length == 2 && call[0] == "inspect"));
            }
        }
        catch (Exception ex)
        {
            PodmanCommandResult logs = await podman.RunAsync(["logs", "kautuka-appliance-credentialshost"], 10, CancellationToken.None);
            PodmanCommandResult channelLogs = await podman.RunAsync(["logs", "kautuka-appliance-channelhost"], 10, CancellationToken.None);
            PodmanCommandResult gatewayLogs = await podman.RunAsync(["logs", "kautuka-appliance-gateway"], 10, CancellationToken.None);
            PodmanCommandResult webHostLogs = await podman.RunAsync(["logs", "kautuka-appliance-webhost"], 10, CancellationToken.None);
            throw new InvalidOperationException($"{ex.Message}\nCredentialsHost logs:\n{logs.StdOut}\n{logs.StdErr}\nChannelHost logs:\n{channelLogs.StdOut}\n{channelLogs.StdErr}\nGateway logs:\n{gatewayLogs.StdOut}\n{gatewayLogs.StdErr}\nWebHost logs:\n{webHostLogs.StdOut}\n{webHostLogs.StdErr}", ex);
        }
        finally
        {
            await podman.RunAsync(["rm", "--force", "--ignore", "--time", "0", "kautuka-appliance-gateway"], 20, CancellationToken.None);
            await podman.RunAsync(["rm", "--force", "--ignore", "--time", "0", "kautuka-appliance-channelhost"], 20, CancellationToken.None);
            await podman.RunAsync(["rm", "--force", "--ignore", "--time", "0", "kautuka-appliance-credentialshost"],
                20, CancellationToken.None);
        }
    }

    private static async Task RunGatewayFixtureAsync(string root, string runtime, string sockets, string images, string nativeHook,
        PodmanCommandRunner podman, VerifiedApplianceRelease release, Action<string> setFixtureHooks, CancellationToken token)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        string version = ProvisionHook(root, runtime);
        string installedHook = Path.Combine(version, "scripts", "kautuka-oci-hook");
        File.Copy(nativeHook, installedHook, overwrite: true);
        File.SetUnixFileMode(installedHook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string hookStateHome = Path.Combine(runtime, "isolated-hook-state");
        string hookState = Path.Combine(hookStateHome, "kautuka", "nac");
        Directory.CreateDirectory(hookState);
        string fixtureHooks = Path.Combine(runtime, "isolated-hooks");
        Directory.CreateDirectory(fixtureHooks);
        string currentHook = Path.Combine(root, "manager-install", "current", "scripts", "kautuka-oci-hook");
        await File.WriteAllTextAsync(Path.Combine(fixtureHooks, "fixture.json"), JsonSerializer.Serialize(new
        {
            version = "1.0.0",
            hook = new { path = currentHook, timeout = 45, env = new[] { "XDG_STATE_HOME=" + hookStateHome } },
            when = new { always = true },
            stages = new[] { "createRuntime" },
        }), token);
        setFixtureHooks(fixtureHooks);
        foreach (string name in new[] { "gateway", "workspaces" }) Directory.CreateDirectory(Path.Combine(runtime, name));
        GatewayConnectCertificateMaterial certificates = GatewayConnectCertificateMaterial.Ensure(Path.Combine(runtime, "connect-certificates"));
        PodmanCommandResult load = await podman.RunAsync(["load", "--input", Path.Combine(images, "gateway.tar")], 120, token);
        Assert.True(load.ExitCode == 0, load.StdErr);
        PodmanCommandResult network = await podman.RunAsync(["network", "create", "--internal", "--disable-dns", "--subnet", "10.247.0.0/16", EgressTopologyManager.WorkloadNetworkName], 30, token);
        Assert.True(network.ExitCode == 0, network.StdErr);
        await using var topology = new EgressTopologyManager(podman, new WorkloadAddressAllocator(Path.Combine(runtime, "ip.json")),
            new WorkloadPostStartVerifier(podman), new WorkloadRegistry(), runtime, hookStateDirectory: hookState,
            gatewayConnectEnvoyDirectory: certificates.EnvoyDirectory);
        topology.WriteInfraAllowlist([]);
        typeof(EgressTopologyManager).GetProperty(nameof(EgressTopologyManager.IsStartupGateOpen))!.SetValue(topology, true);
        int imageInspections = 0;
        var gatewayPodman = new PodmanCommandRunner((args, cancellation) =>
        {
            if (args.Count >= 2 && args[0] == "image" && args[1] == "inspect") imageInspections++;
            return podman.RunAsync(args, 30, cancellation);
        });
        using var gateway = new ApplianceServiceSupervisor(gatewayPodman, release, version, runtime, sockets,
            NullLogger<ApplianceServiceSupervisor>.Instance, topology, certificates.GatewayDirectory);
        try
        {
            string id = await gateway.EnsureRunningAsync("gateway", token);
            Assert.Equal(id, await gateway.EnsureRunningAsync("gateway", token));
            Assert.Equal(1, imageInspections);
            PodmanCommandResult inspected = await podman.RunAsync(["container", "inspect", id], 15, token);
            Assert.Equal(0, inspected.ExitCode);
            AssertGatewayMounts(inspected.StdOut, runtime);
            string socket = Path.Combine(sockets, "gateway", "gateway.sock");
            bool ready = false;
            for (int attempt = 0; attempt < 40 && !ready; attempt++)
            {
                ready = await ApplianceServiceReadiness.CheckGatewayAsync(socket, token);
                if (!ready) await Task.Delay(TimeSpan.FromMilliseconds(250), token);
            }
            Assert.True(ready, "Gateway did not answer its health probe.");
            Assert.Equal(0, (await podman.RunAsync(["stop", id], 30, token)).ExitCode);
            Assert.True(await ApplianceServiceReadiness.CheckAsync(Path.Combine(sockets, "credentialshost", "credentialshost.sock"),
                Path.Combine(runtime, "credentialsHost", "transport", ".ca.cer"),
                Path.Combine(runtime, "credentialsHost", "transport", ".credentials-client.pfx"), token));
            Assert.True(await ApplianceServiceReadiness.CheckAsync(Path.Combine(sockets, "channelhost", "channelhost.sock"),
                Path.Combine(runtime, "security", "channel", "gateway-trust", ChannelSecurityMaterialProvisioner.CaCertificate),
                Path.Combine(runtime, "security", "channel", "gateway-private", ChannelSecurityMaterialProvisioner.GatewayClientPfx), token));
            Assert.Equal(id, await gateway.EnsureRunningAsync("gateway", token));
            ready = false;
            for (int attempt = 0; attempt < 40 && !ready; attempt++)
            {
                ready = await ApplianceServiceReadiness.CheckGatewayAsync(socket, token);
                if (!ready) await Task.Delay(TimeSpan.FromMilliseconds(250), token);
            }
            Assert.True(ready, "Gateway did not recover after restart.");
            if (release.Deployment.Images.ContainsKey("webhost"))
                await CheckPublishedWebHostAsync(gateway, podman, images, token);
        }
        finally
        {
            await podman.RunAsync(["rm", "--force", "--ignore", "--time", "0", "kautuka-appliance-webhost"], 20, CancellationToken.None);
            await podman.RunAsync(["rm", "--force", "--ignore", "--time", "0", "kautuka-appliance-gateway"], 20, CancellationToken.None);
            await podman.RunAsync(["network", "rm", EgressTopologyManager.WorkloadNetworkName], 20, CancellationToken.None);
        }
    }

    private static async Task CheckPublishedWebHostAsync(ApplianceServiceSupervisor supervisor, PodmanCommandRunner podman,
        string images, CancellationToken token)
    {
        PodmanCommandResult load = await podman.RunAsync(["load", "--input", Path.Combine(images, "webhost.tar")], 60, token);
        Assert.True(load.ExitCode == 0, load.StdErr);
        string id = await supervisor.EnsureRunningAsync("webhost", token);
        Assert.Equal(id, await supervisor.EnsureRunningAsync("webhost", token));
        PodmanCommandResult port = await podman.RunAsync(["port", id, "20229/tcp"], 10, token);
        Assert.Equal(0, port.ExitCode);
        Assert.StartsWith("127.0.0.1:", port.StdOut, StringComparison.Ordinal);
        using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        using var client = new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = new Uri($"http://{port.StdOut.Trim()}", UriKind.Absolute),
            Timeout = TimeSpan.FromSeconds(3),
        };
        for (int attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                using HttpResponseMessage response = await client.GetAsync(new Uri("/api/auth/setup/status", UriKind.Relative), token);
                if (response.IsSuccessStatusCode)
                {
                    using JsonDocument status = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                    Assert.True(status.RootElement.GetProperty("needsSetup").GetBoolean());
                    using HttpResponseMessage page = await client.GetAsync(new Uri("/", UriKind.Relative), token);
                    Assert.True(page.IsSuccessStatusCode);
                    Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
                    return;
                }
            }
            catch (HttpRequestException) when (attempt < 39) { }
            await Task.Delay(TimeSpan.FromMilliseconds(250), token);
        }
        Assert.Fail("WebHost did not forward the browser request to Gateway.");
    }
    private static JsonDocument ReadAllowlist(string root)
        => JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "infra-allowlist.json")));
    private static string ProvisionHook(string root, string state)
    {
        string manager = Path.Combine(root, "manager-install");
        string version = Path.Combine(manager, "versions", "v1");
        Directory.CreateDirectory(Path.Combine(version, "scripts"));
        File.WriteAllBytes(Path.Combine(version, "scripts", "kautuka-oci-hook"), [0x7f, 0x45, 0x4c, 0x46]);
        string current = Path.Combine(manager, "current");
        Directory.CreateSymbolicLink(current, version);
        string hooks = Path.Combine(state, ".config", "containers", "hooks.d");
        Directory.CreateDirectory(hooks);
        File.WriteAllText(Path.Combine(hooks, "kautuka_hook.json"), JsonSerializer.Serialize(new
        {
            version = "1.0.0",
            hook = new { path = Path.Combine(current, "scripts", "kautuka-oci-hook"), timeout = 45 },
            when = new { always = true },
            stages = new[] { "createRuntime" },
        }));
        return version;
    }
    private static ChannelSecurityMaterialDirectories ProvisionTrust(string runtime, string manager)
    {
        var (server, ca) = LocalCaMtlsBootstrap.EnsureAndLoad(manager, "Manager",
            [new LocalMtlsClientCertificate(ManagerClientIdentities.ChannelHostCommonName, ManagerClientIdentities.ChannelHostCertificateFileName),
             new LocalMtlsClientCertificate(ManagerClientIdentities.GatewayCommonName, ManagerClientIdentities.GatewayCertificateFileName)]);
        server.Dispose();
        ca.Dispose();
        string security = Path.Combine(runtime, "security", "channel");
        var roots = new ChannelSecurityMaterialDirectories(
            Path.Combine(security, "gateway-private"), Path.Combine(security, "gateway-trust"),
            Path.Combine(security, "channel-private"), Path.Combine(security, "channel-trust"),
            Path.Combine(security, "offline-authority"));
        ChannelSecurityMaterialProvisioner.Provision(roots);
        return roots;
    }
    private static string NormalizeFixtureCreateCommand(string json, string[] prefix, string fixtureHooks, string writerLock, string runtime)
    {
        System.Text.Json.Nodes.JsonNode root = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        System.Text.Json.Nodes.JsonArray command = root[0]!["Config"]!["CreateCommand"]!.AsArray();
        for (int i = 0; i < prefix.Length; i++)
        {
            Assert.Equal(prefix[i], command[1]!.GetValue<string>());
            command.RemoveAt(1);
        }
        for (int i = 0; i < command.Count; i++)
        {
            string value = command[i]!.GetValue<string>();
            if (value == fixtureHooks) command[i] = Path.Combine(runtime, ".config", "containers", "hooks.d");
            else if (value == "127.0.0.1::20229") command[i] = "127.0.0.1:20229:20229";
            else if (value == $"{writerLock}:/run/kautuka-db-writer.lock:ro,z")
                command[i] = "/run/kautuka-db-writer.lock:/run/kautuka-db-writer.lock:ro,z";
        }
        return root.ToJsonString();
    }
    private static void AssertGatewayMounts(string json, string runtime)
    {
        using JsonDocument inspected = JsonDocument.Parse(json);
        string[] sources = inspected.RootElement[0].GetProperty("Mounts").EnumerateArray()
            .Select(mount => mount.GetProperty("Source").GetString()!).ToArray();
        Assert.Contains(Path.Combine(runtime, "manager", ".ca.cer"), sources);
        Assert.Contains(Path.Combine(runtime, "credentialsHost", "transport", ".ca.cer"), sources);
        Assert.DoesNotContain(Path.Combine(runtime, "manager"), sources);
        Assert.DoesNotContain(Path.Combine(runtime, "credentialsHost"), sources);
        Assert.DoesNotContain(Path.Combine(runtime, "security", "channel", "channel-private"), sources);
        Assert.DoesNotContain(Path.Combine(runtime, "security", "channel", "offline-authority"), sources);
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
