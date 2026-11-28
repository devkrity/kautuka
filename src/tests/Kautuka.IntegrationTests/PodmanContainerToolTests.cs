using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;

using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.Manager.RuntimeContainer.Sandbox;
using Kautuka.Manager.RuntimeContainer.Sandbox.Models;
using Kautuka.NetworkAccessController;

namespace Kautuka.IntegrationTests;

public sealed class PodmanContainerToolTests
{
    [Fact]
    public async Task HardenedSandboxUserCanCreateAndRemoveConnectorInstallDirectoryOnTmpfsHome()
    {
        const string image = "ghcr.io/devkrity/kautuka-base@sha256:675400832f2ced5cf802db66eb067ef5022f65f0551c665d87ae7edc58dc0839";
        var podman = new PodmanCommandRunner();
        PodmanCommandResult imageExists;
        try
        {
            imageExists = await podman.RunAsync(
                ["image", "exists", image],
                10,
                TestContext.Current.CancellationToken);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Assert.Skip("Podman is not installed; the sandbox home accessibility check requires Podman.");
            return;
        }

        if (imageExists.ExitCode != 0)
        {
            Assert.Skip($"Sandbox image '{image}' is not available locally.");
            return;
        }

        const string installDirectory = "/home/node/.kautuka/connectors";
        const string command = "mkdir -p /home/node/.kautuka/connectors && rmdir /home/node/.kautuka/connectors";

        // Negative control: the former nested tmpfs leaves /home/node owned by UID 1000,
        // so hardened UID 65532 cannot traverse it to use the connector install path.
        PodmanCommandResult oldNestedHome = await podman.RunAsync(
            [
                "run", "--pull=never", "--rm", "--read-only", "--user", "65532:65532",
                "--tmpfs", "/home/node/.kautuka:rw,nodev,nosuid,mode=1777",
                "--entrypoint", "/bin/sh", image, "-c", command,
            ],
            30,
            TestContext.Current.CancellationToken);
        Assert.NotEqual(0, oldNestedHome.ExitCode);

        PodmanCommandResult writableHome = await podman.RunAsync(
            [
                "run", "--pull=never", "--rm", "--read-only", "--user", "65532:65532",
                "--tmpfs", "/home/node:rw,nodev,nosuid,mode=1777",
                "--entrypoint", "/bin/sh", image, "-c", command,
            ],
            30,
            TestContext.Current.CancellationToken);

        Assert.True(
            writableHome.ExitCode == 0,
            $"UID 65532 could not create and remove {installDirectory} with a writable home tmpfs. "
                + $"ExitCode={writableHome.ExitCode}; stderr={writableHome.StdErr}");
    }

    [Fact]
    public void UserlessAgentSandboxRegistersDenyAllIdentity_WhileUserAgentKeepsNormalEgress()
    {
        var registry = new WorkloadRegistry();
        ContainerContext userless = CreateLaunchContext("placeholder", "shared-agent", Path.Combine(Path.GetTempPath(), "shared-agent"))
            with
        { UserId = null };
        RegistryEntry systemEntry = PodmanContainerTool.BuildSandboxRegistryEntry(userless, "10.247.1.200", "sandbox-system");

        registry.Register(systemEntry);
        RegistryEntry registeredSystemEntry = Assert.Single(registry.GetAll());
        Assert.Equal("deny-all", registeredSystemEntry.Egress);
        Assert.Equal("none", registeredSystemEntry.Attribution);
        Assert.Null(registeredSystemEntry.UserId);
        Assert.Equal("shared-agent", registeredSystemEntry.AgentId);
        List<string> systemArguments = InvokeBuildRunArguments(userless, "sandbox-system");
        Assert.Contains("io.kautuka.egress=deny-all", systemArguments);
        Assert.Contains("io.kautuka.user=", systemArguments);
        Assert.Contains("io.kautuka.agent=shared-agent", systemArguments);

        ContainerContext userAgent = CreateLaunchContext("user-a", "agent-a", Path.Combine(Path.GetTempPath(), "agent-a"));
        RegistryEntry userEntry = PodmanContainerTool.BuildSandboxRegistryEntry(userAgent, "10.247.1.201", "sandbox-user");
        registry.Register(userEntry);
        Assert.Equal("normal", registry.FindByIp("10.247.1.201")?.Egress);
        Assert.Equal("user-agent", registry.FindByIp("10.247.1.201")?.Attribution);
        Assert.Contains("io.kautuka.egress=normal", InvokeBuildRunArguments(userAgent, "sandbox-user"));
    }

