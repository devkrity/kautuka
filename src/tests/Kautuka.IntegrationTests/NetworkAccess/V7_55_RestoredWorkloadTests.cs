using System.Text.Json;

using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.NetworkAccessController;

using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class V7_55_RestoredWorkloadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, false, false, true)]
    [InlineData(true, true, false, false, true)]
    [InlineData(true, true, false, false, false, true)]
    [InlineData(true, true, false, false, false, false, true)]
    [InlineData(true, true, false, false, true, false, true, true, true)]
    [InlineData(true, true, false, false, false, false, true, true, false, true)]
    public async Task RestoredWorkloadRequiresMatchingRulesHash(bool matchingRulesHash, bool approvedProfile = false, bool profileResolutionThrows = false, bool imageInspectFails = false, bool locallyBuiltImage = false, bool repoDigestsNull = false, bool repoDigestsMissing = false, bool imageIdFallback = false, bool imageDigestMissing = false, bool imageDigestDiffersFromId = false)
    {
        const string cid = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string ip = "10.247.1.10";
        string temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var calls = new List<string[]>();
        var podman = new PodmanCommandRunner((args, _) =>
        {
            calls.Add(args.ToArray());
            if (args[0] == "ps") return Task.FromResult(new PodmanCommandResult(0, JsonSerializer.Serialize(new[] { new { Id = cid, State = "running" } }), ""));
            if (args[0] == "image" && args.Count > 1 && args[1] == "inspect")
            {
                if (imageInspectFails) return Task.FromResult(new PodmanCommandResult(1, "", "image inspect failed"));
                var imageMetadata = new Dictionary<string, object?>
                {
                    ["Digest"] = imageDigestMissing ? null : imageDigestDiffersFromId ? "sha256:" + new string('b', 64) : locallyBuiltImage || repoDigestsNull || repoDigestsMissing ? "sha256:" + new string('a', 64) : null,
                    ["Id"] = imageIdFallback ? "sha256:" + new string('a', 64) : null,
                };
                if (!repoDigestsMissing)
                {
                    imageMetadata["RepoDigests"] = repoDigestsNull
                        ? null
                        : locallyBuiltImage
                            ? Array.Empty<string>()
                            : new[] { "ghcr.io/example/other@sha256:" + new string('b', 64), "ghcr.io/example/workload@sha256:" + new string('a', 64) };
                }
                return Task.FromResult(new PodmanCommandResult(0, JsonSerializer.Serialize(new[] { imageMetadata }), ""));
            }
            if (args[0] == "inspect")
            {
                var inspect = new Dictionary<string, object?>
                {
                    ["Id"] = cid,
                    ["Image"] = "sha256:" + new string('b', 64),
                    ["Config"] = new Dictionary<string, object?> { ["Labels"] = new Dictionary<string, string> { ["io.kautuka.user"] = "u1", ["io.kautuka.agent"] = "a1", ["io.kautuka.exec-context"] = "agent-sandbox", ["io.kautuka.kind"] = "agent-sandbox", ["io.kautuka.egress"] = "normal" }, ["User"] = approvedProfile ? "0:0" : "65532:65532" },
                    ["HostConfig"] = new Dictionary<string, object?> { ["SecurityOpt"] = new[] { "no-new-privileges" }, ["Privileged"] = false, ["CapAdd"] = approvedProfile ? new[] { "CHOWN" } : Array.Empty<string>(), ["CapDrop"] = new[] { "ALL" }, ["ReadonlyRootfs"] = !approvedProfile, ["NetworkMode"] = "kautuka-workload", ["Sysctls"] = new Dictionary<string, string> { ["net.ipv6.conf.all.disable_ipv6"] = "1" } },
                    ["EffectiveCaps"] = Array.Empty<string>(),
                    ["BoundingCaps"] = Array.Empty<string>(),
                    ["State"] = new Dictionary<string, int> { ["Pid"] = 4242 },
                    ["NetworkSettings"] = new Dictionary<string, object?> { ["Networks"] = new Dictionary<string, object?> { ["kautuka-workload"] = new Dictionary<string, string> { ["IPAddress"] = ip } } },
                    ["Mounts"] = Array.Empty<object>()
                };
                return Task.FromResult(new PodmanCommandResult(0, JsonSerializer.Serialize(new[] { inspect }), ""));
            }
            if (args.Any(arg => arg.Contains("disable_ipv6", StringComparison.Ordinal))) return Task.FromResult(new PodmanCommandResult(0, "1", ""));
            if (args.Contains("nft", StringComparer.Ordinal))
            {
                string nft = BoundaryRulesetRenderer.ResolveTemplate("kautuka_workload.nft.json.tmpl").Replace("{{IP}}", ip, StringComparison.Ordinal);
                if (!matchingRulesHash) nft = nft.Replace("10.247.0.2", "10.247.0.99", StringComparison.Ordinal);
                return Task.FromResult(new PodmanCommandResult(0, nft, ""));
            }
            return Task.FromResult(new PodmanCommandResult(0, "", ""));
        });
        string persistedState = Path.Combine(temp, "manager-desired-state.json");
        var desired = new WorkspaceDesiredStateStore(persistedState, NullLogger<WorkspaceDesiredStateStore>.Instance);
        string compatPath = Path.Combine(temp, "image-compat.json");
        var imageCompatibility = new ImageCompatibilityStore(compatPath);
        if (profileResolutionThrows) await File.WriteAllTextAsync(compatPath, "invalid-json", TestContext.Current.CancellationToken);
        if (approvedProfile) imageCompatibility.SetProfile(new NetworkAccessActor("admin", true), "sha256:" + new string('a', 64), [ImageCompatibilityStore.RootUser, ImageCompatibilityStore.WritableRootfs, ImageCompatibilityStore.FileOwnershipCaps]);
        var registry = new WorkloadRegistry();
        await using var manager = new EgressTopologyManager(podman, new WorkloadAddressAllocator(Path.Combine(temp, "ip.json")), new WorkloadPostStartVerifier(podman, desired), registry, temp, desiredStateStore: desired, imageCompatibilityStore: imageCompatibility);
        try
        {
            await manager.RebuildRegistryFromLiveContainersAsync(registry, TestContext.Current.CancellationToken);
            bool profileLookupFailed = profileResolutionThrows || imageInspectFails;
            Assert.Contains(calls, call => call.Length > 1 && call[0] == "image" && call[1] == "inspect");
            Assert.Equal(!profileLookupFailed, calls.Any(call => call.Contains("nft", StringComparer.Ordinal)));
            Assert.Equal(matchingRulesHash && !profileLookupFailed, registry.TryGetByIp(ip, out _));
            Assert.Equal(!matchingRulesHash || profileLookupFailed, calls.Any(call => call[0] == "rm" && call.Contains("-f", StringComparer.Ordinal) && call.Contains(cid, StringComparer.Ordinal)));
            Assert.Equal(!matchingRulesHash || profileLookupFailed, desired.IsWorkloadQuarantined(cid));
            if (!matchingRulesHash)
            {
                var reopened = new WorkspaceDesiredStateStore(persistedState, NullLogger<WorkspaceDesiredStateStore>.Instance);
                Assert.True(reopened.IsWorkloadQuarantined(cid));
                var restartedRegistry = new WorkloadRegistry();
                await using var restartedManager = new EgressTopologyManager(podman, new WorkloadAddressAllocator(Path.Combine(temp, "restart-ip.json")), new WorkloadPostStartVerifier(podman, reopened), restartedRegistry, temp, desiredStateStore: reopened);
                await restartedManager.RebuildRegistryFromLiveContainersAsync(restartedRegistry, TestContext.Current.CancellationToken);
                Assert.False(restartedRegistry.TryGetByIp(ip, out _));
            }
        }
        finally { Directory.Delete(temp, recursive: true); }
    }
}












