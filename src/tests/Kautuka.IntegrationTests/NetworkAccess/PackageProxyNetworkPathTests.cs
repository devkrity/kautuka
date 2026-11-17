using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.NetworkAccessController;

using System.Text.Json;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class PackageProxyNetworkPathTests
{
    [Fact]
    public void EnvoyHasDedicatedPackageProxyClusterAndBoundaryPermit()
    {
        string envoy = BoundaryRulesetRenderer.RenderEnvoyConfig();
        string boundary = BoundaryRulesetRenderer.RenderBoundaryNft();

        Assert.Contains("- name: pkgs_proxy", envoy, StringComparison.Ordinal);
        Assert.Contains("address: 10.247.0.3", envoy, StringComparison.Ordinal);
        Assert.Contains("port_value: 20101", envoy, StringComparison.Ordinal);
        Assert.Contains("ip daddr 10.247.0.3 tcp dport 20101 counter accept", boundary, StringComparison.Ordinal);
    }

    [Fact]
    public void PackageProxyWorkloadAcceptsOnlyEnvoyAndEstablishedReplies()
    {
        string proxy = WorkloadRulesetRenderer.Render("10.247.0.3", isPackageProxy: true);
        string ordinaryWorkload = WorkloadRulesetRenderer.Render("10.247.1.20");

        Assert.Contains("ip saddr 10.247.0.2 tcp dport 20101 accept", proxy, StringComparison.Ordinal);
        Assert.Contains("ct state established,related accept", proxy, StringComparison.Ordinal);
        Assert.DoesNotContain("ip daddr 10.247.0.3 tcp dport 20101 accept", ordinaryWorkload, StringComparison.Ordinal);
        Assert.DoesNotContain("ip daddr 10.247.0.3 tcp dport 20101 accept", proxy, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkloadPackageHostnameMapsToSyntheticDestination()
    {
        var spec = new WorkloadContainerSpec("pkg-client", "localhost/test-image:latest", "10.247.1.20")
        {
            UserId = "user-x",
            AgentId = "agent-x",
            ConnectorId = "connector-x",
            StartupGateOpen = true,
            IsBraked = false,
            AddHosts = [$"kautuka-pkgs-proxy:{NetworkAddressClassifier.PackageProxyServiceAddress}"]
        };

        IReadOnlyList<string> arguments = spec.Render();
        Assert.Contains("--add-host", arguments);
        Assert.Contains($"kautuka-pkgs-proxy:{NetworkAddressClassifier.PackageProxyServiceAddress}", arguments);
    }

    [Fact]
    public async Task ReconcileClosesPackageReadinessWhenRegisteredProxyStops()
    {
        string temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        const string cid = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var podman = new PodmanCommandRunner((args, _) => Task.FromResult(
            args[0] == "ps"
                ? new PodmanCommandResult(0, JsonSerializer.Serialize(new[] { new { Id = cid, State = "exited" } }), string.Empty)
                : new PodmanCommandResult(0, string.Empty, string.Empty)));
        var registry = new WorkloadRegistry();
        registry.Register(new RegistryEntry(
            WorkloadRulesetRenderer.PackageProxyIp, "system", "package-proxy", cid, "agent-sandbox", null,
            "agent-sandbox", "normal", "user-agent", null, DateTimeOffset.UtcNow, "package-proxy"));
        await using var manager = new EgressTopologyManager(
            podman,
            new WorkloadAddressAllocator(Path.Combine(temp, "ip.json")),
            new WorkloadPostStartVerifier(podman), registry, temp);
        try
        {
            manager.SetPackageProxyReady(true);
            Assert.True(manager.IsPackageProxyReady);

            await manager.ReconcileOnceAsync(registry, TestContext.Current.CancellationToken);

            Assert.False(manager.IsPackageProxyReady);
            Assert.True(registry.TryGetByIp(WorkloadRulesetRenderer.PackageProxyIp, out _));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public async Task RegistryRebuildDoesNotTrustForgedPackageProxyLabels()
    {
        string temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        const string cid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        string psJson = JsonSerializer.Serialize(new[] { new { Id = cid } });
        string inspectJson = JsonSerializer.Serialize(new[]
        {
            new { Config = new { Labels = new Dictionary<string, string>
            {
                ["io.kautuka.user"] = "system",
                ["io.kautuka.agent"] = "package-proxy",
                ["io.kautuka.kind"] = "agent-sandbox",
                ["io.kautuka.exec-context"] = "agent-sandbox",
                ["io.kautuka.egress"] = "normal"
            } } }
        });
        var podman = new PodmanCommandRunner((args, _) => Task.FromResult(
            args[0] == "ps"
                ? new PodmanCommandResult(0, psJson, string.Empty)
                : new PodmanCommandResult(0, inspectJson, string.Empty)));
        var registry = new WorkloadRegistry();
        await using var manager = new EgressTopologyManager(
            podman,
            new WorkloadAddressAllocator(Path.Combine(temp, "ip.json")),
            new WorkloadPostStartVerifier(podman), registry, temp);
        try
        {
            await manager.RebuildRegistryFromLiveContainersAsync(registry, TestContext.Current.CancellationToken);

            Assert.False(registry.TryGetByIp(WorkloadRulesetRenderer.PackageProxyIp, out _));
            Assert.False(manager.IsPackageProxyReady);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public async Task RegistryRebuildPreservesOnlyThePreviouslyVerifiedLiveProxy()
    {
        string temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        const string cid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        string imageId = "sha256:" + new string('a', 64);
        string psJson = JsonSerializer.Serialize(new[] { new { Id = cid } });
        string inspectJson = JsonSerializer.Serialize(new[]
        {
            new
            {
                Id = cid,
                Image = imageId["sha256:".Length..],
                State = new { Running = true, Pid = 1234, Health = new { Status = "healthy" } },
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
                        ["io.kautuka.kind"] = "agent-sandbox"
                    }
                },
                NetworkSettings = new { Networks = new Dictionary<string, object>
                {
                    [EgressTopologyManager.WorkloadNetworkName] = new { IPAddress = WorkloadRulesetRenderer.PackageProxyIp }
                } }
            }
        });
        string packageRules = BoundaryRulesetRenderer.ResolveTemplate("kautuka_package_proxy.nft.json.tmpl")
            .Replace("{{IP}}", WorkloadRulesetRenderer.PackageProxyIp, StringComparison.Ordinal);
        var podman = new PodmanCommandRunner((args, _) => Task.FromResult(
            args[0] == "ps"
                ? new PodmanCommandResult(0, psJson, string.Empty)
                : args.Contains("nft", StringComparer.Ordinal)
                    ? new PodmanCommandResult(0, packageRules, string.Empty)
                    : new PodmanCommandResult(0, inspectJson, string.Empty)));
        var registry = new WorkloadRegistry();
        await using var manager = new EgressTopologyManager(
            podman,
            new WorkloadAddressAllocator(Path.Combine(temp, "ip.json")),
            new WorkloadPostStartVerifier(podman), registry, temp);
        try
        {
            await manager.AuthorizePackageProxyContainerAsync(cid, imageId, TestContext.Current.CancellationToken);
            manager.RegisterVerifiedPackageProxyContainer(cid);

            await manager.RebuildRegistryFromLiveContainersAsync(registry, TestContext.Current.CancellationToken);

            Assert.True(registry.TryGetByIp(WorkloadRulesetRenderer.PackageProxyIp, out RegistryEntry? entry));
            Assert.NotNull(entry);
            Assert.Equal(cid, entry.ContainerId);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public async Task FailedSpecialHookVerificationCannotRegisterOrMarkProxyReady()
    {
        string temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        const string cid = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
        var podman = new PodmanCommandRunner((args, _) =>
        {
            if (args[0] == "inspect")
            {
                return Task.FromResult(new PodmanCommandResult(0, JsonSerializer.Serialize(new[]
                {
                    new { Id = cid, State = new { Pid = 1234 } }
                }), string.Empty));
            }

            return Task.FromResult(new PodmanCommandResult(1, string.Empty, "intentional special-rules negative control"));
        });
        var registry = new WorkloadRegistry();
        await using var manager = new EgressTopologyManager(
            podman,
            new WorkloadAddressAllocator(Path.Combine(temp, "ip.json")),
            new WorkloadPostStartVerifier(podman), registry, temp);
        try
        {
            bool verified = await manager.VerifyPackageProxyRulesetAsync(cid, TestContext.Current.CancellationToken);

            Assert.False(verified);
            Assert.False(registry.TryGetByIp(WorkloadRulesetRenderer.PackageProxyIp, out _));
            Assert.False(manager.IsPackageProxyReady);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public async Task RevokingPackageProxySyncsWindowsMachineAllowlistBeforeCompleting()
    {
        string temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        const string cid = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
        string imageId = "sha256:" + new string('b', 64);
        string hookState = Path.Combine(temp, "hook");
        Directory.CreateDirectory(hookState);
        var syncedAllowlist = new List<string>();
        var commands = new List<string>();
        var podman = new PodmanCommandRunner(async (args, cancellationToken) =>
        {
            commands.Add(string.Join(' ', args));
            if (args.Count >= 3 && args[0] == "machine" && args[1] == "cp")
            {
                string document = await File.ReadAllTextAsync(args[2], cancellationToken);
                syncedAllowlist.Add(document);
            }

            return new PodmanCommandResult(0, string.Empty, string.Empty);
        });
        var registry = new WorkloadRegistry();
        await using var manager = new EgressTopologyManager(
            podman,
            new WorkloadAddressAllocator(Path.Combine(temp, "ip.json")),
            new WorkloadPostStartVerifier(podman), registry, Path.Combine(temp, "state"),
            hookStateDirectory: hookState,
            windowsPodmanMachineName: "nac-test-machine",
            windowsPodmanHookStateDirectory: "/var/lib/nac");
        try
        {
            await manager.AuthorizePackageProxyContainerAsync(cid, imageId, TestContext.Current.CancellationToken);
            await manager.RevokePackageProxyContainerAsync(cid, TestContext.Current.CancellationToken);

            Assert.Equal(2, syncedAllowlist.Count);
            Assert.Contains(cid, syncedAllowlist[0], StringComparison.Ordinal);
            Assert.DoesNotContain(cid, syncedAllowlist[1], StringComparison.Ordinal);
            Assert.Equal(
                ["machine cp", "machine ssh", "machine cp", "machine ssh"],
                commands.Select(command => string.Join(' ', command.Split(' ').Take(2))));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public async Task RevokingPackageProxyRetriesMachineSyncAfterFirstCopyFailure()
    {
        string temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        const string cid = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee";
        string imageId = "sha256:" + new string('c', 64);
        string hookState = Path.Combine(temp, "hook");
        Directory.CreateDirectory(hookState);
        int copies = 0;
        string? successfulCopy = null;
        var podman = new PodmanCommandRunner(async (args, cancellationToken) =>
        {
            if (args.Count >= 3 && args[0] == "machine" && args[1] == "cp")
            {
                copies++;
                string document = await File.ReadAllTextAsync(args[2], cancellationToken);
                if (copies == 2)
                {
                    return new PodmanCommandResult(1, string.Empty, "intentional first-copy failure");
                }

                successfulCopy = document;
            }

            return new PodmanCommandResult(0, string.Empty, string.Empty);
        });
        var registry = new WorkloadRegistry();
        await using var manager = new EgressTopologyManager(
            podman,
            new WorkloadAddressAllocator(Path.Combine(temp, "ip.json")),
            new WorkloadPostStartVerifier(podman), registry, Path.Combine(temp, "state"),
            hookStateDirectory: hookState,
            windowsPodmanMachineName: "nac-test-machine",
            windowsPodmanHookStateDirectory: "/var/lib/nac");
        try
        {
            await manager.AuthorizePackageProxyContainerAsync(cid, imageId, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => manager.RevokePackageProxyContainerAsync(cid, TestContext.Current.CancellationToken));

            await manager.RevokePackageProxyContainerAsync(cid, TestContext.Current.CancellationToken);

            Assert.Equal(3, copies);
            Assert.NotNull(successfulCopy);
            Assert.DoesNotContain(cid, successfulCopy, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }
}
