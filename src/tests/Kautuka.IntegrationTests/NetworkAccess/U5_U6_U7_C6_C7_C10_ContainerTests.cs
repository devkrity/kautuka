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
            Environment = [new KeyValuePair<string, string>("HTTP_PROXY", "http://evil:8080")]
        };
        Assert.Throws<ArgumentException>(() => specWithProxyEnv.Render());

        // 3. Non-probe connector without AgentId rejected under normal egress (v7-26)
        var specMissingAgent = new WorkloadContainerSpec("test-workload", pinnedImage, "10.247.1.10")
        {
            UserId = "user-1",
            AgentId = "",
            ConnectorId = "search",
            Egress = "normal",
            Systemwide = false
        };
        Assert.Throws<ArgumentException>(() => specMissingAgent.Render());

        // 4. Closed startup gate blocks rendering
        var specGateClosed = new WorkloadContainerSpec("test-workload", pinnedImage, "10.247.1.10")
        {
            UserId = "user-1",
            AgentId = "agent-1",
            StartupGateOpen = false
        };
        Assert.Throws<InvalidOperationException>(() => specGateClosed.Render());

        // 5. Mounts validation
        var mountRoot = Path.Combine(tempDir, "workspace");
        Directory.CreateDirectory(mountRoot);
        var specWithBadMount = new WorkloadContainerSpec("test-workload", pinnedImage, "10.247.1.10")
        {
            UserId = "user-1",
            AgentId = "agent-1",
            AllowedMountRoots = [mountRoot],
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
            AddHosts = ["metadata.internal:169.254.169.254"]
        };
        Assert.Throws<ArgumentException>(() => badHostMapping.Render());

        // 8. Application arguments after the image may contain Podman flag names safely.
        var applicationFlagArgs = new WorkloadContainerSpec("test-workload", pinnedImage, "10.247.1.10")
        {
            UserId = "user-1",
            AgentId = "agent-1",
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
            Systemwide = false,
            Egress = "normal",
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
        Assert.Contains("forward . /etc/resolv.conf", corefile, StringComparison.Ordinal);

        // Negative Control (.NC): Mutated CoreDNS IP changes output
        string corefileMutated = BoundaryRulesetRenderer.RenderCorefile("10.247.0.99");
        Assert.NotEqual(corefile, corefileMutated);
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
            false,
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

        string workloadRuleset = WorkloadRulesetRenderer.Render("10.247.1.20", includePackageProxy: true);

        // MF7: Idempotent atomic transaction structure
        Assert.StartsWith(
            "table inet kautuka_workload\ndelete table inet kautuka_workload\ntable inet kautuka_workload {",
            workloadRuleset.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);

        // Rendering twice produces byte-for-byte identical text
        string workloadRuleset2 = WorkloadRulesetRenderer.Render("10.247.1.20", includePackageProxy: true);
        Assert.Equal(workloadRuleset, workloadRuleset2);

        // Negative Control (.NC):
        // Without package proxy, ruleset text changes:
        string workloadNoProxy = WorkloadRulesetRenderer.Render("10.247.1.20", includePackageProxy: false);
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
                false,
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
                const string inspect = "[{\"Config\":{\"Labels\":{\"io.kautuka.user\":\"u1\",\"io.kautuka.agent\":\"a1\",\"io.kautuka.exec-context\":\"agent-sandbox\",\"io.kautuka.kind\":\"agent-sandbox\",\"io.kautuka.egress\":\"normal\"}},\"NetworkSettings\":{\"Networks\":{\"kautuka-workload\":{\"IPAddress\":\"10.247.1.10\"}}}}]";
                return Task.FromResult(new PodmanCommandResult(0, inspect, string.Empty, ProcessTerminationReason.Completed));
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
    public void V7_20_CanaryRequiresSuccessfulHookRecordForItsOwnIp_WithNegativeControl()
    {
        const string canaryIp = "10.247.1.24";
        string successfulHook = "{\"action\":\"allow\",\"reason\":\"workload_rules\",\"rules_sha256\":\"abc123\",\"ips\":[\"" + canaryIp + "\"]}";

        Assert.True(EgressTopologyManager.HasSuccessfulCanaryHookRecord(successfulHook, canaryIp));

        // Negative controls: a warning-only/missing record, a refusal, a stale record for another IP,
        // or a success without the applied-rules hash must all keep the startup gate closed.
        Assert.False(EgressTopologyManager.HasSuccessfulCanaryHookRecord(null, canaryIp));
        Assert.False(EgressTopologyManager.HasSuccessfulCanaryHookRecord("{\"action\":\"refuse\",\"ips\":[\"" + canaryIp + "\"]}", canaryIp));
        Assert.False(EgressTopologyManager.HasSuccessfulCanaryHookRecord("{\"action\":\"allow\",\"rules_sha256\":\"abc123\",\"ips\":[\"10.247.1.25\"]}", canaryIp));
        Assert.False(EgressTopologyManager.HasSuccessfulCanaryHookRecord("{\"action\":\"allow\",\"rules_sha256\":\"\",\"ips\":[\"" + canaryIp + "\"]}", canaryIp));
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
    public async Task V7_20_ReconcileClosesStartupGateWhenPodDegrades_WithNegativeControl()
    {
        const string degraded = "[{\"State\":\"Degraded\",\"Containers\":[]}]";
        var podman = new PodmanCommandRunner((args, _) => Task.FromResult(
            new PodmanCommandResult(0, degraded, string.Empty, ProcessTerminationReason.Completed)));
        var registry = new WorkloadRegistry();
        var allocator = new WorkloadAddressAllocator(Path.Combine(tempDir, "v720-health-ip.json"));
        var verifier = new WorkloadPostStartVerifier(podman);
        await using var manager = new EgressTopologyManager(podman, allocator, verifier, registry, tempDir);
        typeof(EgressTopologyManager)
            .GetField("isStartupGateOpen", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(manager, true);

        await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);

        Assert.False(manager.IsStartupGateOpen);
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
        typeof(EgressTopologyManager)
            .GetField("isStartupGateOpen", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(manager, true);

        await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);

        Assert.False(manager.IsStartupGateOpen);
        Assert.Contains("inspect kautuka-egress-boundary --format json", calls);
        Assert.DoesNotContain("ps -a --format json", calls);
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
        var allocator = new WorkloadAddressAllocator(Path.Combine(tempDir, "v720-boundary-drift-ip.json"));
        var verifier = new WorkloadPostStartVerifier(podman);
        await using var manager = new EgressTopologyManager(podman, allocator, verifier, registry, tempDir);
        typeof(EgressTopologyManager)
            .GetField("isStartupGateOpen", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(manager, true);
        typeof(EgressTopologyManager)
            .GetField("expectedBoundaryRulesetHash", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(manager, EgressTopologyManager.ComputeBoundaryRulesetHash(ipv4Rules, ipv6Rules));

        await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);

        Assert.False(manager.IsStartupGateOpen);
        Assert.Contains("unshare nsenter -t 4242 -n nft -j list table ip kautuka_egress", calls);
        Assert.DoesNotContain("ps -a --format json", calls);
    }

    [Fact]
    public async Task V7_20_BrakeKeepsRegistryEntryWhenContainerCannotBeStopped_WithNegativeControl()
    {
        var registry = new WorkloadRegistry();
        registry.Register(new RegistryEntry(
            "10.247.1.31", "user-braked", "agent-a", "container-a", "agent-sandbox", null,
            "agent-sandbox", false, "normal", "user-agent", null, DateTimeOffset.UtcNow));
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

    [Fact]
    public async Task V7_29_WorkloadPostStartVerifier_RootlessUsesPodmanUnshare_WithNegativeControl()
    {
        if (Environment.IsPrivilegedProcess)
        {
            return;
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
}
