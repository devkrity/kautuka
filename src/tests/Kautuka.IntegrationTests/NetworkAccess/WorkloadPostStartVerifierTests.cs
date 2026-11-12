using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.Protos;
using Kautuka.NetworkAccessController;

using ManagerSandboxRuntimeDefinition = Kautuka.Manager.RuntimeContainer.Configuration.SandboxRuntimeDefinition;
using ManagerWorkspaceDefinition = Kautuka.Manager.RuntimeContainer.Configuration.WorkspaceDefinition;

namespace Kautuka.IntegrationTests.NetworkAccess;

public sealed class WorkloadPostStartVerifierTests
{
    private const string ContainerId = "nac-verifier-test";
    private const string AllocatedIp = "10.247.1.80";

    [Fact]
    public async Task ValidInspectPassesAndKeepsRegistryEntry()
    {
        var calls = new List<string[]>();
        WorkloadRegistry registry = CreateRegistry();
        await VerifyAsync(Inspect(), calls, registry);

        Assert.Equal(1, registry.Count);
        Assert.DoesNotContain(calls, static call => call[0] is "kill" or "rm");
    }

    [Fact]
    public async Task NoNewPrivilegesOffFailsAndCleansUp()
    {
        var calls = new List<string[]>();
        WorkloadRegistry registry = CreateRegistry();
        await Assert.ThrowsAsync<InvalidOperationException>(() => VerifyAsync(
            Inspect(noNewPrivileges: false), calls, registry));

        AssertCleanup(calls, registry);
    }

    [Fact]
    public async Task BindMountOutsideConfiguredRootFailsAndCleansUp()
    {
        var calls = new List<string[]>();
        WorkloadRegistry registry = CreateRegistry();
        await Assert.ThrowsAsync<InvalidOperationException>(() => VerifyAsync(
            Inspect(mountType: "bind", mountSource: Path.Combine(Path.GetTempPath(), "not-a-workspace", "file")), calls, registry));

        AssertCleanup(calls, registry);
    }

    [Fact]
    public async Task VolumeMountOutsideConfiguredRootFailsAndCleansUp()
    {
        var calls = new List<string[]>();
        WorkloadRegistry registry = CreateRegistry();
        await Assert.ThrowsAsync<InvalidOperationException>(() => VerifyAsync(
            Inspect(mountType: "volume", mountSource: Path.Combine(Path.GetTempPath(), "podman-volumes", "shared")),
            calls, registry));

        AssertCleanup(calls, registry);
    }

    [Fact]
    public async Task FileCapsProfileRequiresEffectiveAndBoundingCapabilities()
    {
        var profile = FileCapsProfile();
        foreach ((string? effective, string? bounding) in new (string?, string?)[]
        {
            (null, "[\"CHOWN\"]"),
            ("null", "[\"CHOWN\"]"),
            ("[\"CHOWN\"]", null),
            ("[\"CHOWN\"]", "[\"CAP_SYS_ADMIN\"]"),
        })
        {
            var calls = new List<string[]>();
            WorkloadRegistry registry = CreateRegistry();
            await Assert.ThrowsAsync<InvalidOperationException>(() => VerifyAsync(
                Inspect(effectiveCapsJson: effective, boundingCapsJson: bounding, fileCaps: true),
                calls, registry, profile));
            AssertCleanup(calls, registry);
        }
    }

