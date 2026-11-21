using System.Reflection;
using System.Text.Json;

using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.NetworkAccessController;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using ManagerSandboxRuntimeDefinition = Kautuka.Manager.RuntimeContainer.Configuration.SandboxRuntimeDefinition;
using ManagerWorkspaceDefinition = Kautuka.Manager.RuntimeContainer.Configuration.WorkspaceDefinition;

namespace Kautuka.IntegrationTests;

public sealed class WorkspaceSidecarSupervisorReadinessTests
{
    [Fact]
    public void PackageProxyRunsOnWorkloadNetworkWithFixedIdentityAndAddress()
    {
        WorkspaceSidecarDefinition sidecar = new(
            Name: "kautuka-pkgs-proxy",
            Image: "localhost/kautuka-pkgs-proxy:local",
            Ports: [],
            Environment: new Dictionary<string, string>(),
            Command: [],
            BuildContext: null,
            Dockerfile: null,
            HealthCheck: null,
            RequiredForReady: false);

        List<string> arguments = InvokeBuildRunArguments(sidecar);

        int networkIndex = arguments.IndexOf("--network");
        Assert.True(networkIndex >= 0);
        Assert.Equal("kautuka-workload:ip=10.247.0.3", arguments[networkIndex + 1]);

        int aliasIndex = arguments.IndexOf("--network-alias");
        Assert.True(aliasIndex >= 0);
        Assert.Equal("kautuka-pkgs-proxy", arguments[aliasIndex + 1]);
        Assert.Contains("io.kautuka.role=package-proxy", arguments);
        Assert.Contains("io.kautuka.user=system", arguments);
        Assert.Contains("io.kautuka.agent=package-proxy", arguments);
        Assert.Contains("io.kautuka.egress=normal", arguments);
        int dnsIndex = arguments.IndexOf("--dns");
        Assert.True(dnsIndex >= 0);
        Assert.Equal(EgressTopologyManager.EgressPodIp, arguments[dnsIndex + 1]);
        Assert.Contains("--health-cmd", arguments);
        Assert.Contains("wget -q -O /dev/null http://127.0.0.1:20101/health || exit 1", arguments);
    }

