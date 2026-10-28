using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;

using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Sandbox;
using Kautuka.Manager.RuntimeContainer.Sandbox.Models;

namespace Kautuka.IntegrationTests;

public sealed class PodmanContainerToolTests
{
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
            UserId: null,
            HostWorkspacePath: "/tmp/kautuka/alpha",
            ContainerWorkspacePath: "/workspace",
            Image: "docker.io/example/agent:latest",
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
            });

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
    public void BuildRunArgumentsIncludesReplaceForStableSandboxNameReuse()
    {
        ContainerContext context = new(
            ScopeType: "agent",
            ScopeId: "admin-research",
            UserId: null,
            HostWorkspacePath: "/tmp/kautuka/admin-research",
            ContainerWorkspacePath: "/workspace",
            Image: "docker.io/example/agent:latest",
            DefaultCpuLimit: 2,
            DefaultMemoryLimit: "2g",
            DefaultTimeoutSeconds: 120,
            BaseCapabilities: [],
            ContainerEnvironment: new Dictionary<string, string>())
        {
            DesiredStateVersion = 7,
            SpecFingerprint = "abc123",
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
            "/home/node/.kautuka:rw,nodev,nosuid,mode=1777");
        Assert.DoesNotContain(arguments, static argument => argument.Contains("size=", StringComparison.Ordinal));
        Assert.Contains("kautuka.desired-version=7", arguments, StringComparer.Ordinal);
        Assert.Contains("kautuka.spec-fingerprint=abc123", arguments, StringComparer.Ordinal);
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
            .GetMethod("BuildRunArguments", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("BuildRunArguments method was not found.");

        object? result = method.Invoke(null, [context, containerName]);
        return Assert.IsType<List<string>>(result);
    }

    private static void InvokeEnsureWritableVolumePermissions(string path)
    {
        MethodInfo method = typeof(WorkspaceSidecarSupervisor)
            .GetMethod("EnsureWritableVolumePermissions", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("EnsureWritableVolumePermissions method was not found.");

        method.Invoke(null, [path]);
    }
}
