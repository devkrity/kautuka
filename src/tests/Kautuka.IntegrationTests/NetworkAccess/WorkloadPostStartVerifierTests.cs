using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Sandbox.Models;
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
    public void PodmanMachineMountPathsMatchOnlyTheirWindowsWorkspaceRoot()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows drive paths are resolved by the Windows host runtime.");
        }

        const string windowsRoot = @"C:\Users\amshu\.kautuka\workspaces\agents\agent-1\user-a";
        string machineRoot = WorkloadPostStartVerifier.MapWindowsWorkspaceRootToPodmanMachinePath(windowsRoot);

        Assert.Equal("/mnt/c/Users/amshu/.kautuka/workspaces/agents/agent-1/user-a", machineRoot);
        Assert.True(WorkloadPostStartVerifier.IsWithinAllowedRoot(
            machineRoot + "/project/file.txt",
            [machineRoot]));
        Assert.False(WorkloadPostStartVerifier.IsWithinAllowedRoot(
            "/mnt/c/Users/amshu/.kautuka/workspaces/agents/agent-1/user-b/project/file.txt",
            [machineRoot]));
        Assert.False(WorkloadPostStartVerifier.IsWithinAllowedRoot(
            "/mnt/c/Users/amshu/.kautuka/workspaces/agents/agent-2/user-a/project/file.txt",
            [machineRoot]));
        Assert.False(WorkloadPostStartVerifier.IsWithinAllowedRoot(
            machineRoot + "/../user-b/project/file.txt",
            [machineRoot]));
    }

    [Fact]
    public void PosixMountPathCannotEscapeAllowedRootWithParentSegments()
    {
        Assert.False(WorkloadPostStartVerifier.IsWithinAllowedRoot(
            "/workspace/user-a/../user-b/file", ["/workspace/user-a"]));
    }

    private const string RootlessPodmanCapabilityInspectFragment = """
        {
          "EffectiveCaps": null,
          "BoundingCaps": null,
          "HostConfig": {
            "CapAdd": [],
            "CapDrop": [
              "CAP_CHOWN", "CAP_DAC_OVERRIDE", "CAP_FOWNER", "CAP_FSETID", "CAP_KILL",
              "CAP_NET_BIND_SERVICE", "CAP_SETFCAP", "CAP_SETGID", "CAP_SETPCAP", "CAP_SETUID", "CAP_SYS_CHROOT"
            ],
            "SecurityOpt": ["no-new-privileges"]
          }
        }
        """;

    private const string RootlessPodman58SysctlInspectFragment = """
        {
          "Sysctls": null,
          "HostConfig": {
            "SecurityOpt": ["no-new-privileges"],
            "CapDrop": [
              "CAP_CHOWN", "CAP_DAC_OVERRIDE", "CAP_FOWNER", "CAP_FSETID", "CAP_KILL",
              "CAP_NET_BIND_SERVICE", "CAP_SETFCAP", "CAP_SETGID", "CAP_SETPCAP", "CAP_SETUID", "CAP_SYS_CHROOT"
            ]
          }
        }
        """;

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
    public async Task RootlessPodmanExpandedCapDropAndNullCapabilitySetsPass()
    {
        // These capability fields are copied from a live `podman inspect` on rootless Podman 6.1.4.
        JsonObject inspect = JsonNode.Parse(Inspect(effectiveCapsJson: "null", boundingCapsJson: "null"))!
            .AsArray()[0]!.AsObject().DeepClone().AsObject();
        JsonObject capabilityInspect = JsonNode.Parse(RootlessPodmanCapabilityInspectFragment)!.AsObject();
        JsonObject hostConfig = inspect["HostConfig"]!.AsObject();
        JsonObject capturedHostConfig = capabilityInspect["HostConfig"]!.AsObject();
        hostConfig["CapAdd"] = capturedHostConfig["CapAdd"]!.DeepClone();
        hostConfig["CapDrop"] = capturedHostConfig["CapDrop"]!.DeepClone();
        hostConfig["SecurityOpt"] = capturedHostConfig["SecurityOpt"]!.DeepClone();
        inspect["EffectiveCaps"] = JsonNode.Parse("null");
        inspect["BoundingCaps"] = JsonNode.Parse("null");

        var calls = new List<string[]>();
        WorkloadRegistry registry = CreateRegistry();
        await VerifyAsync(JsonSerializer.Serialize(new JsonArray(inspect)), calls, registry);

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
    public async Task Ipv6DisableSysctlOffFailsAndCleansUp()
    {
        var calls = new List<string[]>();
        WorkloadRegistry registry = CreateRegistry();
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => VerifyAsync(
            OperatingSystem.IsLinux() ? Inspect() : Inspect(ipv6Disabled: false),
            calls, registry, ipv6SysctlValue: "0"));

        Assert.Contains("IPv6 disable sysctl", exception.InnerException?.Message ?? exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(calls, static call => call.Contains("/proc/sys/net/ipv6/conf/all/disable_ipv6", StringComparer.Ordinal));
        AssertCleanup(calls, registry);
    }

    [Fact]
    public async Task RootlessPodman58InspectWithoutSysctlsUsesLiveNetnsValue()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        JsonObject inspect = JsonNode.Parse(Inspect())!.AsArray()[0]!.AsObject().DeepClone().AsObject();
        JsonObject actualPodmanFields = JsonNode.Parse(RootlessPodman58SysctlInspectFragment)!.AsObject();
        JsonObject hostConfig = inspect["HostConfig"]!.AsObject();
        JsonObject actualHostConfig = actualPodmanFields["HostConfig"]!.AsObject();
        hostConfig.Remove("Sysctls");
        hostConfig["SecurityOpt"] = actualHostConfig["SecurityOpt"]!.DeepClone();
        hostConfig["CapDrop"] = actualHostConfig["CapDrop"]!.DeepClone();
        inspect["Sysctls"] = null;
        inspect["EffectiveCaps"] = null;
        inspect["BoundingCaps"] = null;

        var calls = new List<string[]>();
        WorkloadRegistry registry = CreateRegistry();
        await VerifyAsync(JsonSerializer.Serialize(new JsonArray(inspect)), calls, registry, ipv6SysctlValue: "1");

        Assert.Contains(calls, static call => call.Contains("/proc/sys/net/ipv6/conf/all/disable_ipv6", StringComparer.Ordinal));
        Assert.Equal(1, registry.Count);
        Assert.DoesNotContain(calls, static call => call[0] is "kill" or "rm");
    }

    [Fact]
    public async Task NonLinuxHostVerifiesNetnsRulesetThroughPodmanMachine()
    {
        if (OperatingSystem.IsLinux())
        {
            return;
        }

        var calls = new List<string[]>();
        WorkloadRegistry registry = CreateRegistry();
        await VerifyAsync(Inspect(), calls, registry);

        Assert.Contains(calls, static call => call.Length > 2
            && call[0] == "machine"
            && call[1] == "ssh"
            && call.Contains("nsenter", StringComparer.Ordinal)
            && call.Contains(WorkloadRulesetRenderer.WorkloadTableName, StringComparer.Ordinal));
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
    public async Task BindMountFromSameAgentIdInAnotherUsersWorkspaceFailsAndCleansUp()
    {
        var calls = new List<string[]>();
        WorkloadRegistry registry = CreateRegistry();
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => VerifyAsync(
            Inspect(mountType: "bind", mountSource: "{{OTHER_USER_WORKSPACE}}"),
            calls, registry));

        Assert.Contains("Mount check failed", exception.InnerException?.Message ?? exception.Message, StringComparison.OrdinalIgnoreCase);
        AssertCleanup(calls, registry);
    }

    [Fact]
    public async Task BindMountFromAnotherAgentsWorkspaceOfSameUserFailsAndCleansUp()
    {
        var calls = new List<string[]>();
        WorkloadRegistry registry = CreateRegistry();
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => VerifyAsync(
            Inspect(mountType: "bind", mountSource: "{{OTHER_AGENT_WORKSPACE}}"), calls, registry));

        Assert.Contains("Mount check failed", exception.InnerException?.Message ?? exception.Message, StringComparison.OrdinalIgnoreCase);
        AssertCleanup(calls, registry);
    }

    [Fact]
    public async Task BindMountFromMatchingDesiredWorkspacePasses()
    {
        var calls = new List<string[]>();
        WorkloadRegistry registry = CreateRegistry();
        await VerifyAsync(Inspect(mountType: "bind", mountSource: "{{OWNER_WORKSPACE}}"), calls, registry);

        Assert.Equal(1, registry.Count);
        Assert.DoesNotContain(calls, static call => call[0] is "kill" or "rm");
    }

    [Fact]
    public async Task UserlessSandboxMountMatchesOnlyItsOwnDesiredWorkspace()
    {
        static WorkloadRegistry UserlessRegistry()
        {
            var registry = new WorkloadRegistry();
            registry.Register(new RegistryEntry(AllocatedIp, null, "agent-alpha", ContainerId, "agent-sandbox", null,
                "agent-sandbox", false, "deny-all", "none", null, DateTimeOffset.UtcNow));
            return registry;
        }

        var calls = new List<string[]>();
        await VerifyAsync(Inspect(mountType: "bind", mountSource: "{{USERLESS_WORKSPACE}}"), calls, UserlessRegistry());
        Assert.DoesNotContain(calls, static call => call[0] is "kill" or "rm");

        calls.Clear();
        WorkloadRegistry registry = UserlessRegistry();
        await Assert.ThrowsAsync<InvalidOperationException>(() => VerifyAsync(
            Inspect(mountType: "bind", mountSource: "{{OWNER_WORKSPACE}}"), calls, registry));
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
        ImageCompatProfile? compatProfile = null,
        string ipv6SysctlValue = "1")
    {
        string workspaceRoot = Path.Combine(Path.GetTempPath(), "nac-v7-verifier-root-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceRoot);
        try
        {
            string ownerWorkspacePath = Path.Combine(workspaceRoot, "users", "user-alpha", "agents", "agent-alpha");
            string siblingAgentWorkspacePath = Path.Combine(workspaceRoot, "users", "user-alpha", "agents", "agent-beta");
            inspectJson = inspectJson
                .Replace("{{OWNER_WORKSPACE}}", JsonSerializer.Serialize(ownerWorkspacePath)[1..^1], StringComparison.Ordinal)
                .Replace("{{USERLESS_WORKSPACE}}", JsonSerializer.Serialize(Path.Combine(workspaceRoot, "shared", "agent-alpha"))[1..^1], StringComparison.Ordinal)
                .Replace(
                    "{{OTHER_USER_WORKSPACE}}",
                    JsonSerializer.Serialize(Path.Combine(workspaceRoot, "users", "user-beta", "agents", "agent-alpha"))[1..^1],
                    StringComparison.Ordinal)
                .Replace(
                    "{{OTHER_AGENT_WORKSPACE}}",
                    JsonSerializer.Serialize(Path.Combine(workspaceRoot, "users", "user-alpha", "agents", "agent-beta"))[1..^1],
                    StringComparison.Ordinal);
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
                    if (args.Contains("cat", StringComparer.Ordinal))
                    {
                        return Task.FromResult(Success(ipv6SysctlValue));
                    }

                    return Task.FromResult(Success(ipRules));
                }

                if (args.Count > 1 && args[0] == "machine" && args[1] == "ssh")
                {
                    if (args.Contains("cat", StringComparer.Ordinal))
                    {
                        return Task.FromResult(Success(ipv6SysctlValue));
                    }

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
                [
                    new ContainerContext(
                        "agent", "agent-alpha", null, Path.Combine(workspaceRoot, "shared", "agent-alpha"), "/workspace", "image", 30, "512m", 1, [], new Dictionary<string, string>()),
                    new ContainerContext(
                        "agent", "agent-alpha", "user-alpha", ownerWorkspacePath, "/workspace", "image", 30, "512m", 1, [], new Dictionary<string, string>()),
                    new ContainerContext(
                        "agent", "agent-alpha", "user-beta", Path.Combine(workspaceRoot, "users", "user-beta", "agents", "agent-alpha"), "/workspace", "image", 30, "512m", 1, [], new Dictionary<string, string>()),
                    new ContainerContext(
                        "agent", "agent-beta", "user-alpha", siblingAgentWorkspacePath, "/workspace", "image", 30, "512m", 1, [], new Dictionary<string, string>()),
                ],
                [], []);

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
        bool fileCaps = false,
        bool ipv6Disabled = true)
    {
        var inspect = new JsonObject
        {
            ["HostConfig"] = new JsonObject
            {
                ["SecurityOpt"] = noNewPrivileges ? JsonNode.Parse("[\"no-new-privileges\"]") : JsonNode.Parse("[]"),
                ["Sysctls"] = JsonNode.Parse(ipv6Disabled
                    ? "{\"net.ipv6.conf.all.disable_ipv6\":\"1\"}"
                    : "{\"net.ipv6.conf.all.disable_ipv6\":\"0\"}"),
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