    [Fact]
    public async Task PackageProxyReadinessExecutesAndRequiresImageHealthcheckAsync()
    {
        bool healthcheckInvoked = false;
        int inspectCount = 0;
        var podman = new PodmanCommandRunner((args, _) =>
        {
            if (args.Count > 0 && args[0] == "inspect")
            {
                inspectCount++;
                string healthStatus = inspectCount == 1 ? string.Empty : "healthy";
                string inspect = System.Text.Json.JsonSerializer.Serialize(new
                {
                    HostConfig = new { Dns = new[] { EgressTopologyManager.EgressPodIp } },
                    Config = new { },
                    State = new { Status = "running", Health = new { Status = healthStatus } }
                });
                return Task.FromResult(new PodmanCommandResult(0, inspect, string.Empty));
            }

            if (args.SequenceEqual(["healthcheck", "run", "kautuka-pkgs-proxy"]))
            {
                healthcheckInvoked = true;
                return Task.FromResult(new PodmanCommandResult(0, "healthy", string.Empty));
            }

            throw new InvalidOperationException("Unexpected Podman command: " + string.Join(' ', args));
        });
        WorkspaceSidecarSupervisor supervisor = CreateSupervisor(new WorkspaceSidecarDefinition(
            Name: "kautuka-pkgs-proxy",
            Image: "localhost/kautuka-pkgs-proxy:local",
            Ports: [],
            Environment: new Dictionary<string, string>(),
            Command: [],
            BuildContext: null,
            Dockerfile: null,
            HealthCheck: null,
            RequiredForReady: true), podman);
        MethodInfo isHealthy = typeof(WorkspaceSidecarSupervisor)
            .GetMethod("IsHealthyAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("IsHealthyAsync method was not found.");

        Task<bool> pending = Assert.IsType<Task<bool>>(isHealthy.Invoke(supervisor, [
            new WorkspaceSidecarDefinition(
                Name: "kautuka-pkgs-proxy",
                Image: "localhost/kautuka-pkgs-proxy:local",
                Ports: [],
                Environment: new Dictionary<string, string>(),
                Command: [],
                BuildContext: null,
                Dockerfile: null,
                HealthCheck: null,
                RequiredForReady: true),
            TestContext.Current.CancellationToken]));

        Assert.True(await pending.ConfigureAwait(true));
        Assert.True(healthcheckInvoked);
        Assert.Equal(2, inspectCount);
    }

    [Fact]
    public async Task WarmPackageProxyUsesScheduledHealthAndOneImageLookupButStillVerifiesRulesAsync()
    {
        const string name = "kautuka-pkgs-proxy";
        const string image = "localhost/kautuka-pkgs-proxy:local";
        string containerId = new('b', 64);
        string imageId = "sha256:" + new string('a', 64);
        string inspect = JsonSerializer.Serialize(new
        {
            Id = containerId,
            Image = imageId,
            HostConfig = new { NetworkMode = "bridge", Dns = new[] { EgressTopologyManager.EgressPodIp } },
            Config = new
            {
                Image = image,
                Annotations = new Dictionary<string, string> { ["io.kautuka.role"] = "package-proxy", ["io.kautuka.image-id"] = imageId },
                Labels = new Dictionary<string, string>
                {
                    ["io.kautuka.user"] = "system",
                    ["io.kautuka.agent"] = "package-proxy",
                    ["io.kautuka.kind"] = "agent-sandbox",
                    ["io.kautuka.exec-context"] = "agent-sandbox",
                    ["io.kautuka.egress"] = "normal",
                },
            },
            State = new { Status = "running", Health = new { Status = "healthy" } },
            NetworkSettings = new
            {
                Networks = new Dictionary<string, object>
                {
                    [EgressTopologyManager.WorkloadNetworkName] = new { IPAddress = WorkloadRulesetRenderer.PackageProxyIp },
                },
            },
        });
        var calls = new List<string>();
        var podman = new PodmanCommandRunner((args, _) =>
        {
            calls.Add(string.Join(' ', args.Take(2)));
            return Task.FromResult(args[0] switch
            {
                "inspect" when args[1] == name => new PodmanCommandResult(0, inspect, ""),
                "image" when args[1] == "inspect" => new PodmanCommandResult(0, imageId, ""),
                "inspect" when args[1] == containerId => new PodmanCommandResult(1, "", "fixture ruleset inspection unavailable"),
                "rm" => new PodmanCommandResult(0, "", ""),
                _ => throw new InvalidOperationException("Unexpected Podman command: " + string.Join(' ', args)),
            });
        });
        DirectoryInfo temp = Directory.CreateTempSubdirectory("kautuka-warm-proxy-");
        try
        {
            await using var topology = new EgressTopologyManager(podman, new WorkloadAddressAllocator(Path.Combine(temp.FullName, "ip.json")),
                new WorkloadPostStartVerifier(podman), new WorkloadRegistry(), temp.FullName, hookStateDirectory: temp.FullName);
            WorkspaceSidecarSupervisor supervisor = CreateSupervisor(new WorkspaceSidecarDefinition(name, image, [],
                new Dictionary<string, string>(), [], null, null, null, RequiredForReady: true, ManagerInfrastructure: true), podman, topology);
            Assert.False(await supervisor.EnsureReadyAsync(CreateRuntimeSettings(), TestContext.Current.CancellationToken));
            Assert.Equal(new[] { $"inspect {name}", "image inspect", $"inspect {containerId}", "rm -f" }, calls);
        }
        finally { temp.Delete(recursive: true); }
    }

    [Fact]
    public async Task PackageProxyMachineAllowlistSyncCompletesBeforePodmanStartAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "kautuka-proxy-machine-sync-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        const string containerId = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        string imageId = "sha256:" + new string('a', 64);
        var events = new List<string>();
        var registry = new WorkloadRegistry();
        var podman = new PodmanCommandRunner(async (args, cancellationToken) =>
        {
            if (args.Count >= 2 && args[0] == "image" && args[1] == "inspect")
            {
                return new PodmanCommandResult(0, imageId, string.Empty);
            }

            if (args.Count > 0 && args[0] == "create")
            {
                return new PodmanCommandResult(0, containerId, string.Empty);
            }

            if (args.Count >= 2 && args[0] == "inspect")
            {
                var inspect = new
                {
                    Id = containerId,
                    Image = imageId,
                    HostConfig = new { Dns = new[] { EgressTopologyManager.EgressPodIp } },
                    Config = new
                    {
                        Annotations = new Dictionary<string, string>
                        {
                            ["io.kautuka.role"] = "package-proxy",
                            ["io.kautuka.image-id"] = imageId
                        },
                        Labels = new Dictionary<string, string>
                        {
                            ["io.kautuka.user"] = "system",
                            ["io.kautuka.agent"] = "package-proxy",
                            ["io.kautuka.kind"] = "agent-sandbox",
                            ["io.kautuka.exec-context"] = "agent-sandbox",
                            ["io.kautuka.egress"] = "normal"
                        }
                    },
                    State = new { Running = true, Status = "running" },
                    NetworkSettings = new
                    {
                        Networks = new Dictionary<string, object>
                        {
                            [EgressTopologyManager.WorkloadNetworkName] = new { IPAddress = WorkloadRulesetRenderer.PackageProxyIp }
                        }
                    }
                };
                return new PodmanCommandResult(0, JsonSerializer.Serialize(new[] { inspect }), string.Empty);
            }

            if (args.Count > 1 && args[0] == "machine" && args[1] == "cp")
            {
                events.Add("machine-copy");
                using JsonDocument allowlist = JsonDocument.Parse(await File.ReadAllTextAsync(args[2], cancellationToken));
                JsonElement entry = allowlist.RootElement.GetProperty("entries")[0];
                Assert.Equal(containerId, entry.GetProperty("id").GetString());
                Assert.Equal("pkgs-proxy", entry.GetProperty("role").GetString());
                Assert.Equal(imageId, entry.GetProperty("image_digest").GetString());
                return new PodmanCommandResult(0, string.Empty, string.Empty);
            }

            if (args.Count > 1 && args[0] == "machine" && args[1] == "ssh")
            {
                events.Add("machine-chmod");
                return new PodmanCommandResult(0, string.Empty, string.Empty);
            }

            if (args.Count > 0 && args[0] == "start")
            {
                events.Add("start");
                return new PodmanCommandResult(0, containerId, string.Empty);
            }

            throw new InvalidOperationException("Unexpected Podman command: " + string.Join(' ', args));
        });
        await using var egress = new EgressTopologyManager(
            podman,
            new WorkloadAddressAllocator(Path.Combine(root, "ip.json")),
            new WorkloadPostStartVerifier(podman),
            registry,
            root,
            windowsPodmanMachineName: "acceptance-machine",
            windowsPodmanHookStateDirectory: Path.Combine(root, "hook-state"));
        WorkspaceSidecarDefinition sidecar = new(
            Name: "kautuka-pkgs-proxy",
            Image: "localhost/kautuka-pkgs-proxy:local",
            Ports: [],
            Environment: new Dictionary<string, string>(),
            Command: [],
            BuildContext: null,
            Dockerfile: null,
            HealthCheck: null,
            RequiredForReady: true);
        WorkspaceSidecarSupervisor supervisor = CreateSupervisor(sidecar, podman, egress);

        try
        {
            Assert.True(await supervisor.CreateAuthorizeAndStartPackageProxyAsync(sidecar, TestContext.Current.CancellationToken).ConfigureAwait(true));
            Assert.Equal(["machine-copy", "machine-chmod", "start"], events);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PackageProxyIdentityVerificationDetectsDnsDrift()
    {
        string imageId = "sha256:" + new string('a', 64);
        bool Verify(string dnsAddress)
        {
            var inspect = new
            {
                Image = imageId,
                HostConfig = new { Dns = new[] { dnsAddress } },
                Config = new
                {
                    Annotations = new Dictionary<string, string>
                    {
                        ["io.kautuka.role"] = "package-proxy",
                        ["io.kautuka.image-id"] = imageId
                    },
                    Labels = new Dictionary<string, string>
                    {
                        ["io.kautuka.user"] = "system",
                        ["io.kautuka.agent"] = "package-proxy",
                        ["io.kautuka.kind"] = "agent-sandbox",
                        ["io.kautuka.exec-context"] = "agent-sandbox",
                        ["io.kautuka.egress"] = "normal"
                    }
                },
                NetworkSettings = new
                {
                    Networks = new Dictionary<string, object>
                    {
                        [EgressTopologyManager.WorkloadNetworkName] = new { IPAddress = WorkloadRulesetRenderer.PackageProxyIp }
                    }
                }
            };
            MethodInfo verify = typeof(WorkspaceSidecarSupervisor)
                .GetMethod("IsPackageProxyInspectAuthorized", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("IsPackageProxyInspectAuthorized method was not found.");
            return (bool)verify.Invoke(null, [JsonSerializer.Serialize(new[] { inspect }), imageId, true])!;
        }

        Assert.True(Verify(EgressTopologyManager.EgressPodIp));
        Assert.False(Verify("10.255.255.254"));
    }

    [Fact]
    public void BuildRunArgumentsPublishesOptionalHostBindAddress()
    {
        WorkspaceSidecarDefinition sidecar = new(
            Name: "kautuka-openobserve",
            Image: "public.ecr.aws/zinclabs/openobserve:latest",
            Ports:
            [
                new WorkspaceSidecarPortBinding(20215, 5080, "127.0.0.1")
            ],
            Environment: new Dictionary<string, string>(),
            Command: [],
            BuildContext: null,
            Dockerfile: null,
            HealthCheck: null,
            RequiredForReady: false);

        List<string> arguments = InvokeBuildRunArguments(sidecar);

        int publishIndex = arguments.IndexOf("-p");
        Assert.True(publishIndex >= 0);
        Assert.Equal("127.0.0.1:20215:5080", arguments[publishIndex + 1]);
        int networkIndex = arguments.IndexOf("--network");
        Assert.True(networkIndex >= 0);
        Assert.Equal("kautuka-telemetry", arguments[networkIndex + 1]);
    }

    [Theory]
    [InlineData("kautuka-pkgs-proxy", ":U,Z")]
    [InlineData("kautuka-openobserve", ":Z")]
    public void BuildRunArgumentsRelabelsSidecarDataForSELinux(string name, string writableSuffix)
    {
        WorkspaceSidecarDefinition sidecar = new(
            Name: name,
            Image: "localhost/kautuka-pkgs-proxy:local",
            Ports: [],
            Volumes:
            [
                new WorkspaceSidecarVolumeBinding("/var/lib/kautuka/proxy/pkgs/data", "/data"),
                new WorkspaceSidecarVolumeBinding("/var/lib/kautuka/proxy/pkgs/conf", "/conf", ReadOnly: true)
            ],
            Environment: new Dictionary<string, string>(),
            Command: [],
            BuildContext: null,
            Dockerfile: null,
            HealthCheck: null,
            RequiredForReady: false);

        List<string> arguments = InvokeBuildRunArguments(sidecar);

        List<string> volumeSpecs = [];
        for (int i = 0; i < arguments.Count - 1; i++)
        {
            if (arguments[i] == "-v")
            {
                volumeSpecs.Add(arguments[i + 1]);
            }
        }

        Assert.Contains("/var/lib/kautuka/proxy/pkgs/data:/data" + writableSuffix, volumeSpecs);
        Assert.Contains("/var/lib/kautuka/proxy/pkgs/conf:/conf:ro,Z", volumeSpecs);
    }

    [Fact]
    public async Task EnsureReadyAsyncReturnsTrueWhenOnlyOptionalSidecarFailsAsync()
    {
        WorkspaceSidecarSupervisor supervisor = CreateSupervisor(new WorkspaceSidecarDefinition(
            Name: "optional-sidecar",
            Image: "docker.io/example/optional:latest",
            Ports: [],
            Environment: new Dictionary<string, string>(),
            Command: [],
            BuildContext: null,
            Dockerfile: null,
            HealthCheck: null,
            RequiredForReady: false,
            Policy: "always"));

        bool ready = await supervisor.EnsureReadyAsync(CreateRuntimeSettings(), TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.True(ready);
    }

    [Fact]
    public async Task EnsureReadyAsyncReturnsFalseWhenRequiredSidecarFailsAsync()
    {
        string sidecarName = $"required-sidecar-{Guid.NewGuid():N}";
        WorkspaceSidecarSupervisor supervisor = CreateSupervisor(new WorkspaceSidecarDefinition(
            Name: sidecarName,
            Image: "docker.io/example/required:latest",
            Ports: [],
            Environment: new Dictionary<string, string>(),
            Command: [],
            BuildContext: null,
            Dockerfile: null,
            HealthCheck: null,
            RequiredForReady: true,
            Policy: "always"));

        bool ready = await supervisor.EnsureReadyAsync(CreateRuntimeSettings(), TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(ready);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingSidecarImageIsRebuiltWhenItsPinnedBuildContextChanges(bool applianceMode)
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string root = Path.Combine(Path.GetTempPath(), "kautuka-sidecar-source-" + Guid.NewGuid().ToString("N"));
        string context = Path.Combine(root, "context");
        Directory.CreateDirectory(context);
        string dockerfile = Path.Combine(context, "Dockerfile");
        await File.WriteAllTextAsync(dockerfile, "FROM example.test/proxy@sha256:abc\n", cancellationToken).ConfigureAwait(false);
        string expectedHash = WorkspaceSidecarSupervisor.ComputeBuildContextSourceHash(context, dockerfile);
        List<string>? buildArguments = null;
        var podman = new PodmanCommandRunner((args, _) =>
        {
            if (args.Count >= 2 && args[0] == "image" && args[1] == "exists")
            {
                return Task.FromResult(new PodmanCommandResult(0, string.Empty, string.Empty));
            }

            if (args.Count >= 2 && args[0] == "image" && args[1] == "inspect")
            {
                return Task.FromResult(new PodmanCommandResult(0, "old-source-hash", string.Empty));
            }

            if (args.Count > 0 && args[0] == "build")
            {
                buildArguments = [.. args];
                return Task.FromResult(new PodmanCommandResult(0, string.Empty, string.Empty));
            }

            throw new InvalidOperationException("Unexpected Podman command: " + string.Join(' ', args));
        });
        WorkspaceSidecarDefinition sidecar = new(
            Name: "kautuka-pkgs-proxy",
            Image: "localhost/kautuka-pkgs-proxy:local",
            Ports: [],
            Environment: new Dictionary<string, string>(),
            Command: [],
            BuildContext: context,
            Dockerfile: dockerfile,
            HealthCheck: null,
            RequiredForReady: true,
            Policy: "always");
        WorkspaceSidecarSupervisor supervisor = CreateSupervisor(sidecar, podman, applianceMode: applianceMode);

        try
        {
            MethodInfo ensureImage = typeof(WorkspaceSidecarSupervisor)
                .GetMethod("EnsureImageAvailableAsync", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("EnsureImageAvailableAsync method was not found.");
            Task<bool> pending = Assert.IsType<Task<bool>>(ensureImage.Invoke(supervisor, [sidecar, cancellationToken]));

            Assert.True(await pending.ConfigureAwait(true));
            Assert.NotNull(buildArguments);
            int labelIndex = buildArguments.IndexOf("--label");
            Assert.True(labelIndex >= 0);
            Assert.Equal($"io.kautuka.sidecar.source-hash={expectedHash}", buildArguments[labelIndex + 1]);
            Assert.Equal(applianceMode, buildArguments.Contains("--pull=never", StringComparer.Ordinal));
            Assert.Equal(applianceMode, buildArguments.Contains("--network=none", StringComparer.Ordinal));
            Assert.Equal(applianceMode, buildArguments.Contains("--format=docker", StringComparer.Ordinal));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static WorkspaceSidecarSupervisor CreateSupervisor(WorkspaceSidecarDefinition sidecar)
        => CreateSupervisor(sidecar, new PodmanCommandRunner());

    private static WorkspaceSidecarSupervisor CreateSupervisor(
        WorkspaceSidecarDefinition sidecar,
        PodmanCommandRunner podman,
        EgressTopologyManager? egressTopologyManager = null,
        bool applianceMode = false)
    {
        ServiceCollection services = new();
        services.AddHttpClient("manager.sidecars");

        using ServiceProvider provider = services.BuildServiceProvider();

        return new WorkspaceSidecarSupervisor(
            new WorkspaceSidecarCatalog([sidecar]),
            provider.GetRequiredService<IHttpClientFactory>(),
            podman,
            Options.Create(new ManagerContainerStartupOptions()),
            NullLogger<WorkspaceSidecarSupervisor>.Instance,
            egressTopologyManager: egressTopologyManager,
            applianceMode: applianceMode);
    }

    private static ManagerRuntimeSettings CreateRuntimeSettings()
        => new(
            new ManagerWorkspaceDefinition
            {
                BaseRoot = "/tmp/kautuka-workspaces",
            },
            new ManagerSandboxRuntimeDefinition
            {
                Image = "kautuka-base:latest",
                DefaultTimeout = 120,
            },
            new WorkspaceTelemetryDefinition());

    private static List<string> InvokeBuildRunArguments(WorkspaceSidecarDefinition sidecar)
    {
        MethodInfo method = typeof(WorkspaceSidecarSupervisor)
            .GetMethod("BuildRunArguments", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("BuildRunArguments method was not found.");

        object? result = method.Invoke(null, [sidecar]);
        return Assert.IsType<List<string>>(result);
    }
}