    [Fact]
    public void SkillSandboxesUseSeparateNamesAndOnlyTheFilesModeHasAReadOnlyBind()
    {
        string root = Path.Combine(Path.GetTempPath(), "skill-workspace");
        ContainerContext shared = CreateLaunchContext("user-a", "agent-a", Path.Combine(root, "agent"));
        ContainerContext isolated = shared with
        {
            ScopeType = "skill",
            ScopeId = "skill-a",
            AgentId = shared.ScopeId,
            HostWorkspacePath = Path.Combine(root, "skill"),
        };
        ContainerContext withFiles = isolated with { ScopeType = "skill-with-agent-files", HostWorkspacePath = shared.HostWorkspacePath };

        Assert.Equal(3, new[] { shared, isolated, withFiles }.Select(PodmanContainerTool.BuildContainerName).Distinct().Count());
        List<string> ownArguments = InvokeBuildRunArguments(isolated, "skill-own");
        List<string> filesArguments = InvokeBuildRunArguments(withFiles, "skill-files");
        Assert.Contains("--group-add=0", ownArguments);
        Assert.Contains("--group-add=0", filesArguments);
        Assert.DoesNotContain(ownArguments, item => item.StartsWith(shared.HostWorkspacePath + ":", StringComparison.Ordinal));
        Assert.Contains(filesArguments, item => item.StartsWith(shared.HostWorkspacePath + ":", StringComparison.Ordinal)
            && item.EndsWith(":ro", StringComparison.Ordinal));
        RegistryEntry entry = PodmanContainerTool.BuildSandboxRegistryEntry(withFiles, "10.247.1.202", "skill-files");
        Assert.Equal("agent-a", entry.AgentId);
        Assert.Equal("user-a", entry.UserId);
        Assert.Equal("user-agent", entry.Attribution);
    }

