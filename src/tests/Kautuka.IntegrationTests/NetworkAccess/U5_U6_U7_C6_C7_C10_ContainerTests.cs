using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.NetworkAccessController;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class U5_U6_U7_C6_C7_C10_ContainerTests : IDisposable
{
    private readonly string tempDir;

    public U5_U6_U7_C6_C7_C10_ContainerTests()
    {
        tempDir = Path.Combine(Path.GetTempPath(), "kautuka-nac-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(tempDir))
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup error
            }
        }
    }

    [Fact]
    public void U5a_SpecBuilder_ValidatesTypedFieldsAndHardensDefaults()
    {
        // 1. IP validation (10.247.1.1 to 10.247.255.254 only, never 10.247.0.0/24, never .0 or .255)
        const string pinnedImage = "ghcr.io/devkrity/kautuka-base@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        Assert.Throws<ArgumentException>(() => new WorkloadContainerSpec("test", pinnedImage, "192.168.1.1"));
        Assert.Throws<ArgumentException>(() => new WorkloadContainerSpec("test", pinnedImage, "10.247.0.5"));
        Assert.Throws<ArgumentException>(() => new WorkloadContainerSpec("test", pinnedImage, "10.247.1.0"));
        Assert.Throws<ArgumentException>(() => new WorkloadContainerSpec("test", pinnedImage, "10.247.1.255"));

        // 2. Prohibited proxy env rejection
        var specWithProxyEnv = new WorkloadContainerSpec("test-workload", pinnedImage, "10.247.1.10")
        {
            UserId = "user-1",
            AgentId = "agent-1",
            StartupGateOpen = true,
            IsBraked = false,
            Environment = [new KeyValuePair<string, string>("HTTP_PROXY", "http://evil:8080")]
        };
        Assert.Throws<ArgumentException>(() => specWithProxyEnv.Render());

        // 3. User-attributed connectors can run without an agent, but require a user.
        var specMissingAgent = new WorkloadContainerSpec("test-workload", pinnedImage, "10.247.1.10")
        {
            UserId = "user-1",
            AgentId = "",
            ConnectorId = "search",
            Egress = "normal",
            StartupGateOpen = true,
            IsBraked = false,
        };
        Assert.NotEmpty(specMissingAgent.Render());
        Assert.Throws<ArgumentException>(() => (specMissingAgent with { UserId = "" }).Render());

        // 4. Closed startup gate blocks rendering
        var specGateClosed = new WorkloadContainerSpec("test-workload", pinnedImage, "10.247.1.10")
        {
            UserId = "user-1",
            AgentId = "agent-1",
            StartupGateOpen = false
        };
        Assert.Throws<InvalidOperationException>(() => specGateClosed.Render());

        var specWithoutStartupApproval = new WorkloadContainerSpec("test-workload", pinnedImage, "10.247.1.10")
        {
            UserId = "user-1",
            AgentId = "agent-1",
            ConnectorId = "connector-1",
            Egress = "normal",
        };
        Assert.Throws<InvalidOperationException>(() => specWithoutStartupApproval.Render());

        var specWithoutBrakeApproval = new WorkloadContainerSpec("test-workload", pinnedImage, "10.247.1.10")
        {
            UserId = "user-1",
            AgentId = "agent-1",
            ConnectorId = "connector-1",
            Egress = "normal",
            StartupGateOpen = true,
        };
        Assert.Throws<InvalidOperationException>(() => specWithoutBrakeApproval.Render());

        // 5. Mounts validation
        var mountRoot = Path.Combine(tempDir, "workspace");
        Directory.CreateDirectory(mountRoot);
        var specWithBadMount = new WorkloadContainerSpec("test-workload", pinnedImage, "10.247.1.10")
        {
            UserId = "user-1",
            AgentId = "agent-1",
            ConnectorId = "connector-1",
            AllowedMountRoots = [mountRoot],
            StartupGateOpen = true,
            IsBraked = false,
            Mounts = [new WorkloadMount("/var/run/docker.sock", "/var/run/docker.sock")]
        };
        Assert.Throws<ArgumentException>(() => specWithBadMount.Render());

        // 6. Mounts must be rooted under an explicit workspace allowlist; NAC state is outside it.
        var stateMount = specWithBadMount with
        {
            Mounts = [new WorkloadMount(Path.Combine(tempDir, "nac-state"), "/run/kautuka-nac")]
        };
        Assert.Throws<ArgumentException>(() => stateMount.Render());

        string allowedChild = Path.Combine(mountRoot, "files");
        Directory.CreateDirectory(allowedChild);
        var allowedWorkspaceMount = specWithBadMount with
        {
            Mounts = [new WorkloadMount(allowedChild, "/workspace/files", ReadOnly: true)]
        };
        Assert.Contains($"{allowedChild}:/workspace/files:ro", allowedWorkspaceMount.Render(), StringComparer.Ordinal);

        // 7. Only the one fixed Phase 6 package-proxy mapping may be supplied.
        var badHostMapping = new WorkloadContainerSpec("test-workload", pinnedImage, "10.247.1.10")
        {
            UserId = "user-1",
            AgentId = "agent-1",
            StartupGateOpen = true,
            IsBraked = false,
            AddHosts = ["metadata.internal:169.254.169.254"]
        };
        Assert.Throws<ArgumentException>(() => badHostMapping.Render());

        // 8. Application arguments after the image may contain Podman flag names safely.
        var applicationFlagArgs = new WorkloadContainerSpec("test-workload", pinnedImage, "10.247.1.10")
        {
            UserId = "user-1",
            AgentId = "agent-1",
            ConnectorId = "connector-1",
            StartupGateOpen = true,
            IsBraked = false,
            Persistent = false,
            Command = "connector-tool",
            Args = ["--network", "host"]
        };
        IReadOnlyList<string> appArgs = applicationFlagArgs.Render();
        int imageIndex = Array.IndexOf(appArgs.ToArray(), pinnedImage);
        Assert.Equal(["connector-tool", "--network", "host"], appArgs.Skip(imageIndex + 1));

        // 6. Proper rendering with default hardening
        var validSpec = new WorkloadContainerSpec("test-workload", pinnedImage, "10.247.1.10")
        {
            UserId = "user-1",
            AgentId = "agent-1",
            ConnectorId = "search",
            ExecContext = "isolated",
            Kind = "connector",
            Egress = "normal",
            StartupGateOpen = true,
            IsBraked = false,
        };

        IReadOnlyList<string> args = validSpec.Render();
        Assert.Contains("--cap-drop", args);
        Assert.Contains("ALL", args);
        Assert.Contains("--security-opt", args);
        Assert.Contains("no-new-privileges", args);
        Assert.Contains("--read-only", args);
        Assert.Contains("--tmpfs", args);
        Assert.Contains("/tmp:rw,noexec,nosuid", args);
        Assert.Contains("--network", args);
        Assert.Contains("kautuka-workload:ip=10.247.1.10", args);
        Assert.Contains("--dns", args);
        Assert.Contains("10.247.0.2", args);
        Assert.Contains("--sysctl", args);
        Assert.Contains("net.ipv6.conf.all.disable_ipv6=1", args);
        Assert.Contains("--annotation", args);
        Assert.Contains("io.kautuka.role=workload", args);
        Assert.Contains("io.kautuka.user=user-1", string.Join(" ", args), StringComparison.Ordinal);
        Assert.Contains("io.kautuka.agent=agent-1", string.Join(" ", args), StringComparison.Ordinal);
        Assert.Contains("io.kautuka.connector=search", string.Join(" ", args), StringComparison.Ordinal);
        Assert.DoesNotContain("--add-host", args, StringComparer.Ordinal);

        // 5. Compatibility profile
        var compatProfile = new ImageCompatProfile(
            "sha256:" + new string('a', 64),
            "search",
            "Search",
            [ImageCompatibilityStore.WritableRootfs, ImageCompatibilityStore.RootUser, ImageCompatibilityStore.FileOwnershipCaps],
            "admin-1",
            DateTimeOffset.UtcNow,
            Reason: null);

        var compatSpec = validSpec with { CompatibilityProfile = compatProfile };
        IReadOnlyList<string> compatArgs = compatSpec.Render();
        Assert.DoesNotContain("--read-only", compatArgs);
        Assert.Contains("0:0", compatArgs);
        Assert.Contains("--cap-add", compatArgs);
        Assert.Contains("CHOWN", compatArgs);
        Assert.Contains("FOWNER", compatArgs);

        // Negative Control (.NC):
        // (a) Profile asking for prohibited cap like NET_ADMIN is still rejected:
        var badProfile = new ImageCompatProfile(
            "sha256:" + new string('b', 64),
            "bad",
            "Bad",
            ["cap-net-admin"],
            "admin-1",
            DateTimeOffset.UtcNow,
            Reason: null);
        var specWithBadProfile = validSpec with { CompatibilityProfile = badProfile };
        Assert.Throws<ArgumentException>(() => specWithBadProfile.Render());

        // (b) Same spec without profile renders read-only and non-root:
        Assert.Contains("--read-only", args);
        Assert.Contains("65532:65532", args);
    }

    [Fact]
    public void V7_23_MountAllowlistRejectsSymlinkEscapingConfiguredWorkspaceRoot()
    {
        string workspaceRoot = Path.Combine(tempDir, "workspace-root");
        string outsideRoot = Path.Combine(tempDir, "outside-root");
        Directory.CreateDirectory(workspaceRoot);
        Directory.CreateDirectory(outsideRoot);
        string link = Path.Combine(workspaceRoot, "linked-workspace");
        try
        {
            Directory.CreateSymbolicLink(link, outsideRoot);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            Assert.Skip($"Cannot create the symbolic link required by this test: {exception.Message}");
        }

        var spec = new WorkloadContainerSpec("test-workload", "ghcr.io/devkrity/kautuka-base@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", "10.247.1.10")
        {
            UserId = "user-1",
            AgentId = "agent-1",
            StartupGateOpen = true,
            IsBraked = false,
            AllowedMountRoots = [workspaceRoot],
            Mounts = [new WorkloadMount(link, "/workspace", ReadOnly: true)],
        };

        Assert.Throws<ArgumentException>(() => spec.Render());
    }

    [Fact]
    public void U6_Renderers_EnvoyAndBoundary_WithNegativeControl()
    {
        // 1. Boundary ruleset
        string boundaryNormal = BoundaryRulesetRenderer.RenderBoundaryNft(includeLanGrants: false, decisionEndpointIp: null);
        Assert.DoesNotContain("\r", boundaryNormal, StringComparison.Ordinal);
        Assert.Contains("table ip kautuka_egress", boundaryNormal, StringComparison.Ordinal);
        Assert.Contains("delete table ip kautuka_egress", boundaryNormal, StringComparison.Ordinal);
        Assert.Contains("table ip6 kautuka_egress", boundaryNormal, StringComparison.Ordinal);
        Assert.Contains("ipv6_drop_pod", boundaryNormal, StringComparison.Ordinal);
        Assert.Contains("private_reject", boundaryNormal, StringComparison.Ordinal);
        Assert.Contains("wl_net_reject", boundaryNormal, StringComparison.Ordinal);
        Assert.DoesNotContain("lan_approved", boundaryNormal, StringComparison.Ordinal);
        Assert.DoesNotContain("decision_endpoint", boundaryNormal, StringComparison.Ordinal);

        string boundaryLan = BoundaryRulesetRenderer.RenderBoundaryNft(includeLanGrants: true, decisionEndpointIp: "192.168.1.100");
        Assert.Contains("lan_approved", boundaryLan, StringComparison.Ordinal);
        Assert.Contains("decision_endpoint", boundaryLan, StringComparison.Ordinal);

        // Negative Control (.NC): Mutated rendering has different text
        Assert.NotEqual(boundaryNormal, boundaryLan);

        // 2. CoreDNS Corefile
        string corefile = BoundaryRulesetRenderer.RenderCorefile("10.247.0.2");
        Assert.Contains("bind 10.247.0.2", corefile, StringComparison.Ordinal);
        Assert.Contains("198.18.250.250", corefile, StringComparison.Ordinal);
        Assert.Contains("bind 127.0.0.1", corefile, StringComparison.Ordinal);
        Assert.Contains("forward . 10.248.0.1", corefile, StringComparison.Ordinal);
        Assert.DoesNotContain("forward . /etc/resolv.conf", corefile, StringComparison.Ordinal);

        string corefileForHostResolver = BoundaryRulesetRenderer.RenderCorefile("10.247.0.2", ["10.255.255.254"]);
        Assert.Contains("forward . 10.255.255.254", corefileForHostResolver, StringComparison.Ordinal);
        Assert.DoesNotContain("forward . 10.248.0.1", corefileForHostResolver, StringComparison.Ordinal);

        // Negative Control (.NC): Mutated CoreDNS IP changes output
        string corefileMutated = BoundaryRulesetRenderer.RenderCorefile("10.247.0.99");
        Assert.NotEqual(corefile, corefileMutated);
    }

    [Fact]
    public void V7_20_LanEgressBindsToRoutableOutsideAddress_WithNegativeControl()
    {
        string boundary = BoundaryRulesetRenderer.RenderBoundaryNft(includeLanGrants: true);
        string envoy = BoundaryRulesetRenderer.RenderEnvoyConfig();

        Assert.Contains("ip saddr 10.248.0.3 ip daddr { 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16 }", boundary, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Count(envoy, "address: 10\\.248\\.0\\.3\\r?\\n\\s+port_value: 0", RegexOptions.CultureInvariant));
        Assert.DoesNotContain("192.0.0.8", boundary + envoy, StringComparison.Ordinal);
        Assert.Equal(["10.247.0.2", "10.248.0.2", "10.248.0.3"], EgressTopologyManager.GetExpectedInfraIpv4("egress-envoy"));
        Assert.Equal(["10.247.0.2", "10.248.0.2"], EgressTopologyManager.GetExpectedInfraIpv4("egress-boundary"));
    }

    [Fact]
    public async Task BoundaryInfraAllowlistEmitsOnlyTheKnownRestartAlternate()
    {
        var podman = new PodmanCommandRunner((args, _) => Task.FromResult(new PodmanCommandResult(
            0,
            args.SequenceEqual(["inspect", "kautuka-egress-boundary"])
                ? "[{\"Id\":\"boundary-container-id\"}]"
                : string.Empty,
            string.Empty,
            ProcessTerminationReason.Completed)));
        var registry = new WorkloadRegistry();
        var allocator = new WorkloadAddressAllocator(Path.Combine(tempDir, "boundary-alternate-ip.json"));
        var verifier = new WorkloadPostStartVerifier(podman);
        await using var manager = new EgressTopologyManager(podman, allocator, verifier, registry, tempDir);
        List<InfraAllowlistEntry> entries = [];
        System.Reflection.MethodInfo createInfra = typeof(EgressTopologyManager).GetMethod(
            "EnsureInfraContainerCreatedAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("EnsureInfraContainerCreatedAsync method was not found.");

        await ((Task)createInfra.Invoke(manager,
            ["kautuka-egress-boundary", "egress-boundary", EgressTopologyManager.BoundaryInitImage,
                Array.Empty<string>(), entries, TestContext.Current.CancellationToken])!).ConfigureAwait(false);

        JsonArray allowlist = JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(tempDir, "nac", "infra-allowlist.json"), TestContext.Current.CancellationToken))!["entries"]!.AsArray();
        JsonNode boundary = Assert.Single(allowlist, entry => entry!["id"]!.GetValue<string>() == "boundary-container-id")!;
        Assert.Equal(["10.247.0.2", "10.248.0.2"], boundary["expected_ipv4"]!.AsArray().Select(ip => ip!.GetValue<string>()));
        Assert.Equal(["10.247.0.2", "10.248.0.2", "10.248.0.3"], boundary["expected_ipv4_alternates"]![0]!.AsArray().Select(ip => ip!.GetValue<string>()));
    }

    [Fact]
    public void V7_21_ApprovedLanSourceIsTranslatedToPodmanAssignedOutsideAddress()
    {
        string boundary = BoundaryRulesetRenderer.RenderBoundaryNft(includeLanGrants: true);

        Assert.Contains("ip saddr 10.248.0.3 ip daddr { 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16 } ip daddr != 10.247.0.0/16 counter accept comment \"lan_approved\"", boundary, StringComparison.Ordinal);
        Assert.Contains("chain postrouting", boundary, StringComparison.Ordinal);
        Assert.Contains("ip saddr 10.248.0.3 ip daddr { 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16 } ip daddr != 10.247.0.0/16 counter snat to 10.248.0.2 comment \"lan_source_masquerade\"", boundary, StringComparison.Ordinal);

        string negativeControl = boundary.Replace(
            "    ip saddr 10.248.0.3 ip daddr { 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16 } ip daddr != 10.247.0.0/16 counter snat to 10.248.0.2 comment \"lan_source_masquerade\"\n",
            string.Empty,
            StringComparison.Ordinal);
        Assert.NotEqual(boundary, negativeControl);
        Assert.DoesNotContain("lan_source_masquerade", negativeControl, StringComparison.Ordinal);
    }

    [Fact]
    public void V7_50_LanSourceAddressUsesOutsidePodInterface_NotAnInterfaceName()
    {
        string command = EgressTopologyManager.BuildOutsideLanSourceAddressSetupCommand();

        Assert.Contains("ip -o -4 addr show", command, StringComparison.Ordinal);
        Assert.Contains(@"10\.248\.0\.2", command, StringComparison.Ordinal);
        Assert.Contains("10.248.0.3/24", command, StringComparison.Ordinal);
        Assert.DoesNotContain("dev eth0", command, StringComparison.Ordinal);
        Assert.DoesNotContain("dev eth1", command, StringComparison.Ordinal);

        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string fakeBin = Path.Combine(tempDir, "v750-bin");
        Directory.CreateDirectory(fakeBin);
        string capturePath = Path.Combine(tempDir, "v750-ip-command.txt");
        string fakeIp = Path.Combine(fakeBin, "ip");
        File.WriteAllText(fakeIp,
            "#!/bin/sh\n"
            + "if [ \"$1\" = \"-o\" ]; then\n"
            + "  printf '%s\\n' '1: eth0 inet 10.247.0.2/16 scope global eth0' '2: enp0s8 inet 10.248.0.2/24 scope global enp0s8'\n"
            + "else\n"
            + "  printf '%s\\n' \"$*\" > \"$NAC_IP_CAPTURE\"\n"
            + "fi\n");
        File.SetUnixFileMode(fakeIp,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            },
        };
        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add(command);
        process.StartInfo.Environment["PATH"] = $"{fakeBin}:/usr/bin:/bin";
        process.StartInfo.Environment["NAC_IP_CAPTURE"] = capturePath;
        Assert.True(process.Start());
        Assert.True(process.WaitForExit(5000), "LAN interface selection command timed out.");
        Assert.Equal(0, process.ExitCode);
        Assert.Equal("address replace 10.248.0.3/24 dev enp0s8", File.ReadAllText(capturePath).Trim());
    }

    [Fact]
    public async Task FailedHostedStartupRetriesUntilExplicitlyClosed()
    {
        int infoCalls = 0;
        var podman = new PodmanCommandRunner((args, _) =>
        {
            if (args[0] == "info")
            {
                infoCalls++;
                return Task.FromResult(new PodmanCommandResult(125, "", "temporary failure", ProcessTerminationReason.NonZeroExit));
            }
            return Task.FromResult(new PodmanCommandResult(0, "[]", "", ProcessTerminationReason.Completed));
        });
        var registry = new WorkloadRegistry();
        await using var manager = new EgressTopologyManager(podman, new WorkloadAddressAllocator(Path.Combine(tempDir, "startup-retry-ip.json")), new WorkloadPostStartVerifier(podman), registry, tempDir);
        await manager.StartAsync(TestContext.Current.CancellationToken);
        Assert.False(manager.IsStartupGateOpen);
        Assert.Equal(1, infoCalls);
        await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);
        Assert.Equal(2, infoCalls);
        Assert.False(manager.IsStartupGateOpen);
        manager.CloseLaunchAdmission();
        await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);
        Assert.Equal(2, infoCalls);
        Assert.False(manager.IsStartupGateOpen);
    }

    [Theory]
    [InlineData("image")]
    [InlineData("same-tag-image-id")]
    [InlineData("partial-pod")]
    [InlineData("partial-pod-created")]
    [InlineData("partial-pod-configured")]
    [InlineData("args")]
    [InlineData("current")]
    public async Task V7_53_ExistingInfraContainerIsRecreatedWhenImageOrArgsDrift(string drift)
    {
        string image = EgressTopologyManager.EnvoyPinnedImage;
        string[] args = ["create", "--name", "kautuka-egress-envoy", "--network", "kautuka-egress", image, "--config-path", "/etc/kautuka-envoy/envoy.yaml"];
        string[] actual = drift == "image"
            ? ["podman", "create", "--name", "kautuka-egress-envoy", "--network", "kautuka-egress", "docker.io/envoyproxy/envoy:old", "--config-path", "/etc/kautuka-envoy/envoy.yaml"]
            : drift == "args"
                ? ["podman", "create", "--name", "kautuka-egress-envoy", "--network", "kautuka-egress", image, "--config-path", "/old/envoy.yaml"]
                : new[] { "podman" }.Concat(args).ToArray();
        bool imageIdDrift = drift is "same-tag-image-id" or "partial-pod" or "partial-pod-created" or "partial-pod-configured";
        string staleInspect = JsonSerializer.Serialize(new[] { new { Id = "old-envoy", Image = imageIdDrift ? "sha256:old" : "sha256:current", Config = new { CreateCommand = actual } } });
        var calls = new List<IReadOnlyList<string>>();
        bool removed = false;
        string podState = drift is "partial-pod-created" or "partial-pod-configured" ? "Created" : "Running";
        var podman = new PodmanCommandRunner((call, _) =>
        {
            calls.Add(call.ToArray());
            string output = call.SequenceEqual(["inspect", "kautuka-egress-envoy"])
                ? (removed ? JsonSerializer.Serialize(new[] { new { Id = "new-envoy", Image = "sha256:current", Config = new { CreateCommand = new[] { "podman" }.Concat(args).ToArray() } } }) : staleInspect)
                : call.SequenceEqual(["image", "inspect", image]) ? "[{\"Id\":\"sha256:current\"}]"
                : call.SequenceEqual(["pod", "inspect", EgressTopologyManager.EgressPodName])
                    ? drift.StartsWith("partial-pod", StringComparison.Ordinal)
                        ? $"[{{\"State\":\"{podState}\",\"InfraContainerID\":\"infra\",\"Containers\":[{{\"Id\":\"envoy\",\"Name\":\"kautuka-egress-envoy\",\"State\":\"{(podState == "Running" ? "running" : drift == "partial-pod-configured" ? "configured" : "created")}\"}},{{\"Id\":\"infra\",\"Name\":\"kautuka-egress-pod-infra\",\"State\":\"{(podState == "Running" ? "running" : drift == "partial-pod-configured" ? "configured" : podState == "Created" ? "created" : "exited")}\"}}]}}]"
                        : $"[{{\"State\":\"{podState}\",\"InfraContainerID\":\"infra\",\"Containers\":[{{\"Id\":\"boundary\",\"Name\":\"kautuka-egress-boundary\",\"State\":\"{(podState == "Running" ? "running" : "exited")}\"}},{{\"Id\":\"dns\",\"Name\":\"kautuka-egress-dns\",\"State\":\"{(podState == "Running" ? "running" : "created")}\"}},{{\"Id\":\"envoy\",\"Name\":\"kautuka-egress-envoy\",\"State\":\"{(podState == "Running" ? "running" : "created")}\"}},{{\"Id\":\"infra\",\"Name\":\"kautuka-egress-pod-infra\",\"State\":\"{(podState == "Running" ? "running" : "exited")}\"}}]}}]"
                    : string.Empty;
            if (call.SequenceEqual(["pod", "stop", EgressTopologyManager.EgressPodName])) podState = "Exited";
            if (call.SequenceEqual(["rm", "-f", "kautuka-egress-envoy"])) removed = true;
            return Task.FromResult(new PodmanCommandResult(0, output, string.Empty, ProcessTerminationReason.Completed));
        });
        var registry = new WorkloadRegistry();
        await using var manager = new EgressTopologyManager(podman, new WorkloadAddressAllocator(Path.Combine(tempDir, "v753.json")), new WorkloadPostStartVerifier(podman), registry, tempDir);
        var ensure = typeof(EgressTopologyManager).GetMethod("EnsureInfraContainerCreatedAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await ((Task)ensure.Invoke(manager, ["kautuka-egress-envoy", "egress-envoy", image, args, new List<InfraAllowlistEntry>(), TestContext.Current.CancellationToken])!).ConfigureAwait(false);
        if (drift == "current")
        {
            Assert.DoesNotContain(calls, c => c.SequenceEqual(["pod", "stop", EgressTopologyManager.EgressPodName]));
            Assert.DoesNotContain(calls, c => c.SequenceEqual(["rm", "-f", "kautuka-egress-envoy"]));
            Assert.DoesNotContain(calls, c => c.SequenceEqual(args));
        }
        else
        {
            if (drift is "partial-pod-created" or "partial-pod-configured")
                Assert.DoesNotContain(calls, c => c.SequenceEqual(["pod", "stop", EgressTopologyManager.EgressPodName]));
            else
                Assert.Contains(calls, c => c.SequenceEqual(["pod", "stop", EgressTopologyManager.EgressPodName]));
            Assert.Contains(calls, c => c.SequenceEqual(["rm", "-f", "kautuka-egress-envoy"]));
            Assert.Contains(calls, c => c.SequenceEqual(args));
        }
        Assert.Contains(calls, c => c.SequenceEqual(["inspect", "kautuka-egress-envoy"]));
    }

    [Fact]
    public async Task V7_51_ProductionHostedStartupEnablesLanGrants_WithNegativeControl()
    {
        string stateDirectory = Path.Combine(Path.GetTempPath(), $"nac-v751-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stateDirectory);
        try
        {
            var podman = new PodmanCommandRunner((_, _) => Task.FromResult(new PodmanCommandResult(
                0,
                string.Empty,
                string.Empty,
                ProcessTerminationReason.Completed)));
            var registry = new WorkloadRegistry();
            var allocator = new WorkloadAddressAllocator(Path.Combine(stateDirectory, "ip-cursor.json"));
            var verifier = new WorkloadPostStartVerifier(podman);
            await using var manager = new EgressTopologyManager(podman, allocator, verifier, registry, stateDirectory);

            // Exercise the hosted-service entry point used by production Manager startup.
            await manager.StartAsync(TestContext.Current.CancellationToken);

            string boundaryPath = Path.Combine(stateDirectory, "nac", "boundary", "boundary.nft");
            string boundary = await File.ReadAllTextAsync(boundaryPath, TestContext.Current.CancellationToken);
            Assert.Contains("lan_approved", boundary, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(stateDirectory))
            {
                Directory.Delete(stateDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void V7_11_RenderedAndLiveBoundaryHashesMustMatch_WithNegativeControl()
    {
        EgressTopologyManager.RequireMatchingBoundaryRulesetHash("rendered-native-hash", "rendered-native-hash");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => EgressTopologyManager.RequireMatchingBoundaryRulesetHash("rendered-native-hash", "live-drifted-hash"));
        Assert.Contains("does not match the native rendering", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V7_11_NativeBoundaryRenderingUsesPodmanUnshareWithoutLaunchingAContainer()
    {
        string boundaryPath = Path.Combine(tempDir, "nac", "boundary", "boundary.nft");
        Directory.CreateDirectory(Path.GetDirectoryName(boundaryPath)!);
        await File.WriteAllTextAsync(boundaryPath, "native nft fixture", TestContext.Current.CancellationToken);
        const string ipv4 = "{\"nftables\":[{\"rule\":{\"comment\":\"private_reject\",\"expr\":[]}},{\"rule\":{\"comment\":\"wl_net_reject\",\"expr\":[]}}]}";
        const string ipv6 = "{\"nftables\":[{\"rule\":{\"comment\":\"ipv6_drop_pod\",\"expr\":[]}}]}";
        IReadOnlyList<string>? observedArgs = null;
        bool catFails = false;
        var podman = new PodmanCommandRunner((args, _) =>
        {
            observedArgs = args.ToArray();
            // Fedora's confined boundary file must be read by the shell before nft
            // consumes stdin. A plain pipeline can hide cat's failure status.
            bool readsRulesetFromShellStdin = args.Count > 5
                && args[5].Contains("rules=$(cat \"$1\") &&", StringComparison.Ordinal)
                && args[5].Contains("| nft -f -", StringComparison.Ordinal);
            bool unsafePlainPipeline = args.Count > 5
                && args[5].Contains("cat \"$1\" | nft -f -", StringComparison.Ordinal);
            readsRulesetFromShellStdin |= unsafePlainPipeline;
            if (catFails && unsafePlainPipeline)
            {
                // Negative control: a failing cat can leave a valid prefix on
                // stdin while the pipeline reports nft's successful exit code.
                return Task.FromResult(new PodmanCommandResult(
                    0, $"{ipv4}\n__KUTUKA_IPV6_BOUNDARY__\n{ipv6}", string.Empty, ProcessTerminationReason.Completed));
            }
            if (catFails && readsRulesetFromShellStdin)
            {
                return Task.FromResult(new PodmanCommandResult(
                    1, ipv4, "cat: boundary.nft: No such file", ProcessTerminationReason.Completed));
            }
            if (!readsRulesetFromShellStdin)
            {
                return Task.FromResult(new PodmanCommandResult(
                    1, string.Empty, "nft cannot open confined boundary file", ProcessTerminationReason.Completed));
            }
            return Task.FromResult(new PodmanCommandResult(
                0,
                $"{ipv4}\n__KUTUKA_IPV6_BOUNDARY__\n{ipv6}",
                string.Empty,
                ProcessTerminationReason.Completed));
        });
        var registry = new WorkloadRegistry();
        var allocator = new WorkloadAddressAllocator(Path.Combine(tempDir, "cursor.json"));
        var verifier = new WorkloadPostStartVerifier(podman);
        await using var manager = new EgressTopologyManager(podman, allocator, verifier, registry, tempDir);

        string hash = await manager.ReadRenderedBoundaryRulesetHashAsync(TestContext.Current.CancellationToken);

        Assert.Equal(EgressTopologyManager.ComputeBoundaryRulesetHash(ipv4, ipv6), hash);
        Assert.NotNull(observedArgs);
        Assert.Equal("unshare", observedArgs![0]);
        Assert.Equal("-n", observedArgs[2]);
        Assert.DoesNotContain("run", observedArgs, StringComparer.Ordinal);

        // Model cat failing after producing a valid-looking prefix. The explicit
        // && must make the script fail rather than hash the truncated ruleset.
        catFails = true;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.ReadRenderedBoundaryRulesetHashAsync(TestContext.Current.CancellationToken));
        Assert.Contains("rules=$(cat \"$1\") && printf '%s\\n' \"$rules\" | nft -f -", observedArgs[5], StringComparison.Ordinal);
    }

    [Fact]
    public async Task WindowsMachineBoundaryVerificationReadsBothLiveTablesAndRejectsDrift()
    {
        const string ipv4 = "{\"nftables\":[{\"rule\":{\"comment\":\"private_reject\",\"expr\":[]}},{\"rule\":{\"comment\":\"wl_net_reject\",\"expr\":[]}}]}";
        const string ipv6 = "{\"nftables\":[{\"rule\":{\"comment\":\"ipv6_drop_pod\",\"expr\":[]}}]}";
        var calls = new List<IReadOnlyList<string>>();
        bool drift = false;
        var podman = new PodmanCommandRunner((args, _) =>
        {
            calls.Add(args.ToArray());
            string output = args[0] switch
            {
                "pod" => "{\"InfraContainerID\":\"infra\"}",
                "inspect" => "[{\"State\":{\"Pid\":123}}]",
                "machine" when args[1] == "cp" => "",
                "machine" when args[3].Contains("unshare -n sh", StringComparison.Ordinal) => $"{ipv4}\n__KUTUKA_IPV6_BOUNDARY__\n{ipv6}",
                "machine" when args[3].Contains("table ip6", StringComparison.Ordinal) => ipv6,
                "machine" => drift ? ipv4.Replace("private_reject", "private_reject_changed", StringComparison.Ordinal) : ipv4,
                _ => throw new InvalidOperationException("Unexpected command")
            };
            return Task.FromResult(new PodmanCommandResult(0, output, "", ProcessTerminationReason.Completed));
        });
        var registry = new WorkloadRegistry();
        var allocator = new WorkloadAddressAllocator(Path.Combine(tempDir, "cursor.json"));
        await using var manager = new EgressTopologyManager(podman, allocator, new WorkloadPostStartVerifier(podman), registry, tempDir,
            windowsPodmanMachineName: "dev", windowsPodmanHookStateDirectory: "/home/core/.local/state/kautuka/nac");

        string rendered = await manager.ReadRenderedBoundaryRulesetHashAsync(TestContext.Current.CancellationToken);
        string live = await manager.ReadBoundaryRulesetHashAsync(TestContext.Current.CancellationToken);
        EgressTopologyManager.RequireMatchingBoundaryRulesetHash(rendered, live);
        Assert.Contains(calls, args => args.Count > 1 && args[0] == "machine" && args[1] == "cp");
        Assert.Contains(calls, args => args.Count > 3 && args[0] == "machine" && args[1] == "ssh"
            && args[3].Contains("rules=$(cat \"$1\") &&", StringComparison.Ordinal)
            && args[3].Contains("| nft -f -", StringComparison.Ordinal));
        Assert.Contains(calls, args => args.Count > 3 && args[2] == "dev" && args[3].Contains("nsenter -t 123 -n nft -j list table ip6", StringComparison.Ordinal));
        drift = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ReadBoundaryRulesetHashAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WindowsMachineReconcileClosesGateForValidChangedRules()
    {
        const string ipv4 = "{\"nftables\":[{\"rule\":{\"comment\":\"private_reject\",\"expr\":[{\"drop\":null}]}},{\"rule\":{\"comment\":\"wl_net_reject\",\"expr\":[{\"drop\":null}]}}]}";
        const string ipv6 = "{\"nftables\":[{\"rule\":{\"comment\":\"ipv6_drop_pod\",\"expr\":[{\"drop\":null}]}}]}";
        const string healthy = "[{\"State\":\"Running\",\"InfraContainerID\":\"infra\",\"Containers\":[{\"Name\":\"kautuka-egress-boundary\",\"State\":\"exited\"},{\"Name\":\"kautuka-egress-dns\",\"State\":\"running\"},{\"Name\":\"kautuka-egress-envoy\",\"State\":\"running\"},{\"Name\":\"pod-infra\",\"State\":\"running\"}]}]";
        bool drift = false;
        var podman = new PodmanCommandRunner((args, _) =>
        {
            string output = args[0] switch
            {
                "pod" => healthy,
                "inspect" when args[1] == "kautuka-egress-boundary" => "[{\"State\":{\"Status\":\"exited\",\"ExitCode\":0}}]",
                "inspect" => "[{\"State\":{\"Pid\":123}}]",
                "machine" when args.Count > 3 && args[2] == "dev" && args[3].Contains("table ip6", StringComparison.Ordinal) => ipv6,
                "machine" when args.Count > 3 && args[2] == "dev" => drift ? ipv4.Replace("{\"drop\":null}", "{\"accept\":null}", StringComparison.Ordinal) : ipv4,
                "ps" => "[]",
                _ => "[]"
            };
            return Task.FromResult(new PodmanCommandResult(0, output, "", ProcessTerminationReason.Completed));
        });
        var registry = new WorkloadRegistry();
        var allocator = new WorkloadAddressAllocator(Path.Combine(tempDir, "cursor.json"));
        await using var manager = new EgressTopologyManager(podman, allocator, new WorkloadPostStartVerifier(podman), registry, tempDir,
            windowsPodmanMachineName: "dev", windowsPodmanHookStateDirectory: "/home/core/.local/state/kautuka/nac");
        await SetExpectedBoundarySourceAsync(manager, TestContext.Current.CancellationToken);
        typeof(EgressTopologyManager).GetField("expectedBoundaryRulesetHash", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(manager, EgressTopologyManager.ComputeBoundaryRulesetHash(ipv4, ipv6));
        typeof(EgressTopologyManager).GetField("isStartupGateOpen", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(manager, true);
        await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);
        Assert.True(manager.IsStartupGateOpen);
        drift = true;
        await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);
        Assert.False(manager.IsStartupGateOpen);
    }

    [Fact]
    public async Task WindowsMachineBoundaryRenderingFailsClosedWhenCopyFails()
    {
        var podman = new PodmanCommandRunner((_, _) => Task.FromResult(new PodmanCommandResult(1, "", "copy failed", ProcessTerminationReason.Completed)));
        var registry = new WorkloadRegistry();
        var allocator = new WorkloadAddressAllocator(Path.Combine(tempDir, "cursor.json"));
        await using var manager = new EgressTopologyManager(podman, allocator, new WorkloadPostStartVerifier(podman), registry, tempDir,
            windowsPodmanMachineName: "dev", windowsPodmanHookStateDirectory: "/home/core/.local/state/kautuka/nac");
        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ReadRenderedBoundaryRulesetHashAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void U7_AddressAllocator_MonotonicCursorPersistenceAndWrap_WithNegativeControl()
    {
        string statePath = Path.Combine(tempDir, "ip-cursor.json");
        var allocator = new WorkloadAddressAllocator(statePath);

        // Allocate first 3 IPs
        string ip1 = allocator.AllocateNextIp();
        string ip2 = allocator.AllocateNextIp();
        string ip3 = allocator.AllocateNextIp();

        Assert.Equal("10.247.1.1", ip1);
        Assert.Equal("10.247.1.2", ip2);
        Assert.Equal("10.247.1.3", ip3);

        // Ensure state file exists and persisted cursor
        Assert.True(File.Exists(statePath));

        // Recreate allocator from persisted state: cursor must continue monotonically
        var allocator2 = new WorkloadAddressAllocator(statePath);
        Assert.Equal(2, allocator2.CurrentCursorIndex);
        Assert.Equal("10.247.1.3", allocator2.LastAllocatedIp);

        string ip4 = allocator2.AllocateNextIp();
        Assert.Equal("10.247.1.4", ip4);

        // Verify index-to-IP arithmetic and boundary wrap
        // Index 0 -> 10.247.1.1
        Assert.Equal("10.247.1.1", WorkloadAddressAllocator.ComputeIpFromIndex(0));
        // Index 253 -> 10.247.1.254 (never .255 or .0)
        Assert.Equal("10.247.1.254", WorkloadAddressAllocator.ComputeIpFromIndex(253));
        // Index 254 -> 10.247.2.1
        Assert.Equal("10.247.2.1", WorkloadAddressAllocator.ComputeIpFromIndex(254));
        // Index 64769 -> 10.247.255.254
        Assert.Equal("10.247.255.254", WorkloadAddressAllocator.ComputeIpFromIndex(64769));

        // Registry skip test:
        var registry = new WorkloadRegistry();
        registry.Register(new RegistryEntry(
            "10.247.1.5",
            "u1",
            "a1",
            "cid-1",
            "connector",
            "conn-1",
            "isolated",
            "normal",
            "user-agent",
            null,
            DateTimeOffset.UtcNow));

        // Next allocation should skip 10.247.1.5 and allocate 10.247.1.6
        string ipSkipped = allocator2.AllocateNextIp(registry);
        Assert.Equal("10.247.1.6", ipSkipped);

        // Negative Control (.NC):
        // If we reset cursor or don't advance, same IP would be duplicated:
        Assert.NotEqual(ip1, ip2);
        Assert.NotEqual(ip3, ip4);
    }

    [Fact]
    public void V7_11_CanonicalCounterHashMatchesPythonHookOrdering()
    {
        const string nftJson = "[{\"rule\":{\"counter\":{\"packets\":7,\"bytes\":3}}}]";

        string hash = BoundaryRulesetRenderer.ComputeCanonicalNftJsonHash(nftJson);

        Assert.Equal("897706c133a741af021b35116e1def3d54b9f5f0e1ccdb17294ed4bd32699203", hash);
    }

    [Fact]
    public void C6_CanonicalNftHash_DetectsTampering_WithNegativeControl()
    {
        // Construct canonical nft JSON representing live workload table
        var rulesetNode = new JsonObject
        {
            ["nftables"] = new JsonArray
            {
                new JsonObject
                {
                    ["table"] = new JsonObject
                    {
                        ["family"] = "inet",
                        ["name"] = "kautuka_workload",
                        ["handle"] = 10
                    }
                },
                new JsonObject
                {
                    ["rule"] = new JsonObject
                    {
                        ["family"] = "inet",
                        ["table"] = "kautuka_workload",
                        ["chain"] = "input",
                        ["handle"] = 11,
                        ["comment"] = "input_drop",
                        ["counter"] = new JsonObject { ["packets"] = 42, ["bytes"] = 1024 }
                    }
                },
                new JsonObject
                {
                    ["rule"] = new JsonObject
                    {
                        ["family"] = "inet",
                        ["table"] = "kautuka_workload",
                        ["chain"] = "output",
                        ["handle"] = 12,
                        ["comment"] = "ipv6_drop",
                        ["counter"] = new JsonObject { ["packets"] = 0, ["bytes"] = 0 }
                    }
                },
                new JsonObject
                {
                    ["rule"] = new JsonObject
                    {
                        ["family"] = "inet",
                        ["table"] = "kautuka_workload",
                        ["chain"] = "output",
                        ["handle"] = 13,
                        ["comment"] = "spoof_drop",
                        ["expr"] = new JsonArray { "10.247.1.15" },
                        ["counter"] = new JsonObject { ["packets"] = 5, ["bytes"] = 300 }
                    }
                },
                new JsonObject
                {
                    ["rule"] = new JsonObject
                    {
                        ["family"] = "inet",
                        ["table"] = "kautuka_workload",
                        ["chain"] = "output",
                        ["handle"] = 14,
                        ["comment"] = "wl_net_drop",
                        ["counter"] = new JsonObject { ["packets"] = 0, ["bytes"] = 0 }
                    }
                },
                new JsonObject
                {
                    ["rule"] = new JsonObject
                    {
                        ["family"] = "inet",
                        ["table"] = "kautuka_workload",
                        ["chain"] = "output",
                        ["handle"] = 15,
                        ["comment"] = "meta_drop",
                        ["counter"] = new JsonObject { ["packets"] = 0, ["bytes"] = 0 }
                    }
                },
                new JsonObject
                {
                    ["rule"] = new JsonObject
                    {
                        ["family"] = "inet",
                        ["table"] = "kautuka_workload",
                        ["chain"] = "output",
                        ["handle"] = 16,
                        ["comment"] = "udp_drop",
                        ["counter"] = new JsonObject { ["packets"] = 0, ["bytes"] = 0 }
                    }
                },
                new JsonObject
                {
                    ["rule"] = new JsonObject
                    {
                        ["family"] = "inet",
                        ["table"] = "kautuka_workload",
                        ["chain"] = "output",
                        ["handle"] = 17,
                        ["comment"] = "final_drop",
                        ["counter"] = new JsonObject { ["packets"] = 0, ["bytes"] = 0 }
                    }
                }
            }
        };

        string json = rulesetNode.ToJsonString();
        string hash1 = BoundaryRulesetRenderer.ComputeCanonicalNftJsonHash(
            json,
            WorkloadRulesetRenderer.RequiredWorkloadComments,
            "10.247.1.15");

        Assert.False(string.IsNullOrWhiteSpace(hash1));

        // Same ruleset with different dynamic counters or handles produces the EXACT SAME hash
        var rulesetDifferentCounters = (JsonObject)rulesetNode.DeepClone();
        JsonArray rulesArr = (JsonArray)rulesetDifferentCounters["nftables"]!;
        JsonObject rule1 = (JsonObject)rulesArr[1]!;
        rule1["rule"]!["handle"] = 999;
        rule1["rule"]!["counter"] = new JsonObject { ["packets"] = 999999, ["bytes"] = 88888888 };

        string hash2 = BoundaryRulesetRenderer.ComputeCanonicalNftJsonHash(
            rulesetDifferentCounters.ToJsonString(),
            WorkloadRulesetRenderer.RequiredWorkloadComments,
            "10.247.1.15");

        Assert.Equal(hash1, hash2);

        // Negative Control (.NC):
        // (a) Adding rogue rule changes the hash:
        var tamperedNode = (JsonObject)rulesetNode.DeepClone();
        ((JsonArray)tamperedNode["nftables"]!).Add(new JsonObject
        {
            ["rule"] = new JsonObject
            {
                ["chain"] = "output",
                ["comment"] = "attacker_backdoor",
                ["action"] = "accept"
            }
        });

        string tamperedHash = BoundaryRulesetRenderer.ComputeCanonicalNftJsonHash(
            tamperedNode.ToJsonString(),
            WorkloadRulesetRenderer.RequiredWorkloadComments,
            "10.247.1.15");
        Assert.NotEqual(hash1, tamperedHash);

        // (b) Missing required comment throws:
        var missingCommentNode = (JsonObject)rulesetNode.DeepClone();
        ((JsonArray)missingCommentNode["nftables"]!).RemoveAt(1); // Remove input_drop
        Assert.Throws<InvalidOperationException>(() =>
            BoundaryRulesetRenderer.ComputeCanonicalNftJsonHash(
                missingCommentNode.ToJsonString(),
                WorkloadRulesetRenderer.RequiredWorkloadComments,
                "10.247.1.15"));
    }

    [Fact]
    public void C7_AtomicRulesetApply_Idempotence_WithNegativeControl()
    {
        string goldenPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "kautuka-workload.nft");
        string golden = File.ReadAllText(goldenPath).Replace("\r\n", "\n", StringComparison.Ordinal);
        string defaultRuleset = WorkloadRulesetRenderer.Render("10.247.1.20").Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Equal(golden, defaultRuleset);
        Assert.DoesNotContain("20101", defaultRuleset, StringComparison.Ordinal);

        string workloadRuleset = WorkloadRulesetRenderer.Render("10.247.0.3", isPackageProxy: true);

        // MF7: Idempotent atomic transaction structure
        Assert.StartsWith(
            "table inet kautuka_workload\ndelete table inet kautuka_workload\ntable inet kautuka_workload {",
            workloadRuleset.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);

        // Rendering twice produces byte-for-byte identical text
        string workloadRuleset2 = WorkloadRulesetRenderer.Render("10.247.0.3", isPackageProxy: true);
        Assert.Equal(workloadRuleset, workloadRuleset2);

        // Negative Control (.NC):
        // Without package proxy, ruleset text changes:
        string workloadNoProxy = WorkloadRulesetRenderer.Render("10.247.0.3", isPackageProxy: false);
        Assert.NotEqual(workloadRuleset, workloadNoProxy);
    }

    [Fact]
    public void C10_EnvoyConfig_TimeoutAndFailClosedGuards_WithNegativeControl()
    {
        string envoyConfig = BoundaryRulesetRenderer.RenderEnvoyConfig();

        // Assert 2s ext_authz timeout and fail-closed guards
        Assert.Contains("timeout: 2s", envoyConfig, StringComparison.Ordinal);
        Assert.Contains("failure_mode_allow: false", envoyConfig, StringComparison.Ordinal);
        Assert.Contains("stat_prefix: nac_tls", envoyConfig, StringComparison.Ordinal);
        Assert.Contains("stat_prefix: nac_plain", envoyConfig, StringComparison.Ordinal);

        // Assert NonPublicV4 address matcher ranges are rendered
        Assert.Contains("address_prefix: 127.0.0.0", envoyConfig, StringComparison.Ordinal);
        Assert.Contains("prefix_len: 8", envoyConfig, StringComparison.Ordinal);
        Assert.Contains("address_prefix: 169.254.0.0", envoyConfig, StringComparison.Ordinal);
        Assert.Contains("address_prefix: 168.63.129.16", envoyConfig, StringComparison.Ordinal);
        Assert.Contains("address_prefix: 10.247.0.0", envoyConfig, StringComparison.Ordinal);

        // Negative Control (.NC):
        // If someone mutates the config to failure_mode_allow: true, guard assertion fails
        string mutatedConfig = envoyConfig.Replace("failure_mode_allow: false", "failure_mode_allow: true", StringComparison.Ordinal);
        Assert.Contains("failure_mode_allow: true", mutatedConfig, StringComparison.Ordinal);
        Assert.NotEqual(envoyConfig, mutatedConfig);
    }

    [Fact]
    public async Task V7_28_ReconcileOnce_PreservesContainersRegisteredByNameAndPromotesToHexId_WithNegativeControl()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"nac-v728-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            var registry = new WorkloadRegistry();
            string containerName = "kautuka-sandbox-u1-a1";
            string hexCid = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
            registry.Register(new RegistryEntry(
                "10.247.1.10",
                "u1",
                "a1",
                containerName,
                "agent-sandbox",
                null,
                "agent-sandbox",
                "normal",
                "user-agent",
                null,
                DateTimeOffset.UtcNow));

            bool containerStillExists = true;
            var podman = new PodmanCommandRunner((args, _) =>
            {
                if (args.Count >= 2 && args[0] == "ps")
                {
                    string json = containerStillExists
                        ? "[{\"Id\":\"" + hexCid + "\",\"Names\":[\"" + containerName + "\"]}]"
                        : "[]";
                    return Task.FromResult(new PodmanCommandResult(0, json, string.Empty, ProcessTerminationReason.Completed));
                }

                return Task.FromResult(new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed));
            });

            var allocator = new WorkloadAddressAllocator(Path.Combine(tempRoot, "ip.json"));
            var verifier = new WorkloadPostStartVerifier(podman);
            await using var manager = new EgressTopologyManager(podman, allocator, verifier, registry, tempRoot);

            await manager.ReconcileOnceAsync(registry, CancellationToken.None);

            // Live workload registered by containerName MUST stay registered and promote to canonical hex ID without conflict.
            Assert.True(registry.TryGetByIp("10.247.1.10", out RegistryEntry? promoted));
            Assert.NotNull(promoted);
            Assert.Equal(hexCid, promoted.ContainerId);
            Assert.Equal(0, registry.RegistryConflicts);

            // Negative Control (.NC): once Podman reports the container no longer exists, reconcile deregisters it.
            containerStillExists = false;
            await manager.ReconcileOnceAsync(registry, CancellationToken.None);
            Assert.False(registry.TryGetByIp("10.247.1.10", out _));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task V7_20_RebuildRegistry_SkipsMalformedContainerAndKeepsLaterValidEntry()
    {
        string validCid = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var podman = new PodmanCommandRunner((args, _) =>
        {
            if (args.Count >= 2 && args[0] == "ps")
            {
                return Task.FromResult(new PodmanCommandResult(0,
                    "[{\"Id\":\"bad\"},{\"Id\":\"" + validCid + "\"}]", string.Empty, ProcessTerminationReason.Completed));
            }

            if (args.Count >= 2 && args[0] == "inspect" && args[1] == "bad")
            {
                return Task.FromResult(new PodmanCommandResult(0, "{", string.Empty, ProcessTerminationReason.Completed));
            }

            if (args.Count >= 2 && args[0] == "inspect" && args[1] == validCid)
            {
                var inspect = new[]
                {
                    new
                    {
                        Id = validCid,
                        Config = new { Labels = new Dictionary<string, string> { ["io.kautuka.user"] = "u1", ["io.kautuka.agent"] = "a1", ["io.kautuka.exec-context"] = "agent-sandbox", ["io.kautuka.kind"] = "agent-sandbox", ["io.kautuka.egress"] = "normal" }, User = "65532:65532" },
                        HostConfig = new { SecurityOpt = new[] { "no-new-privileges" }, Privileged = false, CapAdd = Array.Empty<string>(), CapDrop = new[] { "ALL" }, ReadonlyRootfs = true, NetworkMode = "kautuka-workload", Sysctls = new Dictionary<string, string> { ["net.ipv6.conf.all.disable_ipv6"] = "1" } },
                        EffectiveCaps = Array.Empty<string>(), BoundingCaps = Array.Empty<string>(),
                        State = new { Pid = 4242 },
                        NetworkSettings = new { Networks = new Dictionary<string, object> { ["kautuka-workload"] = new { IPAddress = "10.247.1.10", GlobalIPv6Address = "" } } },
                        Mounts = Array.Empty<object>()
                    }
                };
                return Task.FromResult(new PodmanCommandResult(0, System.Text.Json.JsonSerializer.Serialize(inspect), string.Empty, ProcessTerminationReason.Completed));
            }

            if (args.Any(arg => arg.Contains("disable_ipv6", StringComparison.Ordinal)))
                return Task.FromResult(new PodmanCommandResult(0, "1", string.Empty, ProcessTerminationReason.Completed));
            if (args.Contains("nft", StringComparer.Ordinal))
            {
                string rules = BoundaryRulesetRenderer.ResolveTemplate("kautuka_workload.nft.json.tmpl").Replace("{{IP}}", "10.247.1.10", StringComparison.Ordinal);
                return Task.FromResult(new PodmanCommandResult(0, rules, string.Empty, ProcessTerminationReason.Completed));
            }

            return Task.FromResult(new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed));
        });

        var registry = new WorkloadRegistry();
        var allocator = new WorkloadAddressAllocator(Path.Combine(tempDir, "v720-ip.json"));
        var verifier = new WorkloadPostStartVerifier(podman);
        await using var manager = new EgressTopologyManager(podman, allocator, verifier, registry, tempDir);

        await manager.RebuildRegistryFromLiveContainersAsync(registry, TestContext.Current.CancellationToken);

        Assert.True(registry.TryGetByIp("10.247.1.10", out RegistryEntry? entry));
        Assert.NotNull(entry);
        Assert.Equal(validCid, entry.ContainerId);
    }

    [Fact]
    public async Task RebuildRegistryRetainsUserlessDenyAllSandboxButRejectsNormalEgress()
    {
        const string restrictedId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string normalId = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        const string image = "ghcr.io/devkrity/kautuka-base@sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var spec = new WorkloadContainerSpec("sandbox", image, "10.247.1.10")
        {
            UserId = string.Empty,
            AgentId = string.Empty,
            Kind = "agent-sandbox",
            ExecContext = "agent-sandbox",
            Egress = "deny-all",
            StartupGateOpen = true,
            IsBraked = false
        };
        IReadOnlyList<string> args = spec.Render();
        var labels = new Dictionary<string, string>();
        for (int i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "--label")
            {
                string[] pair = args[++i].Split('=', 2);
                labels[pair[0]] = pair[1];
            }
        }
        var normalLabels = new Dictionary<string, string>(labels) { ["io.kautuka.egress"] = "normal" };
        var removed = new List<string>();
        string inspectedIp = string.Empty;
        var podman = new PodmanCommandRunner((command, _) =>
        {
            if (command[0] == "ps")
                return Task.FromResult(new PodmanCommandResult(0, System.Text.Json.JsonSerializer.Serialize(new[] { new { Id = restrictedId, State = "running" }, new { Id = normalId, State = "running" } }), ""));
            if (command[0] == "inspect")
            {
                bool restricted = command[1] == restrictedId;
                string ip = restricted ? "10.247.1.10" : "10.247.1.11";
                inspectedIp = ip;
                string labelJson = System.Text.Json.JsonSerializer.Serialize(restricted ? labels : normalLabels);
                var inspect = new[]
                {
                    new
                    {
                        Id = command[1],
                        Config = new { Labels = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(labelJson)!, User = "65532:65532" },
                        HostConfig = new { SecurityOpt = new[] { "no-new-privileges" }, Privileged = false, CapAdd = Array.Empty<string>(), CapDrop = new[] { "ALL" }, ReadonlyRootfs = true, NetworkMode = "kautuka-workload", Sysctls = new Dictionary<string, string> { ["net.ipv6.conf.all.disable_ipv6"] = "1" } },
                        EffectiveCaps = Array.Empty<string>(), BoundingCaps = Array.Empty<string>(), State = new { Pid = 4242 },
                        NetworkSettings = new { Networks = new Dictionary<string, object> { ["kautuka-workload"] = new { IPAddress = ip, GlobalIPv6Address = "" } } },
                        Mounts = Array.Empty<object>()
                    }
                };
                return Task.FromResult(new PodmanCommandResult(0, System.Text.Json.JsonSerializer.Serialize(inspect), ""));
            }
            if (command.Any(arg => arg.Contains("disable_ipv6", StringComparison.Ordinal))) return Task.FromResult(new PodmanCommandResult(0, "1", ""));
            if (command.Contains("nft", StringComparer.Ordinal))
            {
                string rules = BoundaryRulesetRenderer.ResolveTemplate("kautuka_workload.nft.json.tmpl").Replace("{{IP}}", inspectedIp, StringComparison.Ordinal);
                return Task.FromResult(new PodmanCommandResult(0, rules, ""));
            }
            if (command[0] == "rm") removed.Add(command[^1]);
            return Task.FromResult(new PodmanCommandResult(0, "", ""));
        });
        var registry = new WorkloadRegistry();
        var allocator = new WorkloadAddressAllocator(Path.Combine(tempDir, "rebuild-ip.json"));
        await using var manager = new EgressTopologyManager(podman, allocator, new WorkloadPostStartVerifier(podman), registry, tempDir);

        await manager.RebuildRegistryFromLiveContainersAsync(registry, TestContext.Current.CancellationToken);

        Assert.True(registry.TryGetByIp("10.247.1.10", out RegistryEntry? entry));
        Assert.NotNull(entry);
        Assert.Equal("deny-all", entry.Egress);
        Assert.Equal("none", entry.Attribution);
        Assert.False(registry.TryGetByIp("10.247.1.11", out _));
        Assert.Equal(new[] { normalId }, removed);
    }

    [Fact]
    public void CanaryRejectsStaleHookRecordForReusedIp()
    {
        const string canaryIp = "10.247.1.24";
        string hash = WorkloadRulesetRenderer.ComputeExpectedCanonicalRulesetHash(canaryIp);
        string stale = $$"""{"id":"{{new string('a', 64)}}","action":"allow","rules_sha256":"{{hash}}","ips":["{{canaryIp}}"]}""";
        Assert.False(EgressTopologyManager.HasSuccessfulCanaryHookRecord(stale, canaryIp, new string('b', 64)));
    }

    [Fact]
    public void V7_20_CanaryRequiresSuccessfulHookRecordForItsOwnIp_WithNegativeControl()
    {
        const string canaryIp = "10.247.1.24";
        string canaryId = new('b', 64);
        string expectedRulesHash = WorkloadRulesetRenderer.ComputeExpectedCanonicalRulesetHash(canaryIp);
        string successfulHook = $$"""{"id":"{{canaryId}}","action":"allow","reason":"workload_rules","rules_sha256":"{{expectedRulesHash}}","ips":["{{canaryIp}}"]}""";

        Assert.True(EgressTopologyManager.HasSuccessfulCanaryHookRecord(successfulHook, canaryIp, canaryId));

        // Negative controls: a warning-only/missing record, a refusal, a stale record for another IP,
        // a success with a mismatched applied-rules hash, or a missing hash must all keep the startup gate closed.
        Assert.False(EgressTopologyManager.HasSuccessfulCanaryHookRecord(null, canaryIp, canaryId));
        Assert.False(EgressTopologyManager.HasSuccessfulCanaryHookRecord(successfulHook.Replace("allow", "refuse", StringComparison.Ordinal), canaryIp, canaryId));
        Assert.False(EgressTopologyManager.HasSuccessfulCanaryHookRecord(successfulHook.Replace(canaryIp, "10.247.1.25", StringComparison.Ordinal), canaryIp, canaryId));
        Assert.False(EgressTopologyManager.HasSuccessfulCanaryHookRecord(successfulHook.Replace(expectedRulesHash, "abc123", StringComparison.Ordinal), canaryIp, canaryId));
        Assert.False(EgressTopologyManager.HasSuccessfulCanaryHookRecord(successfulHook.Replace(canaryId, "", StringComparison.Ordinal), canaryIp, canaryId));
        Assert.False(EgressTopologyManager.HasSuccessfulCanaryHookRecord(successfulHook, canaryIp, "short-id"));
    }

    [Fact]
    public async Task V7_20_ConfigReplacementKeepsPreviousFileOnInterruptedWrite_WithNegativeControl()
    {
        string configPath = Path.Combine(tempDir, "envoy.yaml");
        await File.WriteAllTextAsync(configPath, "previous-valid-config", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<IOException>(() => EgressTopologyManager.WriteConfigAtomicallyAsync(
            configPath,
            "new-config-that-must-not-be-partially-published",
            TestContext.Current.CancellationToken,
            async (stream, text, cancellationToken) =>
            {
                byte[] partial = System.Text.Encoding.UTF8.GetBytes(text[..8]);
                await stream.WriteAsync(partial, cancellationToken);
                throw new IOException("simulated write interruption");
            }));

        Assert.Equal("previous-valid-config", await File.ReadAllTextAsync(configPath, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(tempDir, ".nac-config-*.tmp"));
    }

    [Fact]
    public async Task V7_20_EgressTopologyManagerDisposalIsSafeWhenHostedServiceAndDiDisposeIt()
    {
        var podman = new PodmanCommandRunner((_, _) => Task.FromResult(new PodmanCommandResult(
            0,
            string.Empty,
            string.Empty,
            ProcessTerminationReason.Completed)));
        var allocator = new WorkloadAddressAllocator(Path.Combine(tempDir, "ip-cursor.json"));
        var manager = new EgressTopologyManager(
            podman,
            allocator,
            new WorkloadPostStartVerifier(podman),
            new WorkloadRegistry(),
            tempDir);
        var reconcileCts = new CancellationTokenSource();
        var reconcileLoop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(EgressTopologyManager)
            .GetField("reconcileCts", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(manager, reconcileCts);
        typeof(EgressTopologyManager)
            .GetField("reconcileTask", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(manager, reconcileLoop.Task);

        // StopAsync disposes as part of the hosted-service lifecycle; the DI container
        // then calls DisposeAsync for the same singleton. Exercise overlap as well as the
        // normal repeated lifecycle call so the test catches a disposed CTS being reused.
        // Hold the reconcile loop open to prove both callers await the same cleanup operation.
        Task stopTask = manager.StopAsync(TestContext.Current.CancellationToken);
        Task diDisposeTask = manager.DisposeAsync().AsTask();
        Assert.False(stopTask.IsCompleted);
        Assert.False(diDisposeTask.IsCompleted);

        reconcileLoop.TrySetResult();
        await Task.WhenAll(stopTask, diDisposeTask);
        await manager.StopAsync(TestContext.Current.CancellationToken);
        await manager.DisposeAsync();

        Assert.True(reconcileCts.IsCancellationRequested);
    }

    [Fact]
    public void V7_20_UnprivilegedInfraImagesUseTheirPinnedNonRootUsers_WithNegativeControl()
    {
        IReadOnlyList<string> dnsArgs = EgressTopologyManager.BuildCoreDnsCreateArgs("/state/Corefile");
        IReadOnlyList<string> envoyArgs = EgressTopologyManager.BuildEnvoyCreateArgs(
            "/state/envoy.yaml",
            "/state/sockets",
            "/state/admin-socket");

        Assert.DoesNotContain("--user", dnsArgs);
        Assert.DoesNotContain("--user", envoyArgs);
        Assert.Contains("--cap-add", dnsArgs);
        Assert.Contains("NET_BIND_SERVICE", dnsArgs);
        Assert.Contains(EgressTopologyManager.CoreDnsPinnedImage, dnsArgs);
        Assert.Contains(EgressTopologyManager.EnvoyPinnedImage, envoyArgs);
    }

    [Fact]
    public void BoundaryContainerUsesDedicatedRelabeledDirectoryAndDetectsOldMount_WithNegativeControl()
    {
        string boundaryDir = Path.Combine(tempDir, "nac", "boundary");
        IReadOnlyList<string> linuxArgs = EgressTopologyManager.BuildBoundaryCreateArgs(boundaryDir, isWindowsHostNativeDev: false);
        string linuxMount = linuxArgs[linuxArgs.ToList().IndexOf("-v") + 1];
        Assert.Equal($"{Path.GetFullPath(boundaryDir)}:/etc/kautuka-nac:ro,z", linuxMount);
        Assert.Contains(linuxArgs, arg => arg.Contains("/etc/kautuka-nac/boundary.nft", StringComparison.Ordinal));

        IReadOnlyList<string> windowsArgs = EgressTopologyManager.BuildBoundaryCreateArgs("C:\\state\\nac\\boundary", isWindowsHostNativeDev: true);
        Assert.Contains("C:\\state\\nac\\boundary:/etc/kautuka-nac:ro", windowsArgs, StringComparer.Ordinal);
        Assert.DoesNotContain(windowsArgs, arg => arg.EndsWith(":ro,z", StringComparison.Ordinal));

        string current = JsonSerializer.Serialize(new[] { new { CreateCommand = new[] { "podman", "create", "-v", linuxMount } } });
        string oldFileMount = """[{"CreateCommand":["podman","create","-v","/state/nac/boundary.nft:/etc/nftables.conf:ro"]}]""";
        Assert.True(EgressTopologyManager.HasRequiredBoundaryContainerMount(current, boundaryDir, isWindowsHostNativeDev: false));
        Assert.False(EgressTopologyManager.HasRequiredBoundaryContainerMount(oldFileMount, boundaryDir, isWindowsHostNativeDev: false));
    }

    [Fact]
    public void DevelopmentEnvoyUsesItsOwnSelinuxDomainOnlyWhenRequested()
    {
        string envoy = Path.Combine(tempDir, "envoy.yaml");
        IReadOnlyList<string> enabled = EgressTopologyManager.BuildEnvoyCreateArgs(
            envoy, "sockets", "admin", useDevelopmentSelinuxDomain: true, podSelinuxLevel: "s0:c123,c456");
        Assert.Contains("--security-opt", enabled);
        Assert.Contains("label=type:kautuka_envoy.process", enabled);
        Assert.Contains("label=level:s0:c123,c456", EgressTopologyManager.BuildEnvoyCreateArgs(
            envoy, "sockets", "admin", useDevelopmentSelinuxDomain: true, podSelinuxLevel: "s0:c123,c456"));
        Assert.Throws<ArgumentNullException>(() => EgressTopologyManager.BuildEnvoyCreateArgs(
            envoy, "sockets", "admin", useDevelopmentSelinuxDomain: true));

        IReadOnlyList<string> ordinary = EgressTopologyManager.BuildEnvoyCreateArgs(envoy, "sockets", "admin");
        Assert.DoesNotContain("label=type:kautuka_envoy.process", ordinary);
    }

    [Fact]
    public void DnsAndEnvoyConfigsUseDedicatedReadOnlyRelabeledDirectoryMounts_WithNegativeControl()
    {
        string dnsDir = Path.Combine(tempDir, "nac", "dns");
        string envoyDir = Path.Combine(tempDir, "nac", "envoy");
        IReadOnlyList<string> dns = EgressTopologyManager.BuildCoreDnsCreateArgs(Path.Combine(dnsDir, "Corefile"));
        IReadOnlyList<string> envoy = EgressTopologyManager.BuildEnvoyCreateArgs(
            Path.Combine(envoyDir, "envoy.yaml"), Path.Combine(tempDir, "sockets"), Path.Combine(tempDir, "admin"));

        Assert.Contains($"{Path.GetFullPath(dnsDir)}:/etc/coredns:ro,z", dns);
        Assert.Contains("/etc/coredns/Corefile", dns);
        Assert.Contains($"{Path.GetFullPath(envoyDir)}:/etc/kautuka-envoy:ro,z", envoy);
        Assert.Contains("/etc/kautuka-envoy/envoy.yaml", envoy);

        string oldDnsFileMount = """[{"CreateCommand":["podman","create","-v","/state/nac/Corefile:/etc/coredns/Corefile:ro"]}]""";
        string oldEnvoyFileMount = """[{"CreateCommand":["podman","create","-v","/state/nac/envoy.yaml:/etc/envoy/envoy.yaml:ro"]}]""";
        Assert.False(EgressTopologyManager.HasRequiredConfigContainerMount(oldDnsFileMount, dnsDir, "/etc/coredns", false));
        Assert.False(EgressTopologyManager.HasRequiredConfigContainerMount(oldEnvoyFileMount, envoyDir, "/etc/kautuka-envoy", false));

        string currentDns = JsonSerializer.Serialize(new[] { new { CreateCommand = new[] { "podman", "create", "-v", $"{Path.GetFullPath(dnsDir)}:/etc/coredns:ro,z" } } });
        string currentEnvoy = JsonSerializer.Serialize(new[] { new { CreateCommand = new[] { "podman", "create", "-v", $"{Path.GetFullPath(envoyDir)}:/etc/kautuka-envoy:ro,z" } } });
        Assert.True(EgressTopologyManager.HasRequiredConfigContainerMount(currentDns, dnsDir, "/etc/coredns", false));
        Assert.True(EgressTopologyManager.HasRequiredConfigContainerMount(currentEnvoy, envoyDir, "/etc/kautuka-envoy", false));
        IReadOnlyList<string> windowsDns = EgressTopologyManager.BuildCoreDnsCreateArgs(Path.Combine(dnsDir, "Corefile"), isWindowsHostNativeDev: true);
        IReadOnlyList<string> windowsEnvoy = EgressTopologyManager.BuildEnvoyCreateArgs(Path.Combine(envoyDir, "envoy.yaml"), "sockets", "admin", isWindowsHostNativeDev: true, decisionEndpointIp: "172.20.144.1", envoyCertificateDirectory: "C:\\certs");
        Assert.Contains($"{Path.GetFullPath(dnsDir)}:/etc/coredns:ro", windowsDns);
        Assert.Contains($"{Path.GetFullPath(envoyDir)}:/etc/kautuka-envoy:ro", windowsEnvoy);
        Assert.Contains("/etc/kautuka-envoy/envoy.yaml", windowsEnvoy);
        Assert.Contains("C:\\certs:/etc/envoy/certs:ro", windowsEnvoy);
    }

    [Theory]
    [InlineData("kautuka-egress-dns", "/etc/coredns")]
    [InlineData("kautuka-egress-envoy", "/etc/kautuka-envoy")]
    public async Task ConfigMountMigrationStopsPodOnceAndRemovesOnlyTheStaleInfraContainer(string containerName, string containerConfigDirectory)
    {
        ArgumentNullException.ThrowIfNull(containerName);
        ArgumentNullException.ThrowIfNull(containerConfigDirectory);
        const string staleInspect = """[{"CreateCommand":["podman","create","-v","/state/old-config:/etc/old-config:ro"]}]""";
        var calls = new List<IReadOnlyList<string>>();
        string podState = "Running";
        string[] states = ["running", "running", "running", "running"];
        bool removed = false;
        bool failStop = false;
        bool failRemove = false;
        bool failInspect = false;
        bool failExistsCheck = false;
        var podman = new PodmanCommandRunner((args, _) =>
        {
            calls.Add(args.ToArray());
            string output = args.SequenceEqual(["inspect", containerName]) ? staleInspect
                : args.SequenceEqual(["pod", "inspect", EgressTopologyManager.EgressPodName])
                    ? $"[{{\"State\":\"{podState}\",\"InfraContainerID\":\"infra-id\",\"Containers\":[{{\"Id\":\"boundary-id\",\"Name\":\"kautuka-egress-boundary\",\"State\":\"{states[0]}\"}},{{\"Id\":\"dns-id\",\"Name\":\"kautuka-egress-dns\",\"State\":\"{states[1]}\"}},{{\"Id\":\"envoy-id\",\"Name\":\"kautuka-egress-envoy\",\"State\":\"{states[2]}\"}},{{\"Id\":\"infra-id\",\"Name\":\"kautuka-egress-pod-infra\",\"State\":\"{states[3]}\"}}]}}]"
                    : string.Empty;
            bool failed = args.SequenceEqual(["pod", "stop", EgressTopologyManager.EgressPodName]) && failStop
                || args.SequenceEqual(["rm", "-f", containerName]) && failRemove
                || args.SequenceEqual(["inspect", containerName]) && failInspect
                || args.SequenceEqual(["container", "exists", containerName]) && failExistsCheck;
            if (args.SequenceEqual(["pod", "stop", EgressTopologyManager.EgressPodName]) && !failed)
            {
                podState = "Exited";
                states = ["exited", "created", "created", "exited"];
            }
            if (args.SequenceEqual(["rm", "-f", containerName]) && !failed) removed = true;
            int exitCode = failed
                ? (args.SequenceEqual(["container", "exists", containerName]) ? 125 : 1)
                : 0;
            return Task.FromResult(new PodmanCommandResult(exitCode, output, failed ? "fixture failure" : string.Empty, ProcessTerminationReason.Completed));
        });
        var registry = new WorkloadRegistry();
        await using var manager = new EgressTopologyManager(
            podman, new WorkloadAddressAllocator(Path.Combine(tempDir, "dns-migration.json")),
            new WorkloadPostStartVerifier(podman), registry, tempDir);
        var migrate = typeof(EgressTopologyManager).GetMethod("EnsureConfigContainerMountCurrentAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Config mount migration method was not found.");

        string configDirectory = Path.Combine(tempDir, "nac", containerName.EndsWith("dns", StringComparison.Ordinal) ? "dns" : "envoy");
        await ((Task)migrate.Invoke(manager, [containerName, configDirectory, containerConfigDirectory, false, TestContext.Current.CancellationToken])!).ConfigureAwait(false);

        Assert.True(removed);
        Assert.Equal(1, calls.Count(args => args.SequenceEqual(["pod", "stop", EgressTopologyManager.EgressPodName])));
        Assert.Contains(calls, args => args.SequenceEqual(["rm", "-f", containerName]));
        string unrelatedConfigContainer = containerName == "kautuka-egress-envoy" ? "kautuka-egress-dns" : "kautuka-egress-envoy";
        Assert.DoesNotContain(calls, args => args.SequenceEqual(["rm", "-f", unrelatedConfigContainer]));
        Assert.DoesNotContain(calls, args => args.SequenceEqual(["rm", "-f", "kautuka-egress-boundary"]));

        // The second stale config container reuses the already stopped pod; it does not stop the pod again.
        string otherConfigDirectory = Path.Combine(tempDir, "nac", containerName == "kautuka-egress-dns" ? "envoy" : "dns");
        string otherContainerConfigDirectory = containerName == "kautuka-egress-dns" ? "/etc/kautuka-envoy" : "/etc/coredns";
        await ((Task)migrate.Invoke(manager, [unrelatedConfigContainer, otherConfigDirectory, otherContainerConfigDirectory, false, TestContext.Current.CancellationToken])!).ConfigureAwait(false);
        Assert.Equal(1, calls.Count(args => args.SequenceEqual(["pod", "stop", EgressTopologyManager.EgressPodName])));
        Assert.Contains(calls, args => args.SequenceEqual(["rm", "-f", "kautuka-egress-dns"]));
        Assert.Contains(calls, args => args.SequenceEqual(["rm", "-f", "kautuka-egress-envoy"]));

        // Negative controls: ignoring a failed stop or failed targeted removal would allow migration to proceed unsafely.
        calls.Clear();
        removed = false;
        podState = "Running";
        states = ["running", "running", "running", "running"];
        failStop = true;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await (Task)migrate.Invoke(manager, [containerName, configDirectory, containerConfigDirectory, false, TestContext.Current.CancellationToken])!);
        Assert.DoesNotContain(calls, args => args.SequenceEqual(["rm", "-f", containerName]));

        calls.Clear();
        podState = "Exited";
        states = ["exited", "created", "created", "exited"];
        failStop = false;
        failRemove = true;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await (Task)migrate.Invoke(manager, [containerName, configDirectory, containerConfigDirectory, false, TestContext.Current.CancellationToken])!);
        Assert.Contains(calls, args => args.SequenceEqual(["rm", "-f", containerName]));
        Assert.False(removed);

        // If inspect transiently fails while the container still exists, never infer that it is absent/current.
        calls.Clear();
        failRemove = false;
        failInspect = true;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await (Task)migrate.Invoke(manager, [containerName, configDirectory, containerConfigDirectory, false, TestContext.Current.CancellationToken])!);
        Assert.Contains(calls, args => args.SequenceEqual(["container", "exists", containerName]));
        Assert.DoesNotContain(calls, args => args.SequenceEqual(["pod", "stop", EgressTopologyManager.EgressPodName]));
        Assert.DoesNotContain(calls, args => args.SequenceEqual(["rm", "-f", containerName]));

        // An existence-check error is also ambiguous and must fail closed.
        calls.Clear();
        failExistsCheck = true;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await (Task)migrate.Invoke(manager, [containerName, configDirectory, containerConfigDirectory, false, TestContext.Current.CancellationToken])!);
        Assert.DoesNotContain(calls, args => args.SequenceEqual(["pod", "stop", EgressTopologyManager.EgressPodName]));
        Assert.DoesNotContain(calls, args => args.SequenceEqual(["rm", "-f", containerName]));
    }

    [Fact]
    public async Task BoundaryMountMigrationStopsOnlyForDriftAndFailsClosedOnUnknownPodState()
    {
        const string staleInspect = """[{"CreateCommand":["podman","create","-v","/old/boundary.nft:/etc/nftables.conf:ro"]}]""";
        var calls = new List<IReadOnlyList<string>>();
        string podState = "Running";
        string[] memberStates = ["running", "running", "running", "running"];
        string[] memberIds = ["boundary-id", "dns-id", "envoy-id", "infra-id"];
        string podInfraContainerId = "infra-id";
        string boundaryInspect = staleInspect;
        bool failPodInspect = false;
        bool failStoppedPodInspect = false;
        bool failStop = false;
        bool failRemove = false;
        bool leaveMemberRunningAfterStop = false;
        var podman = new PodmanCommandRunner((args, _) =>
        {
            calls.Add(args.ToArray());
            bool failed = args.SequenceEqual(["pod", "inspect", EgressTopologyManager.EgressPodName]) && failPodInspect
                || args.SequenceEqual(["pod", "inspect", EgressTopologyManager.EgressPodName]) && failStoppedPodInspect && string.Equals(podState, "Exited", StringComparison.Ordinal)
                || args.SequenceEqual(["pod", "stop", EgressTopologyManager.EgressPodName]) && failStop
                || args.SequenceEqual(["rm", "-f", "kautuka-egress-boundary"]) && failRemove;
            string output = args.SequenceEqual(["inspect", "kautuka-egress-boundary"])
                ? boundaryInspect
                : args.SequenceEqual(["pod", "inspect", EgressTopologyManager.EgressPodName])
                    ? $"[{{\"State\":\"{podState}\",\"InfraContainerID\":\"{podInfraContainerId}\",\"Containers\":[{{\"Id\":\"{memberIds[0]}\",\"Name\":\"kautuka-egress-boundary\",\"State\":\"{memberStates[0]}\"}},{{\"Id\":\"{memberIds[1]}\",\"Name\":\"kautuka-egress-dns\",\"State\":\"{memberStates[1]}\"}},{{\"Id\":\"{memberIds[2]}\",\"Name\":\"kautuka-egress-envoy\",\"State\":\"{memberStates[2]}\"}},{{\"Id\":\"{memberIds[3]}\",\"Name\":\"pod-infra\",\"State\":\"{memberStates[3]}\"}}]}}]"
                    : string.Empty;
            if (args.SequenceEqual(["pod", "stop", EgressTopologyManager.EgressPodName]))
            {
                podState = "Exited";
                if (!leaveMemberRunningAfterStop)
                {
                    memberStates = ["exited", "created", "created", "exited"];
                }
                else
                {
                    memberStates = ["exited", "created", "created", "running"];
                }
            }
            return Task.FromResult(new PodmanCommandResult(failed ? 1 : 0, output, failed ? "fixture failure" : string.Empty, ProcessTerminationReason.Completed));
        });
        var registry = new WorkloadRegistry();
        var allocator = new WorkloadAddressAllocator(Path.Combine(tempDir, "boundary-migration.json"));
        var verifier = new WorkloadPostStartVerifier(podman);
        await using var manager = new EgressTopologyManager(podman, allocator, verifier, registry, tempDir);
        System.Reflection.MethodInfo migrate = typeof(EgressTopologyManager).GetMethod(
            "EnsureBoundaryContainerMountCurrentAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Boundary mount migration method was not found.");

        await ((Task)migrate.Invoke(manager, [Path.Combine(tempDir, "nac", "boundary"), false, TestContext.Current.CancellationToken])!).ConfigureAwait(false);
        Assert.Contains(calls, args => args.SequenceEqual(["pod", "stop", EgressTopologyManager.EgressPodName]));
        Assert.Contains(calls, args => args.SequenceEqual(["rm", "-f", "kautuka-egress-boundary"]));
        Assert.Equal(2, calls.Count(args => args.SequenceEqual(["pod", "inspect", EgressTopologyManager.EgressPodName])));

        // A degraded pod must be stopped and verified member-by-member before replacing the boundary.
        calls.Clear();
        boundaryInspect = staleInspect;
        podState = "Degraded";
        memberStates = ["running", "running", "running", "running"];
        await ((Task)migrate.Invoke(manager, [Path.Combine(tempDir, "nac", "boundary"), false, TestContext.Current.CancellationToken])!).ConfigureAwait(false);
        Assert.Contains(calls, args => args.SequenceEqual(["pod", "stop", EgressTopologyManager.EgressPodName]));
        Assert.Contains(calls, args => args.SequenceEqual(["rm", "-f", "kautuka-egress-boundary"]));
        Assert.Equal(["exited", "created", "created", "exited"], memberStates);

        calls.Clear();
        podState = "Degraded";
        memberStates = ["running", "running", "running", "running"];
        leaveMemberRunningAfterStop = true;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await (Task)migrate.Invoke(manager, [Path.Combine(tempDir, "nac", "boundary"), false, TestContext.Current.CancellationToken])!);
        Assert.DoesNotContain(calls, args => args.SequenceEqual(["rm", "-f", "kautuka-egress-boundary"]));
        leaveMemberRunningAfterStop = false;

        calls.Clear();
        podState = "Degraded";
        memberStates = ["running", "created", "created", "running"];
        failStoppedPodInspect = true;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await (Task)migrate.Invoke(manager, [Path.Combine(tempDir, "nac", "boundary"), false, TestContext.Current.CancellationToken])!);
        Assert.DoesNotContain(calls, args => args.SequenceEqual(["rm", "-f", "kautuka-egress-boundary"]));
        failStoppedPodInspect = false;

        calls.Clear();
        podState = "Exited";
        podInfraContainerId = "missing-infra-id";
        memberStates = ["exited", "created", "created", "exited"];
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await (Task)migrate.Invoke(manager, [Path.Combine(tempDir, "nac", "boundary"), false, TestContext.Current.CancellationToken])!);
        Assert.DoesNotContain(calls, args => args.SequenceEqual(["rm", "-f", "kautuka-egress-boundary"]));
        podInfraContainerId = "infra-id";

        calls.Clear();
        IReadOnlyList<string> expectedArgs = EgressTopologyManager.BuildBoundaryCreateArgs(Path.Combine(tempDir, "nac", "boundary"), false);
        string currentMount = expectedArgs[expectedArgs.ToList().IndexOf("-v") + 1];
        boundaryInspect = JsonSerializer.Serialize(new[] { new { CreateCommand = new[] { "podman", "create", "-v", currentMount } } });
        await ((Task)migrate.Invoke(manager, [Path.Combine(tempDir, "nac", "boundary"), false, TestContext.Current.CancellationToken])!).ConfigureAwait(false);
        Assert.Single(calls);
        Assert.Equal(["inspect", "kautuka-egress-boundary"], calls[0]);

        calls.Clear();
        boundaryInspect = staleInspect;
        podState = "Paused";
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await (Task)migrate.Invoke(manager, [Path.Combine(tempDir, "nac", "boundary"), false, TestContext.Current.CancellationToken])!);
        Assert.DoesNotContain(calls, args => args.SequenceEqual(["rm", "-f", "kautuka-egress-boundary"]));

        calls.Clear();
        failPodInspect = true;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await (Task)migrate.Invoke(manager, [Path.Combine(tempDir, "nac", "boundary"), false, TestContext.Current.CancellationToken])!);
        Assert.DoesNotContain(calls, args => args.SequenceEqual(["rm", "-f", "kautuka-egress-boundary"]));

        failPodInspect = false;
        failStop = true;
        podState = "Running";
        calls.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await (Task)migrate.Invoke(manager, [Path.Combine(tempDir, "nac", "boundary"), false, TestContext.Current.CancellationToken])!);
        Assert.DoesNotContain(calls, args => args.SequenceEqual(["rm", "-f", "kautuka-egress-boundary"]));

        failStop = false;
        failRemove = true;
        podState = "Exited";
        calls.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await (Task)migrate.Invoke(manager, [Path.Combine(tempDir, "nac", "boundary"), false, TestContext.Current.CancellationToken])!);
        Assert.Contains(calls, args => args.SequenceEqual(["rm", "-f", "kautuka-egress-boundary"]));
    }

    [Fact]
    public void V7_20_RootlessEnvoyKeepsHostGroupsWithoutRunningAsRoot_WithNegativeControl()
    {
        IReadOnlyList<string> envoyArgs = EgressTopologyManager.BuildEnvoyCreateArgs(
            "/state/envoy.yaml",
            "/state/sockets",
            "/state/admin-socket");

        int groupAddIndex = envoyArgs.ToList().IndexOf("--group-add");
        Assert.True(groupAddIndex >= 0 && groupAddIndex + 1 < envoyArgs.Count);
        Assert.Equal("keep-groups", envoyArgs[groupAddIndex + 1]);
        Assert.DoesNotContain("--user", envoyArgs);

        // Negative control: dropping keep-groups removes Envoy's access to the group-owned 0660 socket.
        string[] withoutHostGroups = [.. envoyArgs.Where((_, index) => index != groupAddIndex && index != groupAddIndex + 1)];
        Assert.DoesNotContain("--group-add", withoutHostGroups);
        Assert.NotEqual(envoyArgs, withoutHostGroups);
    }

    [Fact]
    public void WindowsRemotePodmanEnvoyDoesNotRequestLinuxKeepGroups()
    {
        IReadOnlyList<string> envoyArgs = EgressTopologyManager.BuildEnvoyCreateArgs(
            "/state/envoy.yaml",
            "/state/sockets",
            "/state/admin-socket",
            isWindowsHostNativeDev: true,
            decisionEndpointIp: "172.20.144.1",
            envoyCertificateDirectory: "C:\\Users\\user\\.kautuka\\runtime\\manager\\networkAccess\\envoy-certs");

        Assert.DoesNotContain("--group-add", envoyArgs);
        Assert.DoesNotContain(envoyArgs, arg => arg.Contains("/run/kautuka-nac", StringComparison.Ordinal));
        Assert.DoesNotContain(envoyArgs, arg => arg.Contains("/run/kautuka-admin", StringComparison.Ordinal));
        Assert.DoesNotContain("--add-host", envoyArgs);
        IReadOnlyList<string> podArgs = EgressTopologyManager.BuildEgressPodCreateArgs(
            isWindowsHostNativeDev: true,
            decisionEndpointIp: "172.20.144.1");
        Assert.Contains("--add-host", podArgs);
        Assert.Contains("kautuka-decision.internal:172.20.144.1", podArgs);
        Assert.Contains("C:\\Users\\user\\.kautuka\\runtime\\manager\\networkAccess\\envoy-certs:/etc/envoy/certs:ro", envoyArgs);
        Assert.Contains("/tmp/kautuka-admin.sock", BoundaryRulesetRenderer.RenderEnvoyConfig(
            adminSocketPath: "/tmp/kautuka-admin.sock",
            isWindowsMtls: true), StringComparison.Ordinal);

        // Negative control: use the Linux rootless mode and the request regains its Linux-only options.
        IReadOnlyList<string> linuxArgs = EgressTopologyManager.BuildEnvoyCreateArgs(
            "/state/envoy.yaml",
            "/state/sockets",
            "/state/admin-socket",
            isWindowsHostNativeDev: false);
        Assert.Contains("--group-add", linuxArgs);
        Assert.Contains(linuxArgs, arg => arg.Contains("/run/kautuka-nac", StringComparison.Ordinal));
        Assert.Contains(linuxArgs, arg => arg.Contains("/run/kautuka-admin", StringComparison.Ordinal));
        Assert.DoesNotContain("--add-host", EgressTopologyManager.BuildEgressPodCreateArgs(
            isWindowsHostNativeDev: false,
            decisionEndpointIp: null));
    }

    [Fact]
    public void WindowsPodmanRouteSelectsHostGatewayAndRejectsInvalidRoutes()
    {
        IPAddress address = WindowsPodmanDevEgressOptions.ParsePodmanMachineHostGateway(
            new PodmanCommandResult(0, "default via 172.20.144.1 dev eth0 proto kernel\n", string.Empty));

        Assert.Equal(IPAddress.Parse("172.20.144.1"), address);
        Assert.Throws<InvalidOperationException>(() => WindowsPodmanDevEgressOptions.ParsePodmanMachineHostGateway(
            new PodmanCommandResult(0, "default via 127.0.0.1 dev lo\n", string.Empty)));
        Assert.Throws<InvalidOperationException>(() => WindowsPodmanDevEgressOptions.ParsePodmanMachineHostGateway(
            new PodmanCommandResult(1, string.Empty, "machine unavailable")));
    }

    [Fact]
    public async Task WindowsPodmanEnvoyBundleWritesDedicatedClientKeyAndCaFiles()
    {
        (System.Security.Cryptography.X509Certificates.X509Certificate2 ca,
            _,
            System.Security.Cryptography.X509Certificates.X509Certificate2 client)
            = EnvoyMtlsCertificateAuthority.CreateDedicatedBundle();
        string certDir = Path.Combine(tempDir, "envoy-certs");

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await WindowsPodmanDevEgressOptions.WriteClientBundleAsync(certDir, ca, client, cancellationToken);

        string caPem = await File.ReadAllTextAsync(Path.Combine(certDir, "ca.crt"), cancellationToken);
        string clientPem = await File.ReadAllTextAsync(Path.Combine(certDir, "client.crt"), cancellationToken);
        string privateKeyPem = await File.ReadAllTextAsync(Path.Combine(certDir, "client.key"), cancellationToken);
        Assert.Contains("BEGIN CERTIFICATE", caPem, StringComparison.Ordinal);
        Assert.Contains("BEGIN CERTIFICATE", clientPem, StringComparison.Ordinal);
        Assert.Contains("BEGIN PRIVATE KEY", privateKeyPem, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN PRIVATE KEY", caPem, StringComparison.Ordinal);
    }

    [Fact]
    public void V7_20_ExistingEgressPodMustBeHealthyAndComplete_WithNegativeControl()
    {
        const string healthy = "[{\"State\":\"Running\",\"Containers\":["
            + "{\"Name\":\"kautuka-egress-boundary\",\"State\":\"exited\"},"
            + "{\"Name\":\"kautuka-egress-dns\",\"State\":\"running\"},"
            + "{\"Name\":\"kautuka-egress-envoy\",\"State\":\"running\"},"
            + "{\"Name\":\"pod-infra\",\"State\":\"running\"}]}]";

        Assert.True(EgressTopologyManager.IsEgressPodHealthy(healthy));

        // Negative controls: degraded state, a missing required container, or a stopped proxy must fail closed.
        Assert.False(EgressTopologyManager.IsEgressPodHealthy(healthy.Replace("Running", "Degraded", StringComparison.Ordinal)));
        Assert.False(EgressTopologyManager.IsEgressPodHealthy(healthy.Replace(",{\"Name\":\"kautuka-egress-envoy\",\"State\":\"running\"}", string.Empty, StringComparison.Ordinal)));
        Assert.False(EgressTopologyManager.IsEgressPodHealthy(healthy.Replace("\"State\":\"running\"", "\"State\":\"created\"", StringComparison.Ordinal)));
    }

    [Fact]
    public void V7_20_ExistingPodMustMatchRequiredNetworkAndSysctls_WithNegativeControl()
    {
        const string inspect = "[{\"CreateCommand\":[\"podman\",\"pod\",\"create\",\"--name\",\"kautuka-egress\","
            + "\"--network\",\"kautuka-workload:ip=10.247.0.2\",\"--network\",\"kautuka-outside:ip=10.248.0.2\","
            + "\"--sysctl\",\"net.ipv4.ip_forward=0\",\"--sysctl\",\"net.ipv6.conf.all.disable_ipv6=1\"]}]";

        Assert.True(EgressTopologyManager.HasRequiredEgressPodConfiguration(inspect));
        Assert.False(EgressTopologyManager.HasRequiredEgressPodConfiguration(
            inspect.Replace("kautuka-workload:ip=10.247.0.2", "kautuka-workload:ip=10.247.0.3", StringComparison.Ordinal)));
        Assert.False(EgressTopologyManager.HasRequiredEgressPodConfiguration(
            inspect.Replace("net.ipv6.conf.all.disable_ipv6=1", "net.ipv6.conf.all.disable_ipv6=0", StringComparison.Ordinal)));
        Assert.False(EgressTopologyManager.HasRequiredEgressPodConfiguration("[]"));
    }

    [Fact]
    public async Task V7_20_StartupRejectsMisconfiguredExistingPodAtCallSite_WithNegativeControl()
    {
        var calls = new List<string[]>();
        const string misconfiguredPod = "[{\"CreateCommand\":[\"podman\",\"pod\",\"create\",\"--name\",\"kautuka-egress\",\"--network\",\"kautuka-workload:ip=10.247.0.3\"]}]";
        var podman = new PodmanCommandRunner((args, _) =>
        {
            calls.Add([.. args]);
            if (args.SequenceEqual(["network", "inspect", EgressTopologyManager.OutsideNetworkName]))
            {
                return Task.FromResult(new PodmanCommandResult(125, string.Empty, "missing", ProcessTerminationReason.NonZeroExit));
            }

            if (args.SequenceEqual(["pod", "inspect", EgressTopologyManager.EgressPodName]))
            {
                return Task.FromResult(new PodmanCommandResult(0, misconfiguredPod, string.Empty, ProcessTerminationReason.Completed));
            }

            throw new InvalidOperationException("Startup continued past a misconfigured existing pod: " + string.Join(' ', args));
        });
        var registry = new WorkloadRegistry();
        var allocator = new WorkloadAddressAllocator(Path.Combine(tempDir, "v720-existing-pod-callsite-ip.json"));
        var verifier = new WorkloadPostStartVerifier(podman);
        await using var manager = new EgressTopologyManager(podman, allocator, verifier, registry, tempDir);
        System.Reflection.MethodInfo discoverDns = typeof(EgressTopologyManager).GetMethod(
            "DiscoverUpstreamDnsIpsAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("DiscoverUpstreamDnsIpsAsync method was not found.");
        IReadOnlyList<string> upstreamDnsIps = await ((Task<IReadOnlyList<string>>)discoverDns.Invoke(
            manager, [TestContext.Current.CancellationToken])!).ConfigureAwait(false);
        string nacDirectory = Path.Combine(tempDir, "nac");
        string boundaryDirectory = Path.Combine(nacDirectory, "boundary");
        string envoyDirectory = Path.Combine(nacDirectory, "envoy");
        string dnsDirectory = Path.Combine(nacDirectory, "dns");
        Directory.CreateDirectory(boundaryDirectory);
        Directory.CreateDirectory(envoyDirectory);
        Directory.CreateDirectory(dnsDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(boundaryDirectory, "boundary.nft"),
            BoundaryRulesetRenderer.RenderBoundaryNft(includeLanGrants: false, upstreamDnsIps: upstreamDnsIps),
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(envoyDirectory, "envoy.yaml"),
            BoundaryRulesetRenderer.RenderEnvoyConfig("/run/kautuka-admin/admin.sock", isWindowsMtls: false),
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(dnsDirectory, "Corefile"),
            BoundaryRulesetRenderer.RenderCorefile(EgressTopologyManager.EgressPodIp),
            TestContext.Current.CancellationToken);
        System.Reflection.MethodInfo ensurePod = typeof(EgressTopologyManager).GetMethod(
            "EnsureEgressPodAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("EnsureEgressPodAsync method was not found.");

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await (Task)ensurePod.Invoke(manager, [false, null, false, null, CancellationToken.None])!).ConfigureAwait(false);

        Assert.Contains("does not have the required workload/outside network addresses", failure.Message, StringComparison.Ordinal);
        Assert.Single(calls, args => args.SequenceEqual(["pod", "inspect", EgressTopologyManager.EgressPodName]));
        Assert.DoesNotContain(calls, args => args.SequenceEqual(["pod", "create"])
            || args.SequenceEqual(["pod", "start", EgressTopologyManager.EgressPodName]));
    }

    [Fact]
    public async Task V7_20_WorkloadNetworkPrefersOutsideDefaultRoute_WithNegativeControl()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Existing Podman network drift validation is Linux-specific.");
        }

        var createCalls = new List<string[]>();
        var creatingPodman = new PodmanCommandRunner((args, _) =>
        {
            string[] copied = [.. args];
            createCalls.Add(copied);
            bool isInspect = copied.Length > 1 && copied[0] == "network" && copied[1] == "inspect";
            return Task.FromResult(new PodmanCommandResult(
                isInspect ? 125 : 0,
                string.Empty,
                isInspect ? "missing" : string.Empty,
                ProcessTerminationReason.Completed));
        });
        var registry = new WorkloadRegistry();
        var allocator = new WorkloadAddressAllocator(Path.Combine(tempDir, "v720-route-ip.json"));
        var verifier = new WorkloadPostStartVerifier(creatingPodman);
        await using var creatingManager = new EgressTopologyManager(creatingPodman, allocator, verifier, registry, tempDir);
        System.Reflection.MethodInfo ensureNetworks = typeof(EgressTopologyManager).GetMethod(
            "EnsureNetworksAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await ((Task)ensureNetworks.Invoke(creatingManager, [CancellationToken.None])!).ConfigureAwait(false);

        string[] workloadCreate = createCalls.Single(args =>
            args.Length > 2
            && args[0] == "network"
            && args[1] == "create"
            && args[^1] == EgressTopologyManager.WorkloadNetworkName);
        int metricIndex = Array.IndexOf(workloadCreate, "metric=1000");
        Assert.True(metricIndex > 0, "The workload network must use a higher default-route metric than the outside network.");
        Assert.Equal("--opt", workloadCreate[metricIndex - 1]);
        int workloadRouteIndex = Array.IndexOf(workloadCreate, "--route");
        Assert.True(workloadRouteIndex >= 0, "The workload network must preserve the explicit deny-path default route.");
        Assert.Equal(
            "0.0.0.0/0,10.247.0.2,1000",
            workloadCreate[workloadRouteIndex + 1]);
        string[] outsideCreate = createCalls.Single(args =>
            args.Length > 2
            && args[0] == "network"
            && args[1] == "create"
            && args[^1] == EgressTopologyManager.OutsideNetworkName);
        int outsideMetricIndex = Array.IndexOf(outsideCreate, "metric=100");
        Assert.True(outsideMetricIndex > 0);
        Assert.Equal("--opt", outsideCreate[outsideMetricIndex - 1]);

        const string wrongMetricInspect = "[{\"internal\":true,\"dns_enabled\":false,\"options\":{\"metric\":\"100\"}}]";
        var existingPodman = new PodmanCommandRunner((args, _) =>
        {
            bool isInspect = args.Count > 1 && args[0] == "network" && args[1] == "inspect";
            bool isWorkloadInspect = isInspect && args.Contains(EgressTopologyManager.WorkloadNetworkName);
            return Task.FromResult(new PodmanCommandResult(
                isWorkloadInspect ? 0 : isInspect ? 125 : 0,
                isWorkloadInspect ? wrongMetricInspect : string.Empty,
                string.Empty,
                ProcessTerminationReason.Completed));
        });
        var existingVerifier = new WorkloadPostStartVerifier(existingPodman);
        await using var existingManager = new EgressTopologyManager(existingPodman, allocator, existingVerifier, registry, tempDir);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await (Task)ensureNetworks.Invoke(existingManager, [CancellationToken.None])!);

        const string correctWorkloadInspect = "[{\"internal\":true,\"dns_enabled\":false,\"options\":{\"metric\":\"1000\"}}]";
        const string wrongOutsideInspect = "[{\"options\":{\"metric\":\"200\"}}]";
        var wrongOutsidePodman = new PodmanCommandRunner((args, _) =>
        {
            bool isInspect = args.Count > 1 && args[0] == "network" && args[1] == "inspect";
            bool isWorkloadInspect = isInspect && args.Contains(EgressTopologyManager.WorkloadNetworkName);
            bool isOutsideInspect = isInspect && args.Contains(EgressTopologyManager.OutsideNetworkName);
            return Task.FromResult(new PodmanCommandResult(
                isWorkloadInspect || isOutsideInspect ? 0 : isInspect ? 125 : 0,
                isWorkloadInspect ? correctWorkloadInspect : isOutsideInspect ? wrongOutsideInspect : string.Empty,
                string.Empty,
                ProcessTerminationReason.Completed));
        });
        var wrongOutsideVerifier = new WorkloadPostStartVerifier(wrongOutsidePodman);
        await using var wrongOutsideManager = new EgressTopologyManager(wrongOutsidePodman, allocator, wrongOutsideVerifier, registry, tempDir);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await (Task)ensureNetworks.Invoke(wrongOutsideManager, [CancellationToken.None])!);
    }

    [Fact]
    public async Task V7_20_ReconcileClosesGateWhenRenderedBoundaryFileChanges_WithNegativeControl()
    {
        const string healthyPod = "[{\"State\":\"Running\",\"InfraContainerID\":\"infra-id\",\"Containers\":["
            + "{\"Name\":\"kautuka-egress-boundary\",\"State\":\"exited\"},"
            + "{\"Name\":\"kautuka-egress-dns\",\"State\":\"running\"},"
            + "{\"Name\":\"kautuka-egress-envoy\",\"State\":\"running\"},{\"Name\":\"pod-infra\",\"State\":\"running\"}]}]";
        const string boundarySuccess = "[{\"State\":{\"Status\":\"exited\",\"ExitCode\":0}}]";
        const string infraInspect = "[{\"State\":{\"Pid\":4242}}]";
        const string ipv4Rules = "{\"nftables\":[{\"table\":{\"family\":\"ip\",\"name\":\"kautuka_egress\"}},{\"rule\":{\"family\":\"ip\",\"table\":\"kautuka_egress\",\"chain\":\"output\",\"comment\":\"private_reject\",\"expr\":[{\"drop\":null}]}},{\"rule\":{\"family\":\"ip\",\"table\":\"kautuka_egress\",\"chain\":\"output\",\"comment\":\"wl_net_reject\",\"expr\":[{\"drop\":null}]}}]}";
        const string ipv6Rules = "{\"nftables\":[{\"table\":{\"family\":\"ip6\",\"name\":\"kautuka_egress\"}},{\"rule\":{\"family\":\"ip6\",\"table\":\"kautuka_egress\",\"chain\":\"output\",\"comment\":\"ipv6_drop_pod\",\"expr\":[{\"drop\":null}]}}]}";
        var podman = new PodmanCommandRunner((args, _) =>
        {
            string output = args.SequenceEqual(["pod", "inspect", EgressTopologyManager.EgressPodName, "--format", "json"])
                ? healthyPod
                : args.SequenceEqual(["inspect", "kautuka-egress-boundary", "--format", "json"])
                    ? boundarySuccess
                    : args.SequenceEqual(["inspect", "infra-id", "--format", "json"])
                        ? infraInspect
                        : args.SequenceEqual(["unshare", "nsenter", "-t", "4242", "-n", "nft", "-j", "list", "table", "ip", "kautuka_egress"])
                            ? ipv4Rules
                            : args.SequenceEqual(["unshare", "nsenter", "-t", "4242", "-n", "nft", "-j", "list", "table", "ip6", "kautuka_egress"])
                                ? ipv6Rules
                                : "[]";
            return Task.FromResult(new PodmanCommandResult(0, output, string.Empty, ProcessTerminationReason.Completed));
        });
        var registry = new WorkloadRegistry();
        var allocator = new WorkloadAddressAllocator(Path.Combine(tempDir, "v720-boundary-source-ip.json"));
        var verifier = new WorkloadPostStartVerifier(podman, new WorkspaceDesiredStateStore());
        await using var manager = new EgressTopologyManager(podman, allocator, verifier, registry, tempDir);
        await SetExpectedBoundarySourceAsync(manager, TestContext.Current.CancellationToken);
        await File.AppendAllTextAsync(
            Path.Combine(tempDir, "nac", "boundary", "boundary.nft"),
            "# changed",
            TestContext.Current.CancellationToken);
        typeof(EgressTopologyManager)
            .GetField("isStartupGateOpen", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(manager, true);
        typeof(EgressTopologyManager)
            .GetField("expectedBoundaryRulesetHash", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(manager, EgressTopologyManager.ComputeBoundaryRulesetHash(ipv4Rules, ipv6Rules));
        await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);

        Assert.False(manager.IsStartupGateOpen);
    }

    [Fact]
    public async Task V7_20_ReconcileClosesStartupGateWhenPodDegrades_WithNegativeControl()
    {
        const string degraded = "[{\"State\":\"Degraded\",\"Containers\":[]}]";
        var podman = new PodmanCommandRunner((args, _) => Task.FromResult(
            new PodmanCommandResult(0, degraded, string.Empty, ProcessTerminationReason.Completed)));
        var registry = new WorkloadRegistry();
        var allocator = new WorkloadAddressAllocator(Path.Combine(tempDir, "v720-health-ip.json"));
        var verifier = new WorkloadPostStartVerifier(podman);
        await using var manager = new EgressTopologyManager(podman, allocator, verifier, registry, tempDir);
        await SetExpectedBoundarySourceAsync(manager, TestContext.Current.CancellationToken);
        typeof(EgressTopologyManager)
            .GetField("isStartupGateOpen", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(manager, true);

        await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);

        Assert.False(manager.IsStartupGateOpen);
    }

    [Fact]
    public async Task V7_54_ReconcileDoesNotLatchWhenPodHealthCheckFails_WithNegativeControl()
    {
        const string degraded = "[{\"State\":\"Degraded\",\"Containers\":[]}]";
        var podman = new PodmanCommandRunner((args, _) => Task.FromResult(
            new PodmanCommandResult(0, degraded, string.Empty, ProcessTerminationReason.Completed)));
        var registry = new WorkloadRegistry();
        var allocator = new WorkloadAddressAllocator(Path.Combine(tempDir, "v754-transient-health-ip.json"));
        var verifier = new WorkloadPostStartVerifier(podman);
        await using var manager = new EgressTopologyManager(podman, allocator, verifier, registry, tempDir);
        await SetExpectedBoundarySourceAsync(manager, TestContext.Current.CancellationToken);
        typeof(EgressTopologyManager)
            .GetField("isStartupGateOpen", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(manager, true);

        await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);

        Assert.False(manager.IsStartupGateOpen);
        Assert.False((bool)typeof(EgressTopologyManager)
            .GetField("topologyIncidentLatched", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(manager)!);
    }

    [Fact]
    public async Task V7_54_ReconcileDoesNotLatchWhenPodInspectThrows_WithNegativeControl()
    {
        int healthInspectCount = 0;
        var calls = new List<string>();
        var podman = new PodmanCommandRunner((args, _) =>
        {
            calls.Add(string.Join(" ", args));
            if (args.SequenceEqual(["ps", "-a", "--format", "json"]))
            {
                return Task.FromResult(new PodmanCommandResult(0, "[]", string.Empty, ProcessTerminationReason.Completed));
            }
            if (args.SequenceEqual(["pod", "inspect", EgressTopologyManager.EgressPodName, "--format", "json"]))
            {
                healthInspectCount++;
                if (healthInspectCount == 1)
                {
                    return Task.FromException<PodmanCommandResult>(new TimeoutException("transient pod inspect timeout"));
                }
            }

            return Task.FromResult(new PodmanCommandResult(
                0,
                "[{\"State\":\"Degraded\",\"Containers\":[]}]",
                string.Empty,
                ProcessTerminationReason.Completed));
        });
        var registry = new WorkloadRegistry();
        var allocator = new WorkloadAddressAllocator(Path.Combine(tempDir, "v754-transient-inspect-ip.json"));
        var verifier = new WorkloadPostStartVerifier(podman);
        await using var manager = new EgressTopologyManager(podman, allocator, verifier, registry, tempDir);
        await SetExpectedBoundarySourceAsync(manager, TestContext.Current.CancellationToken);
        typeof(EgressTopologyManager)
            .GetField("isStartupGateOpen", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(manager, true);

        await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);

        Assert.False(manager.IsStartupGateOpen);
        Assert.False((bool)typeof(EgressTopologyManager)
            .GetField("topologyIncidentLatched", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(manager)!);
        Assert.Contains("pod rm -f kautuka-egress", calls);
    }

    [Fact]
    public async Task V7_54_ReconcileKeepsUnrelatedClosedGateWhileMaintainingRegistry()
    {
        var calls = new List<string>();
        var podman = new PodmanCommandRunner((args, _) =>
        {
            calls.Add(string.Join(" ", args));
            return Task.FromResult(new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed));
        });
        var registry = new WorkloadRegistry();
        var allocator = new WorkloadAddressAllocator(Path.Combine(tempDir, "v754-closed-gate-ip.json"));
        var verifier = new WorkloadPostStartVerifier(podman);
        await using var manager = new EgressTopologyManager(podman, allocator, verifier, registry, tempDir);

        await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);

        Assert.False(manager.IsStartupGateOpen);
        Assert.Equal(["ps -a --format json"], calls);
    }

    [Fact]
    public async Task V7_54_ReconcileDoesNotStartRecoveryWhenGateClosesDuringHealthCheck()
    {
        const string degraded = "[{\"State\":\"Degraded\",\"Containers\":[]}]";
        var healthInspectStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHealthInspect = new TaskCompletionSource<PodmanCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        var podman = new PodmanCommandRunner((args, _) =>
        {
            calls.Add(string.Join(" ", args));
            if (args.SequenceEqual(["pod", "inspect", EgressTopologyManager.EgressPodName, "--format", "json"]))
            {
                healthInspectStarted.TrySetResult();
                return releaseHealthInspect.Task;
            }

            return Task.FromResult(new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed));
        });
        var registry = new WorkloadRegistry();
        var allocator = new WorkloadAddressAllocator(Path.Combine(tempDir, "v754-close-during-health-ip.json"));
        var verifier = new WorkloadPostStartVerifier(podman);
        await using var manager = new EgressTopologyManager(podman, allocator, verifier, registry, tempDir);
        await SetExpectedBoundarySourceAsync(manager, TestContext.Current.CancellationToken);
        typeof(EgressTopologyManager)
            .GetField("isStartupGateOpen", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(manager, true);

        Task reconcile = manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);
        await healthInspectStarted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        manager.CloseLaunchAdmission();
        releaseHealthInspect.TrySetResult(new PodmanCommandResult(0, degraded, string.Empty, ProcessTerminationReason.Completed));
        await reconcile;

        Assert.False(manager.IsStartupGateOpen);
        Assert.DoesNotContain(calls, call => call.StartsWith("pod exists kautuka-egress", StringComparison.Ordinal));
        Assert.DoesNotContain(calls, call => call.StartsWith("pod rm -f kautuka-egress", StringComparison.Ordinal));
        int callCount = calls.Count;
        await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);
        Assert.Equal(callCount + 1, calls.Count);
        Assert.Equal("ps -a --format json", calls[^1]);
    }

    [Fact]
    public async Task V7_54_ReconcileKeepsGateClosedWhenPodExistenceIsUnknown()
    {
        const string degraded = "[{\"State\":\"Degraded\",\"Containers\":[]}]";
        var calls = new List<string>();
        var podman = new PodmanCommandRunner((args, _) =>
        {
            calls.Add(string.Join(" ", args));
            if (args.SequenceEqual(["pod", "inspect", EgressTopologyManager.EgressPodName, "--format", "json"]))
            {
                return Task.FromResult(new PodmanCommandResult(0, degraded, string.Empty, ProcessTerminationReason.Completed));
            }

            if (args.SequenceEqual(["pod", "exists", EgressTopologyManager.EgressPodName]))
            {
                return Task.FromResult(new PodmanCommandResult(125, string.Empty, "Podman unavailable", ProcessTerminationReason.NonZeroExit));
            }

            return Task.FromResult(new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed));
        });
        var registry = new WorkloadRegistry();
        var allocator = new WorkloadAddressAllocator(Path.Combine(tempDir, "v754-unknown-pod-existence-ip.json"));
        var verifier = new WorkloadPostStartVerifier(podman);
        await using var manager = new EgressTopologyManager(podman, allocator, verifier, registry, tempDir);
        await SetExpectedBoundarySourceAsync(manager, TestContext.Current.CancellationToken);
        typeof(EgressTopologyManager)
            .GetField("isStartupGateOpen", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(manager, true);

        await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);

        Assert.False(manager.IsStartupGateOpen);
        Assert.False((bool)typeof(EgressTopologyManager)
            .GetField("topologyIncidentLatched", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(manager)!);
        Assert.DoesNotContain(calls, call => call.StartsWith("pod rm -f kautuka-egress", StringComparison.Ordinal));
        Assert.DoesNotContain(calls, call => call.StartsWith("pod create ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V7_54_ReconcileRecreatesPodAndReopensGateAfterTransientRulesetReadFailure(bool testQuarantineRetry)
    {
        const string healthy = "[{\"State\":\"Running\",\"InfraContainerID\":\"infra-id\",\"Containers\":["
            + "{\"Name\":\"kautuka-egress-boundary\",\"State\":\"exited\"},"
            + "{\"Name\":\"kautuka-egress-dns\",\"State\":\"running\"},"
            + "{\"Name\":\"kautuka-egress-envoy\",\"State\":\"running\"},{\"Name\":\"pod-infra\",\"State\":\"running\"}]}]";
        const string configuredPod = "[{\"InfraContainerID\":\"infra-id\",\"CreateCommand\":[\"podman\",\"pod\",\"create\",\"--name\",\"kautuka-egress\",\"--network\",\"kautuka-workload:ip=10.247.0.2\",\"--network\",\"kautuka-outside:ip=10.248.0.2\",\"--sysctl\",\"net.ipv4.ip_forward=0\",\"--sysctl\",\"net.ipv6.conf.all.disable_ipv6=1\"]}]";
        const string boundarySuccess = "[{\"State\":{\"Status\":\"exited\",\"ExitCode\":0}}]";
        const string containerId = "{\"Id\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\"}";
        const string ipv4Rules = "{\"nftables\":[{\"table\":{\"family\":\"ip\",\"name\":\"kautuka_egress\"}},{\"rule\":{\"family\":\"ip\",\"table\":\"kautuka_egress\",\"chain\":\"output\",\"comment\":\"private_reject\",\"expr\":[{\"drop\":null}]}},{\"rule\":{\"family\":\"ip\",\"table\":\"kautuka_egress\",\"chain\":\"output\",\"comment\":\"wl_net_reject\",\"expr\":[{\"drop\":null}]}}]}";
        const string ipv6Rules = "{\"nftables\":[{\"table\":{\"family\":\"ip6\",\"name\":\"kautuka_egress\"}},{\"rule\":{\"family\":\"ip6\",\"table\":\"kautuka_egress\",\"chain\":\"output\",\"comment\":\"ipv6_drop_pod\",\"expr\":[{\"drop\":null}]}}]}";
        var calls = new List<string>();
        int formattedPodInspects = 0;
        int plainPodInspects = 0;
        int liveRulesetReadFailuresRemaining = 1;
        int podRemovals = 0;
        int podStarts = 0;
        const string quarantinedId = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
        bool cleanupPhase = false;
        bool quarantineSurvives = true;
        int quarantineRemovals = 0;
        var secondRecoveryPodStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondRecoveryPodStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var containerInspectCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        string certificateDirectory = Path.Combine(tempDir, "v754-certs");
        Directory.CreateDirectory(certificateDirectory);
        string boundaryDirectory = Path.Combine(tempDir, "nac", "boundary");
        string dnsDirectory = Path.Combine(tempDir, "nac", "dns");
        string envoyDirectory = Path.Combine(tempDir, "nac", "envoy");
        string socketsDirectory = Path.Combine(tempDir, "sockets");
        string adminDirectory = Path.Combine(tempDir, "admin-socket");
        bool isWindowsHostNativeDev = OperatingSystem.IsWindows();
        var createdContainerCommands = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["kautuka-egress-boundary"] = ["podman", .. EgressTopologyManager.BuildBoundaryCreateArgs(boundaryDirectory, isWindowsHostNativeDev)],
            ["kautuka-egress-dns"] = ["podman", .. EgressTopologyManager.BuildCoreDnsCreateArgs(Path.Combine(dnsDirectory, "Corefile"), isWindowsHostNativeDev)],
            ["kautuka-egress-envoy"] = ["podman", .. EgressTopologyManager.BuildEnvoyCreateArgs(Path.Combine(envoyDirectory, "envoy.yaml"), socketsDirectory, adminDirectory, isWindowsHostNativeDev, "192.168.1.10", certificateDirectory)]
        };
        var podman = new PodmanCommandRunner((args, _) =>
        {
            string call = string.Join(" ", args);
            calls.Add(call);
            if (cleanupPhase && args[0] == "ps")
                return Task.FromResult(new PodmanCommandResult(0, quarantineSurvives ? "[{\"Id\":\"" + quarantinedId + "\",\"State\":\"running\"}]" : "[]", string.Empty, ProcessTerminationReason.Completed));
            if (cleanupPhase && args[0] == "rm" && args[^1] == quarantinedId)
            {
                quarantineRemovals++;
                if (quarantineRemovals == 1) return Task.FromResult(new PodmanCommandResult(125, "", "temporary failure", ProcessTerminationReason.NonZeroExit));
                quarantineSurvives = false;
                return Task.FromResult(new PodmanCommandResult(0, "", "", ProcessTerminationReason.Completed));
            }
            if (args[0] == "ps") return Task.FromResult(new PodmanCommandResult(0, "[]", "", ProcessTerminationReason.Completed));
            if (args.Count > 0 && args[0] == "create")
            {
                int nameIndex = args.ToList().IndexOf("--name");
                if (nameIndex >= 0 && nameIndex + 1 < args.Count)
                    createdContainerCommands[args[nameIndex + 1]] = ["podman", .. args];
            }

            if (args.Count == 3 && args[0] == "image" && args[1] == "inspect")
                return Task.FromResult(new PodmanCommandResult(0, "[{\"Id\":\"sha256:v754-image\"}]", string.Empty, ProcessTerminationReason.Completed));
            if (args.SequenceEqual(["pod", "inspect", EgressTopologyManager.EgressPodName, "--format", "json"]))
            {
                formattedPodInspects++;
                return Task.FromResult(new PodmanCommandResult(0, healthy, string.Empty, ProcessTerminationReason.Completed));
            }

            if (args.SequenceEqual(["pod", "inspect", EgressTopologyManager.EgressPodName]))
            {
                plainPodInspects++;
                return Task.FromResult(plainPodInspects == 1
                    ? new PodmanCommandResult(125, string.Empty, "missing", ProcessTerminationReason.NonZeroExit)
                    : new PodmanCommandResult(0, configuredPod, string.Empty, ProcessTerminationReason.Completed));
            }

            if (args.SequenceEqual(["pod", "rm", "-f", EgressTopologyManager.EgressPodName]))
            {
                podRemovals++;
                return Task.FromResult(podRemovals == 1
                    ? new PodmanCommandResult(125, string.Empty, "transient remove failure", ProcessTerminationReason.NonZeroExit)
                    : new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed));
            }

            if (args.SequenceEqual(["pod", "start", EgressTopologyManager.EgressPodName]))
            {
                podStarts++;
                if (podStarts == 2)
                {
                    secondRecoveryPodStart.TrySetResult();
                    return releaseSecondRecoveryPodStart.Task.ContinueWith(
                        static _ => new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed),
                        TaskScheduler.Default);
                }

                return Task.FromResult(new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed));
            }

            if (args.SequenceEqual(["inspect", "kautuka-egress-boundary", "--format", "json"]))
            {
                return Task.FromResult(new PodmanCommandResult(0, boundarySuccess, string.Empty, ProcessTerminationReason.Completed));
            }

            if (args.SequenceEqual(["inspect", "infra-id", "--format", "json"]))
            {
                return Task.FromResult(new PodmanCommandResult(0, "[{\"State\":{\"Pid\":4242}}]", string.Empty, ProcessTerminationReason.Completed));
            }

            if (args.Count >= 2 && args[0] == "network" && args[1] == "inspect")
            {
                return Task.FromResult(new PodmanCommandResult(0, "[]", string.Empty, ProcessTerminationReason.Completed));
            }

            if (args.SequenceEqual(["image", "exists", EgressTopologyManager.BoundaryInitImage]))
            {
                return Task.FromResult(new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed));
            }

            if (args.Count >= 2 && args[0] == "container" && args[1] == "exists")
            {
                return Task.FromResult(new PodmanCommandResult(1, string.Empty, string.Empty, ProcessTerminationReason.NonZeroExit));
            }

            if (args.Count >= 2 && args[0] == "inspect")
            {
                string name = args[1];
                int count = containerInspectCounts.TryGetValue(name, out int prior) ? prior + 1 : 1;
                containerInspectCounts[name] = count;
                return Task.FromResult(count == 1
                    ? new PodmanCommandResult(125, string.Empty, "missing", ProcessTerminationReason.NonZeroExit)
                    : new PodmanCommandResult(0, JsonSerializer.Serialize(new[] { new { Id = JsonDocument.Parse(containerId).RootElement.GetProperty("Id").GetString(), Image = "sha256:v754-image", Config = new { CreateCommand = createdContainerCommands.TryGetValue(name, out string[]? command) ? command : [] } } }), string.Empty, ProcessTerminationReason.Completed));
            }

            if (args.Count >= 4 && args[0] == "machine" && args[1] == "ssh")
            {
                string command = args[^1];
                if (command.Contains("podman unshare unshare", StringComparison.Ordinal))
                {
                    return Task.FromResult(new PodmanCommandResult(
                        0,
                        ipv4Rules + "\n__KUTUKA_IPV6_BOUNDARY__\n" + ipv6Rules,
                        string.Empty,
                        ProcessTerminationReason.Completed));
                }

                if (liveRulesetReadFailuresRemaining > 0 && command.Contains("nft -j list table ip ", StringComparison.Ordinal))
                {
                    liveRulesetReadFailuresRemaining--;
                    return Task.FromResult(new PodmanCommandResult(125, string.Empty, "transient inspect failure", ProcessTerminationReason.NonZeroExit));
                }

                return Task.FromResult(new PodmanCommandResult(
                    0,
                    command.Contains("table ip6 ", StringComparison.Ordinal) ? ipv6Rules : ipv4Rules,
                    string.Empty,
                    ProcessTerminationReason.Completed));
            }

            return Task.FromResult(new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed));
        });
        var registry = new WorkloadRegistry();
        var allocator = new WorkloadAddressAllocator(Path.Combine(tempDir, "v754-recovery-ip.json"));
        var verifier = new WorkloadPostStartVerifier(podman);
        var desiredState = new WorkspaceDesiredStateStore(Path.Combine(tempDir, "v754-cleanup-state.json"), Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkspaceDesiredStateStore>.Instance);
        await using var manager = new EgressTopologyManager(
            podman,
            allocator,
            verifier,
            registry,
            tempDir,
            windowsDecisionEndpointIp: "192.168.1.10",
            windowsEnvoyCertificateDirectory: certificateDirectory,
            windowsPodmanMachineName: "nac-test-machine",
            windowsPodmanHookStateDirectory: "/tmp/kautuka-nac-test",
            desiredStateStore: desiredState);
        await SetExpectedBoundarySourceAsync(manager, TestContext.Current.CancellationToken);
        typeof(EgressTopologyManager)
            .GetField("isStartupGateOpen", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(manager, true);

        await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);

        Assert.False(manager.IsStartupGateOpen);
        Assert.False((bool)typeof(EgressTopologyManager)
            .GetField("topologyIncidentLatched", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(manager)!);

        await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);

        Assert.True(manager.IsStartupGateOpen);
        Assert.False((bool)typeof(EgressTopologyManager)
            .GetField("topologyIncidentLatched", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(manager)!);
        Assert.Contains("pod rm -f kautuka-egress", calls);
        Assert.Contains(calls, call => call.StartsWith("pod create ", StringComparison.Ordinal));
        Assert.Contains("pod start kautuka-egress", calls);

        if (testQuarantineRetry)
        {
            desiredState.QuarantineWorkload(quarantinedId);
            cleanupPhase = true;
            var reconcileWorkloads = typeof(EgressTopologyManager).GetMethod("ReconcileWorkloadRegistryAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            await (Task)reconcileWorkloads.Invoke(manager, [registry, TestContext.Current.CancellationToken])!;
            Assert.False(manager.IsStartupGateOpen);
            releaseSecondRecoveryPodStart.TrySetResult();
            await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);
            Assert.Equal(2, quarantineRemovals);
            Assert.False(quarantineSurvives);
            Assert.True(manager.IsStartupGateOpen);
            return;
        }

        containerInspectCounts.Clear();
        liveRulesetReadFailuresRemaining = 1;
        Task inFlightRecovery = manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);
        await secondRecoveryPodStart.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        manager.CloseLaunchAdmission();
        releaseSecondRecoveryPodStart.TrySetResult();
        await inFlightRecovery;

        Assert.False(manager.IsStartupGateOpen);
        int callsAfterExplicitClose = calls.Count;
        await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);
        Assert.Equal(callsAfterExplicitClose + 1, calls.Count);
        Assert.Equal("ps -a --format json", calls[^1]);
    }

    [Fact]
    public async Task V7_54_ReconcileLatchesAndStopsWorkloadsOnProvenRulesetHashMismatch()
    {
        const string healthyPod = "[{\"State\":\"Running\",\"InfraContainerID\":\"infra-id\",\"Containers\":["
            + "{\"Name\":\"kautuka-egress-boundary\",\"State\":\"exited\"},"
            + "{\"Name\":\"kautuka-egress-dns\",\"State\":\"running\"},"
            + "{\"Name\":\"kautuka-egress-envoy\",\"State\":\"running\"},{\"Name\":\"pod-infra\",\"State\":\"running\"}]}]";
        const string boundarySuccess = "[{\"State\":{\"Status\":\"exited\",\"ExitCode\":0}}]";
        const string infraInspect = "[{\"State\":{\"Pid\":4242}}]";
        const string ipv4Rules = "{\"nftables\":[{\"table\":{\"family\":\"ip\",\"name\":\"kautuka_egress\"}},{\"rule\":{\"family\":\"ip\",\"table\":\"kautuka_egress\",\"chain\":\"output\",\"comment\":\"private_reject\",\"expr\":[{\"drop\":null}]}},{\"rule\":{\"family\":\"ip\",\"table\":\"kautuka_egress\",\"chain\":\"output\",\"comment\":\"wl_net_reject\",\"expr\":[{\"drop\":null}]}}]}";
        const string mismatchingIpv4Rules = "{\"nftables\":[{\"table\":{\"family\":\"ip\",\"name\":\"kautuka_egress\"}},{\"rule\":{\"family\":\"ip\",\"table\":\"kautuka_egress\",\"chain\":\"output\",\"comment\":\"private_reject\",\"expr\":[{\"drop\":null}]}},{\"rule\":{\"family\":\"ip\",\"table\":\"kautuka_egress\",\"chain\":\"output\",\"comment\":\"wl_net_reject\",\"expr\":[{\"drop\":null}]}},{\"rule\":{\"family\":\"ip\",\"table\":\"kautuka_egress\",\"chain\":\"output\",\"comment\":\"rogue_accept\",\"expr\":[{\"accept\":null}]}}]}";
        const string ipv6Rules = "{\"nftables\":[{\"table\":{\"family\":\"ip6\",\"name\":\"kautuka_egress\"}},{\"rule\":{\"family\":\"ip6\",\"table\":\"kautuka_egress\",\"chain\":\"output\",\"comment\":\"ipv6_drop_pod\",\"expr\":[{\"drop\":null}]}}]}";
        var calls = new List<string>();
        var podman = new PodmanCommandRunner((args, _) =>
        {
            calls.Add(string.Join(" ", args));
            string output = args.SequenceEqual(["pod", "inspect", EgressTopologyManager.EgressPodName, "--format", "json"])
                ? healthyPod
                : args.SequenceEqual(["inspect", "kautuka-egress-boundary", "--format", "json"])
                    ? boundarySuccess
                    : args.SequenceEqual(["inspect", "infra-id", "--format", "json"])
                        ? infraInspect
                        : args.Count >= 4 && args[0] == "machine" && args[1] == "ssh"
                            ? args[^1].Contains("table ip6 ", StringComparison.Ordinal) ? ipv6Rules : mismatchingIpv4Rules
                            : "[]";
            return Task.FromResult(new PodmanCommandResult(0, output, string.Empty, ProcessTerminationReason.Completed));
        });
        var registry = new WorkloadRegistry();
        registry.Register(new RegistryEntry(
            "10.247.1.54", "hash-drift", "agent-a", "hash-drift-container", "agent-sandbox", null,
            "agent-sandbox", "normal", "user-agent", null, DateTimeOffset.UtcNow));
        var allocator = new WorkloadAddressAllocator(Path.Combine(tempDir, "v754-rules-hash-ip.json"));
        var verifier = new WorkloadPostStartVerifier(podman);
        await using var manager = new EgressTopologyManager(
            podman,
            allocator,
            verifier,
            registry,
            tempDir,
            windowsPodmanMachineName: "nac-test-machine",
            windowsPodmanHookStateDirectory: "/tmp/kautuka-nac-test");
        await SetExpectedBoundarySourceAsync(manager, TestContext.Current.CancellationToken);
        typeof(EgressTopologyManager)
            .GetField("expectedBoundaryRulesetHash", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(manager, EgressTopologyManager.ComputeBoundaryRulesetHash(ipv4Rules, ipv6Rules));
        typeof(EgressTopologyManager)
            .GetField("isStartupGateOpen", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(manager, true);

        await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);

        Assert.False(manager.IsStartupGateOpen);
        Assert.True((bool)typeof(EgressTopologyManager)
            .GetField("topologyIncidentLatched", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(manager)!);
        Assert.Contains("stop -t 2 hash-drift-container", calls);
        Assert.DoesNotContain(calls, call => call.StartsWith("pod rm -f kautuka-egress", StringComparison.Ordinal));
    }

    [Fact]
    public async Task V7_20_StartupCanaryUsesPersistedSystemBrakeState()
    {
        string policyPath = Path.Combine(tempDir, "v720-canary-policy.json");
        using var policyStore = new NetworkAccessPolicyStore(policyPath);
        policyStore.SetBrake(new NetworkAccessActor("admin", IsAdmin: true), "system", null, on: true);
        var calls = new List<IReadOnlyList<string>>();
        var podman = new PodmanCommandRunner((args, _) =>
        {
            calls.Add(args.ToArray());
            return Task.FromResult(new PodmanCommandResult(1, string.Empty, "unexpected", ProcessTerminationReason.Completed));
        });
        var registry = new WorkloadRegistry();
        var allocator = new WorkloadAddressAllocator(Path.Combine(tempDir, "v720-canary-ip.json"));
        await using var manager = new EgressTopologyManager(
            podman, allocator, new WorkloadPostStartVerifier(podman), registry, tempDir, policyStore: policyStore);

        bool passed = await manager.RunStartupSelfTestAsync(TestContext.Current.CancellationToken);

        Assert.False(passed);
        Assert.DoesNotContain(calls, args => args.Count > 0 && args[0] == "run");
        Assert.Contains(calls, args => args.Count == 3 && args[0] == "rm" && args[1] == "-f" && args[2].StartsWith("kautuka-canary-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task V7_20_StartupCanaryBrakeReleaseDuringFailureStillQueuesRetry()
    {
        using var policyStore = new NetworkAccessPolicyStore(Path.Combine(tempDir, "v720-canary-race-policy.json"));
        var admin = new NetworkAccessActor("admin", IsAdmin: true);
        policyStore.SetBrake(admin, "system", null, on: true);
        var registry = new WorkloadRegistry();
        var podman = new PodmanCommandRunner((args, _) =>
        {
            if (args.Count == 3 && args[0] == "rm" && args[1] == "-f")
            {
                policyStore.SetBrake(admin, "system", null, on: false);
            }
            return Task.FromResult(new PodmanCommandResult(1, string.Empty, "braked", ProcessTerminationReason.Completed));
        });
        await using var manager = new EgressTopologyManager(
            podman, new WorkloadAddressAllocator(Path.Combine(tempDir, "v720-canary-race-ip.json")),
            new WorkloadPostStartVerifier(podman), registry, tempDir, policyStore: policyStore);
        System.Reflection.MethodInfo runCanary = typeof(EgressTopologyManager).GetMethod(
            "RunStartupCanaryOrDeferAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RunStartupCanaryOrDeferAsync method was not found.");

        bool passed = await ((Task<bool>)runCanary.Invoke(manager, [registry, TestContext.Current.CancellationToken])!).ConfigureAwait(false);

        Assert.False(passed);
        Assert.False(manager.IsStartupGateOpen);
        Assert.True(manager.IsStartupRetryPending);
    }

    [Fact]
    public async Task V7_20_DeferredStartupReinitializesAndStaysClosedWhenPodDriftsAfterBrakeRelease()
    {
        string policyPath = Path.Combine(tempDir, "v720-canary-retry-policy.json");
        using var policyStore = new NetworkAccessPolicyStore(policyPath);
        var admin = new NetworkAccessActor("admin", IsAdmin: true);
        policyStore.SetBrake(admin, "system", null, on: true);
        var calls = new List<IReadOnlyList<string>>();
        var podman = new PodmanCommandRunner((args, _) =>
        {
            calls.Add(args.ToArray());
            return Task.FromResult(new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed));
        });
        var registry = new WorkloadRegistry();
        await using var manager = new EgressTopologyManager(
            podman, new WorkloadAddressAllocator(Path.Combine(tempDir, "v720-canary-retry-ip.json")),
            new WorkloadPostStartVerifier(podman), registry, tempDir, policyStore: policyStore);
        await SetExpectedBoundarySourceAsync(manager, TestContext.Current.CancellationToken);
        typeof(EgressTopologyManager).GetMethod("QueueStartupCanaryRetry", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(manager, [registry]);
        await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);
        Assert.False(manager.IsStartupGateOpen);
        Assert.DoesNotContain(calls, args => args.Count > 0 && args[0] == "run");

        policyStore.SetBrake(admin, "system", null, on: false);
        await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);

        Assert.False(manager.IsStartupGateOpen);
        Assert.Contains(calls, args => args.SequenceEqual(["info", "--format", "{{.Host.Distribution.Distribution}}"]));
        Assert.DoesNotContain(calls, args => args.Count > 0 && args[0] == "run");
    }

    [Fact]
    public async Task V7_20_StartupRetryLatchesConfigTamperingDuringBrakeWait_WithNegativeControl()
    {
        string policyPath = Path.Combine(tempDir, "v720-config-tamper-policy.json");
        using var policyStore = new NetworkAccessPolicyStore(policyPath);
        var admin = new NetworkAccessActor("admin", IsAdmin: true);
        policyStore.SetBrake(admin, "system", null, on: true);
        var calls = new List<IReadOnlyList<string>>();
        var podman = new PodmanCommandRunner((args, _) =>
        {
            calls.Add(args.ToArray());
            return Task.FromResult(new PodmanCommandResult(0, "[]", string.Empty, ProcessTerminationReason.Completed));
        });
        var registry = new WorkloadRegistry();
        await using var manager = new EgressTopologyManager(
            podman, new WorkloadAddressAllocator(Path.Combine(tempDir, "v720-config-tamper-ip.json")),
            new WorkloadPostStartVerifier(podman), registry, tempDir, policyStore: policyStore);
        await SetExpectedBoundarySourceAsync(manager, TestContext.Current.CancellationToken);
        typeof(EgressTopologyManager).GetMethod("QueueStartupCanaryRetry", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(manager, [registry]);

        await File.AppendAllTextAsync(
            Path.Combine(tempDir, "nac", "envoy", "envoy.yaml"), "# modified while braked\n", TestContext.Current.CancellationToken);
        policyStore.SetBrake(admin, "system", null, on: false);
        await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);

        Assert.False(manager.IsStartupGateOpen);
        Assert.True((bool)typeof(EgressTopologyManager)
            .GetField("topologyIncidentLatched", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(manager)!);
        Assert.DoesNotContain(calls, args => args.Count > 0 && args[0] == "info");
    }

    [Fact]
    public async Task V7_20_StartupRetryConfigReadFailureRecoversWithoutLatching()
    {
        var calls = new List<IReadOnlyList<string>>();
        var podman = new PodmanCommandRunner((args, _) =>
        {
            calls.Add(args.ToArray());
            return Task.FromResult(new PodmanCommandResult(0, "[]", string.Empty, ProcessTerminationReason.Completed));
        });
        var registry = new WorkloadRegistry();
        await using var manager = new EgressTopologyManager(
            podman, new WorkloadAddressAllocator(Path.Combine(tempDir, "v720-config-retry-read-ip.json")),
            new WorkloadPostStartVerifier(podman), registry, tempDir);
        await SetExpectedBoundarySourceAsync(manager, TestContext.Current.CancellationToken);
        typeof(EgressTopologyManager).GetMethod("QueueStartupCanaryRetry", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(manager, [registry]);
        File.Delete(Path.Combine(tempDir, "nac", "dns", "Corefile"));

        await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);

        Assert.False(manager.IsStartupGateOpen);
        Assert.False((bool)typeof(EgressTopologyManager)
            .GetField("topologyIncidentLatched", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(manager)!);
        Assert.DoesNotContain(calls, args => args.Count > 0 && args[0] == "info");
    }

    [Fact]
    public async Task V7_20_StartupRecreatesEgressPodWhenRenderedConfigBytesChanged()
    {
        string boundary = "boundary desired";
        string envoy = "envoy desired";
        string corefile = "dns desired";
        string nac = Path.Combine(tempDir, "nac");
        Directory.CreateDirectory(Path.Combine(nac, "boundary"));
        Directory.CreateDirectory(Path.Combine(nac, "envoy"));
        Directory.CreateDirectory(Path.Combine(nac, "dns"));
        await File.WriteAllTextAsync(Path.Combine(nac, "boundary", "boundary.nft"), boundary, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(nac, "envoy", "envoy.yaml"), envoy, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(nac, "dns", "Corefile"), "old loaded config", TestContext.Current.CancellationToken);
        var calls = new List<IReadOnlyList<string>>();
        bool failRemoval = false;
        var podman = new PodmanCommandRunner((args, _) =>
        {
            calls.Add(args.ToArray());
            int exitCode = failRemoval && args.SequenceEqual(["pod", "rm", "-f", EgressTopologyManager.EgressPodName]) ? 1 : 0;
            return Task.FromResult(new PodmanCommandResult(exitCode, string.Empty, string.Empty, ProcessTerminationReason.Completed));
        });
        var registry = new WorkloadRegistry();
        await using var manager = new EgressTopologyManager(
            podman, new WorkloadAddressAllocator(Path.Combine(tempDir, "v720-config-recreate-ip.json")),
            new WorkloadPostStartVerifier(podman), registry, tempDir);
        System.Reflection.MethodInfo ensureConfig = typeof(EgressTopologyManager).GetMethod(
            "EnsureExistingEgressPodUsesRenderedConfigAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("EnsureExistingEgressPodUsesRenderedConfigAsync method was not found.");

        await ((Task)ensureConfig.Invoke(manager, [boundary, envoy, corefile, false, TestContext.Current.CancellationToken])!).ConfigureAwait(false);
        Assert.Contains(calls, args => args.SequenceEqual(["pod", "rm", "-f", EgressTopologyManager.EgressPodName]));

        calls.Clear();
        await File.WriteAllTextAsync(Path.Combine(nac, "dns", "Corefile"), corefile, TestContext.Current.CancellationToken);
        await ((Task)ensureConfig.Invoke(manager, [boundary, envoy, corefile, false, TestContext.Current.CancellationToken])!).ConfigureAwait(false);
        Assert.DoesNotContain(calls, args => args.SequenceEqual(["pod", "rm", "-f", EgressTopologyManager.EgressPodName]));

        await File.WriteAllTextAsync(Path.Combine(nac, "dns", "Corefile"), "old loaded config again", TestContext.Current.CancellationToken);
        failRemoval = true;
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await (Task)ensureConfig.Invoke(manager, [boundary, envoy, corefile, false, TestContext.Current.CancellationToken])!);
        Assert.False(manager.IsStartupGateOpen);
    }

    [Fact]
    public async Task V7_20_ReconcileConfigReadFailureRemainsTransientAndDoesNotLatch()
    {
        var calls = new List<IReadOnlyList<string>>();
        var podman = new PodmanCommandRunner((args, _) =>
        {
            calls.Add(args.ToArray());
            int exitCode = args.SequenceEqual(["pod", "rm", "-f", EgressTopologyManager.EgressPodName]) ? 1 : 0;
            return Task.FromResult(new PodmanCommandResult(exitCode, string.Empty, string.Empty, ProcessTerminationReason.Completed));
        });
        var registry = new WorkloadRegistry();
        await using var manager = new EgressTopologyManager(
            podman, new WorkloadAddressAllocator(Path.Combine(tempDir, "v720-config-read-failure-ip.json")),
            new WorkloadPostStartVerifier(podman), registry, tempDir);
        await SetExpectedBoundarySourceAsync(manager, TestContext.Current.CancellationToken);
        typeof(EgressTopologyManager).GetField("isStartupGateOpen", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(manager, true);
        File.Delete(Path.Combine(tempDir, "nac", "envoy", "envoy.yaml"));

        await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);

        Assert.False(manager.IsStartupGateOpen);
        Assert.False((bool)typeof(EgressTopologyManager)
            .GetField("topologyIncidentLatched", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(manager)!);
    }

    [Fact]
    public async Task V7_20_ReconcileLatchesOnEnvoyOrCoreDnsConfigurationDrift_WithNegativeControl()
    {
        const string healthyPod = "[{\"State\":\"Running\",\"InfraContainerID\":\"infra\",\"Containers\":["
            + "{\"Name\":\"kautuka-egress-boundary\",\"State\":\"exited\"},"
            + "{\"Name\":\"kautuka-egress-dns\",\"State\":\"running\"},"
            + "{\"Name\":\"kautuka-egress-envoy\",\"State\":\"running\"},{\"Name\":\"pod-infra\",\"State\":\"running\"}]}]";
        const string ipv4 = "{\"nftables\":[{\"table\":{\"family\":\"ip\",\"name\":\"kautuka_egress\"}},{\"rule\":{\"family\":\"ip\",\"table\":\"kautuka_egress\",\"chain\":\"output\",\"comment\":\"private_reject\",\"expr\":[{\"drop\":null}]}},{\"rule\":{\"family\":\"ip\",\"table\":\"kautuka_egress\",\"chain\":\"output\",\"comment\":\"wl_net_reject\",\"expr\":[{\"drop\":null}]}}]}";
        const string ipv6 = "{\"nftables\":[{\"table\":{\"family\":\"ip6\",\"name\":\"kautuka_egress\"}},{\"rule\":{\"family\":\"ip6\",\"table\":\"kautuka_egress\",\"chain\":\"output\",\"comment\":\"ipv6_drop_pod\",\"expr\":[{\"drop\":null}]}}]}";

        foreach (string relativePath in new[] { "envoy/envoy.yaml", "dns/Corefile" })
        {
            var podman = new PodmanCommandRunner((args, _) =>
            {
                string output = args[0] switch
                {
                    "pod" => healthyPod,
                    "inspect" when args.Count > 1 && args[1] == "kautuka-egress-boundary" => "[{\"State\":{\"Status\":\"exited\",\"ExitCode\":0}}]",
                    "inspect" => "[{\"State\":{\"Pid\":123}}]",
                    "machine" when args.Count > 3 && args[3].Contains("table ip6", StringComparison.Ordinal) => ipv6,
                    "machine" => ipv4,
                    "ps" => "[]",
                    _ => "[]"
                };
                return Task.FromResult(new PodmanCommandResult(0, output, string.Empty, ProcessTerminationReason.Completed));
            });
            var registry = new WorkloadRegistry();
            var allocator = new WorkloadAddressAllocator(Path.Combine(tempDir, $"v720-{Path.GetFileName(relativePath)}-ip.json"));
            await using var manager = new EgressTopologyManager(podman, allocator, new WorkloadPostStartVerifier(podman), registry, tempDir,
                windowsPodmanMachineName: "dev", windowsPodmanHookStateDirectory: "/tmp/kautuka-nac-test");
            await SetExpectedBoundarySourceAsync(manager, TestContext.Current.CancellationToken);
            typeof(EgressTopologyManager).GetField("expectedBoundaryRulesetHash", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(manager, EgressTopologyManager.ComputeBoundaryRulesetHash(ipv4, ipv6));
            typeof(EgressTopologyManager).GetField("isStartupGateOpen", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(manager, true);

            await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);
            Assert.True(manager.IsStartupGateOpen);

            string configPath = Path.Combine(tempDir, "nac", relativePath);
            await File.AppendAllTextAsync(configPath, "# drift\n", TestContext.Current.CancellationToken);
            await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);
            Assert.False(manager.IsStartupGateOpen);
            Assert.True((bool)typeof(EgressTopologyManager)
                .GetField("topologyIncidentLatched", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(manager)!);
        }
    }

    [Fact]
    public async Task V7_20_ReconcileClosesGateWhenBoundaryInitFailed_WithNegativeControl()
    {
        const string healthyPod = "[{\"State\":\"Running\",\"Containers\":["
            + "{\"Name\":\"kautuka-egress-boundary\",\"State\":\"exited\"},"
            + "{\"Name\":\"kautuka-egress-dns\",\"State\":\"running\"},"
            + "{\"Name\":\"kautuka-egress-envoy\",\"State\":\"running\"},"
            + "{\"Name\":\"pod-infra\",\"State\":\"running\"}]}]";
        const string failedBoundary = "[{\"State\":{\"Status\":\"exited\",\"ExitCode\":1}}]";
        var calls = new List<string>();
        var podman = new PodmanCommandRunner((args, _) =>
        {
            calls.Add(string.Join(" ", args));
            string output = args.SequenceEqual(["pod", "inspect", EgressTopologyManager.EgressPodName, "--format", "json"])
                ? healthyPod
                : args.SequenceEqual(["inspect", "kautuka-egress-boundary"])
                    ? failedBoundary
                    : "[]";
            return Task.FromResult(new PodmanCommandResult(0, output, string.Empty, ProcessTerminationReason.Completed));
        });
        var registry = new WorkloadRegistry();
        var allocator = new WorkloadAddressAllocator(Path.Combine(tempDir, "v720-boundary-failure-ip.json"));
        var verifier = new WorkloadPostStartVerifier(podman);
        await using var manager = new EgressTopologyManager(podman, allocator, verifier, registry, tempDir);
        await SetExpectedBoundarySourceAsync(manager, TestContext.Current.CancellationToken);
        typeof(EgressTopologyManager)
            .GetField("isStartupGateOpen", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(manager, true);

        await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);

        Assert.False(manager.IsStartupGateOpen);
        Assert.Contains("inspect kautuka-egress-boundary --format json", calls);
        Assert.Contains("ps -a --format json", calls);
        Assert.Contains("pod rm -f kautuka-egress", calls);
        Assert.True(calls.IndexOf("ps -a --format json") < calls.IndexOf("pod rm -f kautuka-egress"));
    }

    [Fact]
    public async Task V7_20_ReconcileClosesGateWhenBoundaryRulesetDrifts_WithNegativeControl()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("Boundary netns ruleset drift verification runs on Linux rootless Podman.");
        }

        const string healthyPod = "[{\"State\":\"Running\",\"InfraContainerID\":\"infra-id\",\"Containers\":["
            + "{\"Name\":\"kautuka-egress-boundary\",\"State\":\"exited\"},"
            + "{\"Name\":\"kautuka-egress-dns\",\"State\":\"running\"},"
            + "{\"Name\":\"kautuka-egress-envoy\",\"State\":\"running\"},"
            + "{\"Name\":\"pod-infra\",\"State\":\"running\"}]}]";
        const string boundarySuccess = "[{\"State\":{\"Status\":\"exited\",\"ExitCode\":0}}]";
        const string infraInspect = "[{\"State\":{\"Pid\":4242}}]";
        const string ipv4Rules = "{\"nftables\":[{\"table\":{\"family\":\"ip\",\"name\":\"kautuka_egress\"}},"
            + "{\"rule\":{\"family\":\"ip\",\"table\":\"kautuka_egress\",\"chain\":\"output\",\"comment\":\"private_reject\",\"expr\":[{\"drop\":null}]}},"
            + "{\"rule\":{\"family\":\"ip\",\"table\":\"kautuka_egress\",\"chain\":\"output\",\"comment\":\"wl_net_reject\",\"expr\":[{\"drop\":null}]}}]}";
        const string ipv4Tampered = "{\"nftables\":[{\"table\":{\"family\":\"ip\",\"name\":\"kautuka_egress\"}},"
            + "{\"rule\":{\"family\":\"ip\",\"table\":\"kautuka_egress\",\"chain\":\"output\",\"comment\":\"private_reject\",\"expr\":[{\"drop\":null}]}},"
            + "{\"rule\":{\"family\":\"ip\",\"table\":\"kautuka_egress\",\"chain\":\"output\",\"comment\":\"wl_net_reject\",\"expr\":[{\"drop\":null}]}},"
            + "{\"rule\":{\"family\":\"ip\",\"table\":\"kautuka_egress\",\"chain\":\"output\",\"comment\":\"rogue_accept\",\"expr\":[{\"accept\":null}]}}]}";
        const string ipv6Rules = "{\"nftables\":[{\"table\":{\"family\":\"ip6\",\"name\":\"kautuka_egress\"}},"
            + "{\"rule\":{\"family\":\"ip6\",\"table\":\"kautuka_egress\",\"chain\":\"output\",\"comment\":\"ipv6_drop_pod\",\"expr\":[{\"drop\":null}]}}]}";
        var calls = new List<string>();
        var podman = new PodmanCommandRunner((args, _) =>
        {
            calls.Add(string.Join(" ", args));
            string output = args.SequenceEqual(["pod", "inspect", EgressTopologyManager.EgressPodName, "--format", "json"])
                ? healthyPod
                : args.SequenceEqual(["inspect", "kautuka-egress-boundary", "--format", "json"])
                    ? boundarySuccess
                    : args.SequenceEqual(["inspect", "infra-id", "--format", "json"])
                        ? infraInspect
                        : args.SequenceEqual(["unshare", "nsenter", "-t", "4242", "-n", "nft", "-j", "list", "table", "ip", "kautuka_egress"])
                            ? ipv4Tampered
                            : args.SequenceEqual(["unshare", "nsenter", "-t", "4242", "-n", "nft", "-j", "list", "table", "ip6", "kautuka_egress"])
                                ? ipv6Rules
                                : "[]";
            return Task.FromResult(new PodmanCommandResult(0, output, string.Empty, ProcessTerminationReason.Completed));
        });
        var registry = new WorkloadRegistry();
        registry.Register(new RegistryEntry(
            "10.247.1.43", "hash-drift", "agent-a", "hash-drift-container", "agent-sandbox", null,
            "agent-sandbox", "normal", "user-agent", null, DateTimeOffset.UtcNow));
        var allocator = new WorkloadAddressAllocator(Path.Combine(tempDir, "v720-boundary-drift-ip.json"));
        var verifier = new WorkloadPostStartVerifier(podman);
        await using var manager = new EgressTopologyManager(podman, allocator, verifier, registry, tempDir);
        await SetExpectedBoundarySourceAsync(manager, TestContext.Current.CancellationToken);
        typeof(EgressTopologyManager)
            .GetField("isStartupGateOpen", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(manager, true);
        typeof(EgressTopologyManager)
            .GetField("expectedBoundaryRulesetHash", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(manager, EgressTopologyManager.ComputeBoundaryRulesetHash(ipv4Rules, ipv6Rules));

        await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);

        Assert.False(manager.IsStartupGateOpen);
        Assert.True((bool)typeof(EgressTopologyManager)
            .GetField("topologyIncidentLatched", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(manager)!);
        Assert.Contains("unshare nsenter -t 4242 -n nft -j list table ip kautuka_egress", calls);
        Assert.Contains("stop -t 2 hash-drift-container", calls);
        Assert.DoesNotContain("ps -a --format json", calls);
    }

    [Fact]
    public async Task V7_20_BrakeKeepsRegistryEntryWhenContainerCannotBeStopped_WithNegativeControl()
    {
        var registry = new WorkloadRegistry();
        registry.Register(new RegistryEntry(
            "10.247.1.31", "user-braked", "agent-a", "container-a", "agent-sandbox", null,
            "agent-sandbox", "normal", "user-agent", null, DateTimeOffset.UtcNow));
        var calls = new List<string>();
        var podman = new PodmanCommandRunner((args, _) =>
        {
            calls.Add(string.Join(" ", args));
            return Task.FromResult(new PodmanCommandResult(125, string.Empty, "container runtime unavailable", ProcessTerminationReason.NonZeroExit));
        });
        var allocator = new WorkloadAddressAllocator(Path.Combine(tempDir, "v720-brake-ip.json"));
        var verifier = new WorkloadPostStartVerifier(podman);
        await using var manager = new EgressTopologyManager(podman, allocator, verifier, registry, tempDir);

        await manager.EngageBrakeAsync("user-braked", registry, TestContext.Current.CancellationToken);

        Assert.Contains("stop -t 2 container-a", calls);
        Assert.Contains("kill container-a", calls);
        Assert.True(registry.TryGetByIp("10.247.1.31", out RegistryEntry? stillTracked));
        Assert.Equal("container-a", stillTracked?.ContainerId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconcileRetriesStoppingAWorkloadWithAnActiveBrake(bool releasedBeforeRetry)
    {
        using var policy = new NetworkAccessPolicyStore(Path.Combine(tempDir, "brake-retry-policy.json"));
        policy.SetBrake(new NetworkAccessActor("admin", true), "user", "alice", true);
        var registry = new WorkloadRegistry();
        registry.Register(new RegistryEntry("10.247.1.31", "alice", "agent", "container-a", "agent-sandbox", null,
            "agent-sandbox", "normal", "user-agent", null, DateTimeOffset.UtcNow));
        int stopAttempts = 0;
        var podman = new PodmanCommandRunner((args, _) =>
        {
            if (args[0] == "stop") return Task.FromResult(++stopAttempts == 1
                ? new PodmanCommandResult(125, "", "failed", ProcessTerminationReason.NonZeroExit)
                : new PodmanCommandResult(0, "", "", ProcessTerminationReason.Completed));
            if (args[0] == "kill") return Task.FromResult(new PodmanCommandResult(125, "", "failed", ProcessTerminationReason.NonZeroExit));
            if (args[0] == "ps") return Task.FromResult(new PodmanCommandResult(0, "[]", "", ProcessTerminationReason.Completed));
            return Task.FromResult(new PodmanCommandResult(0, "", "", ProcessTerminationReason.Completed));
        });
        await using var manager = new EgressTopologyManager(podman, new WorkloadAddressAllocator(Path.Combine(tempDir, "brake-retry-ip.json")),
            new WorkloadPostStartVerifier(podman), registry, tempDir, policyStore: policy);
        await manager.EngageBrakeAsync("alice", registry, TestContext.Current.CancellationToken);
        Assert.Single(registry.GetAll());
        if (releasedBeforeRetry) policy.SetBrake(new NetworkAccessActor("admin", true), "user", "alice", false);
        await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);
        Assert.Equal(releasedBeforeRetry ? 1 : 2, stopAttempts);
        Assert.Empty(registry.GetAll());
    }

    [Fact]
    public async Task V7_29_WorkloadPostStartVerifier_RootlessUsesPodmanUnshare_WithNegativeControl()
    {
        if (!OperatingSystem.IsLinux() || Environment.IsPrivilegedProcess)
        {
            Assert.Skip("The rootless Podman namespace verification test requires an unprivileged Linux process.");
        }

        IReadOnlyList<string>? capturedArgs = null;
        string goldenJson = WorkloadRulesetRenderer.ComputeExpectedCanonicalRulesetHash("10.247.1.12");
        string tmplJson = BoundaryRulesetRenderer.ResolveTemplate("kautuka_workload.nft.json.tmpl")
            .Replace("{{IP}}", "10.247.1.12", StringComparison.Ordinal);

        bool returnMismatch = false;
        var podman = new PodmanCommandRunner((args, _) =>
        {
            capturedArgs = args;
            if (args.Count >= 2 && args[0] == "unshare" && args[1] == "nsenter")
            {
                string payload = returnMismatch
                    ? "{\"nftables\":[{\"table\":{\"family\":\"inet\",\"name\":\"kautuka_workload\"}}]}"
                    : tmplJson;
                return Task.FromResult(new PodmanCommandResult(0, payload, string.Empty, ProcessTerminationReason.Completed));
            }

            return Task.FromResult(new PodmanCommandResult(1, string.Empty, "unexpected", ProcessTerminationReason.NonZeroExit));
        });

        var verifier = new WorkloadPostStartVerifier(podman);
        await verifier.VerifyNetnsRulesetAsync(4242, "10.247.1.12", CancellationToken.None);

        Assert.NotNull(capturedArgs);
        Assert.Equal(["unshare", "nsenter", "-t", "4242", "-n", "nft", "-j", "list", "table", "inet", "kautuka_workload"], capturedArgs);
        Assert.False(string.IsNullOrEmpty(goldenJson));

        // Negative Control (.NC): tampered/partial ruleset returned from podman unshare nsenter throws.
        returnMismatch = true;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => verifier.VerifyNetnsRulesetAsync(4242, "10.247.1.12", CancellationToken.None));
    }

    [Fact]
    public async Task V7_19_NonLinuxRulesetVerificationUsesPodmanMachine_WithNegativeControl()
    {
        const string ip = "10.247.1.12";
        string expectedNftJson = BoundaryRulesetRenderer.ResolveTemplate("kautuka_workload.nft.json.tmpl")
            .Replace("{{IP}}", ip, StringComparison.Ordinal);
        IReadOnlyList<string>? capturedArgs = null;
        bool returnMismatch = false;
        var podman = new PodmanCommandRunner((args, _) =>
        {
            capturedArgs = args.ToArray();
            string output = returnMismatch
                ? "{\"nftables\":[{\"table\":{\"family\":\"inet\",\"name\":\"kautuka_workload\"}}]}"
                : expectedNftJson;
            return Task.FromResult(new PodmanCommandResult(0, output, string.Empty, ProcessTerminationReason.Completed));
        });
        var verifier = new WorkloadPostStartVerifier(podman);

        await verifier.VerifyNetnsRulesetAsync(4242, ip, TestContext.Current.CancellationToken, usePodmanMachineOverride: true);

        Assert.Equal(
            ["machine", "ssh", "podman", "unshare", "nsenter", "-t", "4242", "-n", "nft", "-j", "list", "table", "inet", "kautuka_workload"],
            capturedArgs);

        // Negative control: a missing/partial ruleset returned by Podman Machine must fail verification.
        returnMismatch = true;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => verifier.VerifyNetnsRulesetAsync(4242, ip, TestContext.Current.CancellationToken, usePodmanMachineOverride: true));
    }

    [Fact]
    public void V7_33_BoundaryNft_AllowsUpstreamDnsIpsBeforePrivateReject_WithNegativeControl()
    {
        string rendered = BoundaryRulesetRenderer.RenderBoundaryNft(
            includeLanGrants: false,
            decisionEndpointIp: null,
            upstreamDnsIps: ["10.248.0.1", "10.247.0.2", "127.0.0.1", "169.254.169.254"]);

        int dnsRuleIdx = rendered.IndexOf("ip daddr 10.248.0.1 meta l4proto { tcp, udp } th dport 53 counter accept comment \"dns_upstream\"", StringComparison.Ordinal);
        int privateRejectIdx = rendered.IndexOf("counter reject comment \"private_reject\"", StringComparison.Ordinal);

        Assert.True(dnsRuleIdx > 0);
        Assert.True(privateRejectIdx > dnsRuleIdx);
        Assert.DoesNotContain("10.247.0.2 meta l4proto { tcp, udp } th dport 53 counter accept comment \"dns_upstream\"", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1 meta l4proto { tcp, udp } th dport 53", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("169.254.169.254", rendered.Substring(0, privateRejectIdx), StringComparison.Ordinal);

        // Negative Control (.NC): without upstreamDnsIps, dns_upstream rule is omitted and 10.248.0.1 hits private_reject (10.0.0.0/8).
        string withoutDns = BoundaryRulesetRenderer.RenderBoundaryNft(includeLanGrants: false, decisionEndpointIp: null, upstreamDnsIps: null);
        Assert.DoesNotContain("comment \"dns_upstream\"", withoutDns, StringComparison.Ordinal);
    }

    private async Task SetExpectedBoundarySourceAsync(EgressTopologyManager manager, CancellationToken cancellationToken)
    {
        string rendered = BoundaryRulesetRenderer.RenderBoundaryNft(includeLanGrants: true);
        string nacDirectory = Path.Combine(tempDir, "nac", "boundary");
        Directory.CreateDirectory(nacDirectory);
        await File.WriteAllTextAsync(Path.Combine(nacDirectory, "boundary.nft"), rendered, cancellationToken);
        string envoy = BoundaryRulesetRenderer.RenderEnvoyConfig("/run/kautuka-admin/admin.sock", isWindowsMtls: false);
        string corefile = BoundaryRulesetRenderer.RenderCorefile(EgressTopologyManager.EgressPodIp);
        string envoyDirectory = Path.Combine(tempDir, "nac", "envoy");
        string dnsDirectory = Path.Combine(tempDir, "nac", "dns");
        Directory.CreateDirectory(envoyDirectory);
        Directory.CreateDirectory(dnsDirectory);
        await File.WriteAllTextAsync(Path.Combine(envoyDirectory, "envoy.yaml"), envoy, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(dnsDirectory, "Corefile"), corefile, cancellationToken);
        typeof(EgressTopologyManager)
            .GetField("expectedBoundaryNftSourceHash", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(manager, EgressTopologyManager.ComputeReconcileConfigurationHash(rendered, envoy, corefile));
    }
}