    private static async Task VerifyAsync(
        string inspectJson,
        List<string[]> calls,
        WorkloadRegistry registry,
        ImageCompatProfile? compatProfile = null)
    {
        string workspaceRoot = Path.Combine(Path.GetTempPath(), "nac-v7-verifier-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceRoot);
        try
        {
            string ipRules = BoundaryRulesetRenderer.ResolveTemplate("kautuka_workload.nft.json.tmpl")
                .Replace("{{IP}}", AllocatedIp, StringComparison.Ordinal);
            var runner = new PodmanCommandRunner((args, _) =>
            {
                calls.Add([.. args]);
                if (args[0] == "inspect")
                {
                    return Task.FromResult(Success(inspectJson));
                }

                if (args[0] == "unshare")
                {
                    return Task.FromResult(Success(ipRules));
                }

                return Task.FromResult(Success(string.Empty));
            });
            var desiredState = new WorkspaceDesiredStateStore();
            _ = desiredState.ApplyDesiredState(
                1,
                WorkspaceDesiredStateSyncReason.Bootstrap,
                new ManagerRuntimeSettings(
                    new ManagerWorkspaceDefinition { BaseRoot = workspaceRoot },
                    new ManagerSandboxRuntimeDefinition { Image = "kautuka-base:latest", DefaultTimeout = 30 },
                    new WorkspaceTelemetryDefinition()),
                [], [], []);

            await new WorkloadPostStartVerifier(runner, desiredState).VerifyWorkloadAsync(
                ContainerId, AllocatedIp, compatProfile, registry, CancellationToken.None);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    private static string Inspect(
        bool noNewPrivileges = true,
        string? mountType = null,
        string? mountSource = null,
        string? effectiveCapsJson = "[]",
        string? boundingCapsJson = "[]",
        bool fileCaps = false)
    {
        var inspect = new JsonObject
        {
            ["HostConfig"] = new JsonObject
            {
                ["SecurityOpt"] = noNewPrivileges ? JsonNode.Parse("[\"no-new-privileges\"]") : JsonNode.Parse("[]"),
                ["Sysctls"] = JsonNode.Parse("{\"net.ipv6.conf.all.disable_ipv6\":\"1\"}"),
                ["Privileged"] = false,
                ["CapAdd"] = fileCaps ? JsonNode.Parse("[\"CHOWN\"]") : JsonNode.Parse("[]"),
                ["CapDrop"] = JsonNode.Parse("[\"ALL\"]"),
                ["ReadonlyRootfs"] = true,
                ["NetworkMode"] = "bridge",
                ["Devices"] = JsonNode.Parse("[]"),
            },
            ["Config"] = new JsonObject { ["User"] = "65532:65532" },
            ["State"] = new JsonObject { ["Pid"] = 4242 },
            ["NetworkSettings"] = JsonNode.Parse("{\"Networks\":{\"kautuka-workload\":{\"IPAddress\":\"10.247.1.80\"}}}"),
            ["Mounts"] = new JsonArray(),
        };
        if (effectiveCapsJson is not null)
        {
            inspect["EffectiveCaps"] = JsonNode.Parse(effectiveCapsJson);
        }
        if (boundingCapsJson is not null)
        {
            inspect["BoundingCaps"] = JsonNode.Parse(boundingCapsJson);
        }
        if (mountType is not null)
        {
            inspect["Mounts"]!.AsArray().Add(new JsonObject
            {
                ["Type"] = mountType,
                ["Source"] = mountSource,
                ["Destination"] = "/data",
            });
        }

        return JsonSerializer.Serialize(new JsonArray(inspect));
    }

    private static ImageCompatProfile FileCapsProfile()
        => new(
            "sha256:" + new string('a', 64),
            "connector",
            "Connector",
            [ImageCompatibilityStore.FileOwnershipCaps],
            "admin-1",
            DateTimeOffset.UtcNow,
            Reason: null);

    private static WorkloadRegistry CreateRegistry()
    {
        var registry = new WorkloadRegistry();
        registry.Register(new RegistryEntry(
            AllocatedIp, "user-alpha", "agent-alpha", ContainerId, "connector", "connector-alpha",
            "isolated", false, "normal", "user-agent", null, DateTimeOffset.UtcNow));
        return registry;
    }

    private static void AssertCleanup(IReadOnlyList<string[]> calls, WorkloadRegistry registry)
    {
        Assert.Contains(calls, static call => call[0] == "kill");
        Assert.Contains(calls, static call => call[0] == "rm" && call.Contains("-f", StringComparer.Ordinal));
        Assert.Equal(0, registry.Count);
    }

    private static PodmanCommandResult Success(string stdout)
        => new(0, stdout, string.Empty, ProcessTerminationReason.Completed);
}
