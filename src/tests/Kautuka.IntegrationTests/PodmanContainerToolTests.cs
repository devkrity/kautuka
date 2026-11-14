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

        Assert.Contains("NPM_CONFIG_REGISTRY", arguments);
        Assert.Contains("UV_INDEX_URL", arguments);
        Assert.Contains("PIP_INDEX_URL", arguments);
        Assert.Contains("LOG_LEVEL", arguments);
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
    public void BuildRunArgumentsRejectsUserScopeWithoutAnAgentIdentity()
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
            ContainerEnvironment: new Dictionary<string, string>());

        ArgumentException exception = Assert.Throws<ArgumentException>(
            () => PodmanContainerTool.BuildRunArguments(context, "kautuka-user-user-alpha", "10.247.1.10", isBraked: false));

        Assert.Contains("agentId", exception.Message, StringComparison.Ordinal);
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