    [Fact]
    public async Task V7_45_BrakedSandboxIsRejectedBeforePodman_AndOtherUserReachesRun_WithNegativeControl()
    {
        string root = Path.Combine(Path.GetTempPath(), "nac-v7-sandbox-launch-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var policyStore = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"));
            policyStore.SetBrake(new NetworkAccessActor("admin", IsAdmin: true), "user", "blocked-user", on: true);
            var commands = new List<string[]>();
            var runner = new PodmanCommandRunner((args, _) =>
            {
                commands.Add([.. args]);
                if (args[0] == "run")
                {
                    return Task.FromResult(new PodmanCommandResult(125, string.Empty, "synthetic start failure", ProcessTerminationReason.NonZeroExit));
                }

                if (args[0] == "inspect")
                {
                    return Task.FromResult(new PodmanCommandResult(125, string.Empty, "no such container", ProcessTerminationReason.NonZeroExit));
                }

                if (args[0] == "container" && args.Count > 1 && args[1] == "exists")
                {
                    return Task.FromResult(new PodmanCommandResult(1, string.Empty, "no such container", ProcessTerminationReason.NonZeroExit));
                }

                return Task.FromResult(new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed));
            });
            var gate = new WorkloadLaunchGate(new TestWorkloadStartupState(true), policyStore);
            var tool = new PodmanContainerTool(
                runner,
                addressAllocator: new WorkloadAddressAllocator(Path.Combine(root, "ip-cursor.json")),
                workloadLaunchGate: gate);
            ContainerContext blockedContext = CreateLaunchContext("blocked-user", "agent-a", Path.Combine(root, "blocked"));

            InvalidOperationException blocked = await Assert.ThrowsAsync<InvalidOperationException>(
                () => tool.SpawnSandboxAsync(blockedContext, TestContext.Current.CancellationToken));

            Assert.Contains("user network brake is active", blocked.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(commands);

            ContainerContext otherUserContext = CreateLaunchContext("other-user", "agent-b", Path.Combine(root, "other"));
            _ = await Assert.ThrowsAsync<InvalidOperationException>(
                () => tool.SpawnSandboxAsync(otherUserContext, TestContext.Current.CancellationToken));
            Assert.Contains(commands, static args => args[0] == "run");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SandboxStartupFiltersHostSensitiveEnvironmentAndReusesExistingV0127Sandbox()
    {
        string root = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string workspacePath = Path.Combine(root, "agent-a");
            Directory.CreateDirectory(workspacePath);
            string image = "ghcr.io/devkrity/kautuka-base@sha256:" + new string('a', 64);
            string imageId = "sha256:" + new string('b', 64);
            IReadOnlyDictionary<string, string>? observedRunEnvironment = null;
            List<string>? observedRunArgs = null;
            string? environmentPath = null;
            string? environmentContent = null;
            bool containerExists = false;
            string? inspectPayload = null;

            var runner = new PodmanCommandRunner(async (args, env, ct) =>
            {
                if (args[0] == "run")
                {
                    observedRunArgs = [.. args];
                    environmentPath = args[args.ToList().IndexOf("--env-file") + 1];
                    environmentContent = await File.ReadAllTextAsync(environmentPath, ct);
                    observedRunEnvironment = new Dictionary<string, string>(env, StringComparer.Ordinal);
                    return new PodmanCommandResult(0, new string('c', 64), string.Empty, ProcessTerminationReason.Completed);
                }

                if (args[0] == "image" && args.Count > 1 && args[1] == "inspect")
                {
                    return new PodmanCommandResult(0, imageId, string.Empty, ProcessTerminationReason.Completed);
                }

                if (args[0] == "inspect")
                {
                    return containerExists && inspectPayload is not null
                        ? new PodmanCommandResult(0, inspectPayload, string.Empty, ProcessTerminationReason.Completed)
                        : new PodmanCommandResult(125, string.Empty, "no such container", ProcessTerminationReason.NonZeroExit);
                }

                if (args[0] == "container" && args.Count > 1 && args[1] == "exists")
                {
                    return containerExists
                        ? new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed)
                        : new PodmanCommandResult(1, string.Empty, "no such container", ProcessTerminationReason.NonZeroExit);
                }

                return new PodmanCommandResult(0, string.Empty, string.Empty, ProcessTerminationReason.Completed);
            });

            using var policyStore = new NetworkAccessPolicyStore(Path.Combine(root, "policy.json"));
            var tool = new PodmanContainerTool(
                runner,
                addressAllocator: new WorkloadAddressAllocator(Path.Combine(root, "ip-cursor.json")),
                workloadLaunchGate: new WorkloadLaunchGate(new TestWorkloadStartupState(true), policyStore));

            ContainerContext context = CreateLaunchContext("user-a", "agent-a", workspacePath) with
            {
                Image = image,
                DesiredStateVersion = 1,
                ContainerEnvironment = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["NPM_CONFIG_REGISTRY"] = "http://kautuka-pkgs-proxy:20101/npm/",
                    ["LD_PRELOAD"] = "/tmp/poison.so",
                    ["CONTAINERS_CONF"] = "/tmp/containers.conf",
                    ["DOCKER_HOST"] = "tcp://127.0.0.1:2375",
                    ["PODMAN_TMPDIR"] = "/tmp/podman",
                    ["GCONV_PATH"] = "/tmp/gconv",
                    ["PATH"] = "/tmp/bin",
                },
            };
            context = context with { SpecFingerprint = WorkspaceContainerSpecFingerprint.Compute(context, []) };

            OciSandboxSession created = await tool.SpawnSandboxAsync(context, TestContext.Current.CancellationToken);
            Assert.True(created.Created);
            Assert.NotNull(observedRunArgs);
            Assert.NotNull(observedRunEnvironment);
            Assert.Empty(observedRunEnvironment);
            Assert.NotNull(environmentContent);
            Assert.False(File.Exists(environmentPath));
            foreach (var entry in context.ContainerEnvironment)
                Assert.Contains(entry.Key + "=" + entry.Value, environmentContent, StringComparison.Ordinal);
            foreach (string blocked in new[] { "LD_PRELOAD", "CONTAINERS_CONF", "DOCKER_HOST", "PODMAN_TMPDIR", "GCONV_PATH", "PATH" })
            {
                Assert.DoesNotContain(blocked, observedRunArgs);
                Assert.False(observedRunEnvironment.ContainsKey(blocked));
            }

            // v0.1.27 stored these settings inside the container, including host-sensitive names.
            ContainerContext v0127Context = context;
            v0127Context = v0127Context with { SpecFingerprint = WorkspaceContainerSpecFingerprint.Compute(v0127Context, []) };
            containerExists = true;
            observedRunArgs = null;
            inspectPayload = System.Text.Json.JsonSerializer.Serialize(new[]
            {
                new
                {
                    Id = new string('d', 64),
                    Image = imageId,
                    State = new { Status = "running" },
                    HostConfig = new
                    {
                        NetworkMode = "kautuka-workload",
                        PublishAllPorts = false,
                        PortBindings = new Dictionary<string, object>(),
                        Binds = new[] { $"{workspacePath}:/workspace:rw" },
                        CapDrop = new[] { "ALL" },
                        CapAdd = Array.Empty<string>(),
                        Privileged = false,
                        Devices = Array.Empty<object>(),
                        ReadonlyRootfs = true,
                        PidsLimit = 256L,
                        SecurityOpt = new[] { "no-new-privileges" },
                        Tmpfs = new Dictionary<string, string> { ["/tmp"] = "rw,noexec,nosuid", ["/home/node"] = "rw,nodev,nosuid,mode=1777" },
                    },
                    Mounts = new[] { new { Type = "bind", Source = workspacePath, Destination = "/workspace" } },
                    NetworkSettings = new { Networks = new Dictionary<string, object> { ["kautuka-workload"] = new { IPAddress = "10.247.1.10" } } },
                    Config = new
                    {
                        Env = context.ContainerEnvironment.Select(pair => pair.Key + "=" + pair.Value).ToArray(),
                        Labels = new Dictionary<string, string>
                        {
                            ["kautuka.desired-version"] = "1",
                            ["kautuka.spec-fingerprint"] = v0127Context.SpecFingerprint!,
                        },
                    },
                },
            });

            OciSandboxSession reused = await tool.SpawnSandboxAsync(v0127Context, TestContext.Current.CancellationToken);
            Assert.False(reused.Created);
            Assert.Null(observedRunArgs);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EphemeralEnvironmentFileIsRestrictedToTheProcessOwner()
    {
        string path = await PodmanContainerTool.WriteEphemeralEnvironmentFileAsync(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["MCP_AUTH_TOKEN"] = "synthetic-env-secret" },
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        try
        {
            string text = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true);
            Assert.Contains("synthetic-env-secret", text, StringComparison.Ordinal);
            if (OperatingSystem.IsWindows())
            {
                FileSecurity security = new FileInfo(path).GetAccessControl();
                Assert.True(security.AreAccessRulesProtected);
                AuthorizationRuleCollection rules = security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier));
                Assert.NotEmpty(rules);
                foreach (FileSystemAccessRule rule in rules)
                {
                    Assert.Equal(WindowsIdentity.GetCurrent().User, (SecurityIdentifier)rule.IdentityReference);
                }
            }
            else
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            }
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void BuildRunArgumentsUsesOnlyContainerEnvironment()
    {
        ContainerContext context = new(
            ScopeType: "agent",
            ScopeId: "alpha",
            UserId: "user-test",
            HostWorkspacePath: "/tmp/kautuka/alpha",
            ContainerWorkspacePath: "/workspace",
            Image: "docker.io/example/agent@sha256:0000000000000000000000000000000000000000000000000000000000000001",
            DefaultCpuLimit: 2,
            DefaultMemoryLimit: "2g",
            DefaultTimeoutSeconds: 120,
            BaseCapabilities: [],
            ContainerEnvironment: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["NPM_CONFIG_REGISTRY"] = "https://gateway.example.invalid/npm/",
                ["UV_INDEX_URL"] = "https://gateway.example.invalid/uv/simple",
                ["PIP_INDEX_URL"] = "https://gateway.example.invalid/pip/simple",
                ["LOG_LEVEL"] = "debug",
            })
        {
            WorkspaceBaseRoot = "/tmp/kautuka",
        };

        List<string> arguments = InvokeBuildRunArguments(context, "kautuka-agent-alpha");

        AssertOption(arguments, "--add-host", $"kautuka-pkgs-proxy:{Kautuka.NetworkAccessController.NetworkAddressClassifier.PackageProxyServiceAddress}");

        Assert.DoesNotContain("NPM_CONFIG_REGISTRY", arguments);
        Assert.DoesNotContain("UV_INDEX_URL", arguments);
        Assert.DoesNotContain("PIP_INDEX_URL", arguments);
        Assert.DoesNotContain("LOG_LEVEL", arguments);
        Assert.DoesNotContain(arguments, static argument => argument.Contains("gateway.example.invalid", StringComparison.Ordinal));
        Assert.DoesNotContain(arguments, static argument => argument == "NPM_CONFIG_REGISTRY=http://kautuka-pkgs-proxy:20101/npm/");
        Assert.DoesNotContain(arguments, static argument => argument == "UV_INDEX_URL=http://kautuka-pkgs-proxy:20101/pypi/simple/");
        Assert.DoesNotContain(arguments, static argument => argument == "PIP_INDEX_URL=http://kautuka-pkgs-proxy:20101/pypi/simple/");
        Assert.DoesNotContain(arguments, static argument => argument == "PIP_TRUSTED_HOST=kautuka-pkgs-proxy");
    }

    [Fact]
    public void BuildRunArgumentsUsesWritableTmpfsHomeWithoutChangingHardenedUser()
    {
        ContainerContext context = new(
            ScopeType: "agent",
            ScopeId: "admin-research",
            UserId: "user-test",
            HostWorkspacePath: "/tmp/kautuka/admin-research",
            ContainerWorkspacePath: "/workspace",
            Image: "docker.io/example/agent@sha256:0000000000000000000000000000000000000000000000000000000000000001",
            DefaultCpuLimit: 2,
            DefaultMemoryLimit: "2g",
            DefaultTimeoutSeconds: 120,
            BaseCapabilities: [],
            ContainerEnvironment: new Dictionary<string, string>())
        {
            DesiredStateVersion = 7,
            SpecFingerprint = "abc123",
            WorkspaceBaseRoot = "/tmp/kautuka",
        };

        List<string> arguments = InvokeBuildRunArguments(context, "kautuka-agent-admin-research");

        Assert.DoesNotContain("--replace", arguments, StringComparer.Ordinal);
        Assert.Contains("--cap-drop", arguments, StringComparer.Ordinal);
        Assert.Contains("ALL", arguments, StringComparer.Ordinal);
        Assert.Contains("--read-only", arguments, StringComparer.Ordinal);
        Assert.Contains("--pids-limit", arguments, StringComparer.Ordinal);
        Assert.Contains("--tmpfs", arguments, StringComparer.Ordinal);
        AssertOption(
            arguments,
            "--tmpfs",
            "/home/node:rw,nodev,nosuid,mode=1777");
        Assert.DoesNotContain(arguments, static argument => argument.Contains("size=", StringComparison.Ordinal));
        Assert.Contains("kautuka.desired-version=7", arguments, StringComparer.Ordinal);
        Assert.Contains("kautuka.spec-fingerprint=abc123", arguments, StringComparer.Ordinal);
    }

    [Fact]
    public void BuildRunArgumentsDeniesEgressForUserScopeWithoutAnAgentIdentity()
    {
        ContainerContext context = new(
            ScopeType: "user",
            ScopeId: "user-alpha",
            UserId: "user-alpha",
            HostWorkspacePath: "/tmp/kautuka/user-alpha",
            ContainerWorkspacePath: "/workspace",
            Image: "localhost/kautuka-base:latest",
            DefaultCpuLimit: 1,
            DefaultMemoryLimit: "512m",
            DefaultTimeoutSeconds: 30,
            BaseCapabilities: [],
            ContainerEnvironment: new Dictionary<string, string>())
        {
            WorkspaceBaseRoot = "/tmp/kautuka",
        };

        IReadOnlyList<string> arguments = PodmanContainerTool.BuildRunArguments(
            context,
            "kautuka-user-user-alpha",
            "10.247.1.10",
            isBraked: false);

        Assert.Contains("io.kautuka.egress=deny-all", arguments, StringComparer.Ordinal);
        Assert.Contains("io.kautuka.user=user-alpha", arguments, StringComparer.Ordinal);
        Assert.Equal("user-alpha", PodmanContainerTool.BuildSandboxRegistryEntry(context, "10.247.1.10", "kautuka-user-user-alpha").UserId);
        Assert.DoesNotContain(arguments, static argument => argument.StartsWith("io.kautuka.agent=", StringComparison.Ordinal));
    }

    [Fact]
    public void V7_23_SandboxMountMustStayUnderConfiguredWorkspaceBaseRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "nac-v7-23-sandbox-root-" + Guid.NewGuid().ToString("N"));
        string outside = Path.Combine(Path.GetTempPath(), "nac-v7-23-sandbox-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        try
        {
            var context = new ContainerContext(
                "agent", "agent-a", "user-a", outside, "/workspace", "image@sha256:digest",
                1, "512m", 30, [], new Dictionary<string, string>())
            {
                WorkspaceBaseRoot = root,
            };

            Assert.Throws<ArgumentException>(() => PodmanContainerTool.BuildRunArguments(
                context,
                "sandbox-agent-a",
                "10.247.1.10",
                isBraked: false));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    private static void AssertOption(List<string> arguments, string option, string value)
    {
        for (int index = 0; index < arguments.Count - 1; index++)
        {
            if (string.Equals(arguments[index], option, StringComparison.Ordinal)
                && string.Equals(arguments[index + 1], value, StringComparison.Ordinal))
            {
                return;
            }
        }

        Assert.Fail($"Expected option '{option}' with value '{value}'.");
    }

    [Fact]
    public void EnsureWritableVolumePermissionsSetsRootModeAndLeavesNestedEntriesUntouched()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string root = Path.Combine(Path.GetTempPath(), "kautuka-sidecar-permissions", Guid.NewGuid().ToString("N"));
        string nestedDirectory = Path.Combine(root, "wal", "blocks");
        string nestedFile = Path.Combine(root, "queries.active");

        try
        {
            Directory.CreateDirectory(nestedDirectory);
            File.WriteAllText(nestedFile, "active");

            new DirectoryInfo(root).UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            new DirectoryInfo(Path.Combine(root, "wal")).UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            new DirectoryInfo(nestedDirectory).UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            new FileInfo(nestedFile).UnixFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            InvokeEnsureWritableVolumePermissions(root);

            // The workspace is owner/group writable, never world writable.
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute,
                new DirectoryInfo(root).UnixFileMode);

            // Nested entries must be left exactly as they were — they are owned by
            // container-mapped UIDs and the container process already has owner access.
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                new DirectoryInfo(nestedDirectory).UnixFileMode);

            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                new FileInfo(nestedFile).UnixFileMode);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static List<string> InvokeBuildRunArguments(ContainerContext context, string containerName)
    {
        MethodInfo method = typeof(PodmanContainerTool)
            .GetMethod("BuildRunArguments", BindingFlags.Static | BindingFlags.Public)
            ?? throw new InvalidOperationException("BuildRunArguments method was not found.");

        object? result = method.Invoke(null, [context, containerName, "10.247.1.10", false]);
        return Assert.IsAssignableFrom<IReadOnlyList<string>>(result).ToList();
    }

    private static void InvokeEnsureWritableVolumePermissions(string path)
    {
        MethodInfo method = typeof(WorkspaceSidecarSupervisor)
            .GetMethod("EnsureWritableVolumePermissions", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("EnsureWritableVolumePermissions method was not found.");

        method.Invoke(null, [path]);
    }

    private static ContainerContext CreateLaunchContext(string userId, string agentId, string hostPath)
        => new(
            "agent",
            agentId,
            userId,
            hostPath,
            "/workspace",
            "ghcr.io/devkrity/kautuka-base@sha256:" + new string('a', 64),
            2,
            "2g",
            120,
            [],
            new Dictionary<string, string>())
        {
            WorkspaceBaseRoot = Path.GetDirectoryName(hostPath),
        };

    private sealed class TestWorkloadStartupState(bool isStartupGateOpen) : IWorkloadStartupState
    {
        public bool IsStartupGateOpen { get; } = isStartupGateOpen;
    }
}
