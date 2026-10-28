using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Kautuka.Manager.Protos;
using Kautuka.Manager.RuntimeContainer.Sandbox.Models;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

internal static class ConnectorContainerSpecBuilder
{
    private const string nonRootUser = "65532:65532";
    internal const string SandboxNetworkName = "kautuka-sandbox";

    public static List<string> BuildRun(
        WorkspaceMcpContainerConnectorDefinition connector,
        string containerName,
        WorkspaceMcpExecutionContext executionContext,
        ContainerContext? workspaceContext,
        bool persistent)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        ContainerImageProvenancePolicy.EnsureTrusted(connector.Image, $"Connector '{connector.ConnectorId}'");
        var arguments = new List<string> { "run" };
        if (persistent)
        {
            arguments.Add("-d");
        }
        else
        {
            arguments.AddRange(["--rm", "-i"]);
        }

        arguments.AddRange([
            "--user", nonRootUser,
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--pids-limit", "128",
            "--cpus", "1",
            "--memory", "536870912",
            "--read-only",
            // tmpfs pages are charged to the container's memory cgroup. Do not add a
            // mount-level size option: crun on Podman Machine/WSL rejects sized tmpfs
            // mounts with ENOSPC before the process starts.
            "--tmpfs", "/tmp:rw,noexec,nosuid",
            "--name", containerName,
            "--label", "kautuka.managed=manager",
            "--label", "kautuka.role=connector",
            "--label", $"kautuka.desired-version={connector.DesiredStateVersion.ToString(CultureInfo.InvariantCulture)}",
            "--label", $"kautuka.spec-fingerprint={connector.SpecFingerprint}",
            // Isolation is a private container plus optional read-only agent-file
            // mounts. Every connector still joins kautuka-sandbox so aardvark can
            // resolve sidecar aliases (kautuka-pkgs-proxy) and public DNS.
            "--network", SandboxNetworkName,
            "--env", "HOME=/tmp",
            "--env", "NPM_CONFIG_CACHE=/tmp/.npm",
            "--env", "PIP_CACHE_DIR=/tmp/.pip",
            "--env", "UV_CACHE_DIR=/tmp/.uv"]);

        foreach (KeyValuePair<string, string> pair in ForwardedEnvironment(connector))
        {
            arguments.Add("--env");
            arguments.Add(pair.Key);
        }

        if (workspaceContext is not null
            && executionContext == WorkspaceMcpExecutionContext.IsolatedWithAgentFiles)
        {
            arguments.Add("--volume");
            arguments.Add($"{workspaceContext.HostWorkspacePath}:{workspaceContext.ContainerWorkspacePath}:ro");
        }

        arguments.Add(connector.Image);
        if (persistent)
        {
            arguments.AddRange(["sh", "-lc", "tail -f /dev/null"]);
        }
        else
        {
            AppendCommand(arguments, connector);
        }

        EnsureHostProcessArguments(arguments);
        return arguments;
    }

    public static List<string> BuildExec(
        WorkspaceMcpContainerConnectorDefinition connector,
        string containerName,
        string? workingDirectory)
    {
        ArgumentNullException.ThrowIfNull(connector);
        var arguments = new List<string> { "exec", "-i" };
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            arguments.AddRange(["--workdir", workingDirectory]);
        }

        foreach (KeyValuePair<string, string> pair in ForwardedEnvironment(connector))
        {
            arguments.AddRange(["--env", pair.Key]);
        }

        arguments.Add(containerName);
        AppendCommand(arguments, connector);
        EnsureHostProcessArguments(arguments);
        return arguments;
    }

    public static Dictionary<string, string?> BuildTransportEnvironment(WorkspaceMcpContainerConnectorDefinition connector)
        => ForwardedEnvironment(connector)
            .ToDictionary(static pair => pair.Key, static pair => (string?)pair.Value, StringComparer.Ordinal);

    internal static bool ForwardsEnvironmentKey(string key)
        => !string.IsNullOrWhiteSpace(key) && !IsPackageIndexBypassKey(key);

    internal static bool AllowsRuntimeEnvironmentOverride(string key)
        => ForwardsEnvironmentKey(key) && !IsHostProcessSensitiveKey(key);

    private static readonly HashSet<string> hostProcessSensitiveKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "PATHEXT",
        "LD_PRELOAD", "LD_LIBRARY_PATH", "LD_AUDIT",
        "DYLD_INSERT_LIBRARIES", "DYLD_LIBRARY_PATH",
        "NODE_OPTIONS", "NODE_PATH",
        "PYTHONPATH", "PYTHONSTARTUP", "PYTHONHOME",
        "BASH_ENV", "SHELLOPTS",
        "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY",
        "SSLKEYLOGFILE",
        "HOME", "USERPROFILE", "HOMEDRIVE", "HOMEPATH",
        "TMPDIR", "TEMP", "TMP",
        "XDG_CONFIG_HOME", "XDG_RUNTIME_DIR", "XDG_DATA_HOME", "XDG_CACHE_HOME",
        "APPDATA", "LOCALAPPDATA",
        "COMSPEC", "SYSTEMROOT", "WINDIR",
        "CONTAINERS_CONF", "CONTAINERS_CONF_OVERRIDE", "CONTAINERS_STORAGE_CONF", "CONTAINERS_REGISTRIES_CONF",
        "CONTAINERS_HELPER_BINARY_DIR",
        "CONTAINER_HOST", "CONTAINER_SSHKEY", "CONTAINER_PASSPHRASE", "CONTAINER_CONNECTION",
        "DOCKER_HOST", "DOCKER_CONFIG", "DOCKER_CERT_PATH", "DOCKER_TLS_VERIFY", "DOCKER_CONTEXT",
        "REGISTRY_AUTH_FILE", "PODMAN_TMPDIR",
        "STORAGE_OPTS",
    };

    private static bool IsHostProcessSensitiveKey(string key)
        => hostProcessSensitiveKeys.Contains(key);

    private static IEnumerable<KeyValuePair<string, string>> ForwardedEnvironment(WorkspaceMcpContainerConnectorDefinition connector)
        => connector.Environment.Where(static pair => ForwardsEnvironmentKey(pair.Key));

    private static bool IsPackageIndexBypassKey(string key)
    {
        if (key.Equals("NPM_CONFIG_REGISTRY", StringComparison.Ordinal))
        {
            return false;
        }

        return key.Equals("npm_config_registry", StringComparison.OrdinalIgnoreCase)
            || key.Equals("npm_config_userconfig", StringComparison.OrdinalIgnoreCase)
            || key.Equals("npm_config_globalconfig", StringComparison.OrdinalIgnoreCase)
            || key.Equals("UV_EXTRA_INDEX_URL", StringComparison.OrdinalIgnoreCase)
            || key.Equals("UV_INDEX", StringComparison.OrdinalIgnoreCase)
            || key.Equals("UV_CONFIG_FILE", StringComparison.OrdinalIgnoreCase)
            || key.Equals("PIP_EXTRA_INDEX_URL", StringComparison.OrdinalIgnoreCase)
            || key.Equals("PIP_CONFIG_FILE", StringComparison.OrdinalIgnoreCase);
    }

    public static string ComputeFingerprint(WorkspaceMcpContainerConnectorDefinition connector)
    {
        ArgumentNullException.ThrowIfNull(connector);
        var builder = new StringBuilder();
        Append(builder, connector.ConnectorId);
        Append(builder, connector.Image);
        Append(builder, connector.Command);
        Append(builder, connector.Enabled ? "1" : "0");
        Append(builder, connector.IsInternalSystemConnector ? "1" : "0");
        Append(builder, connector.BundlePath ?? string.Empty);
        Append(builder, connector.ServerType ?? string.Empty);
        Append(builder, connector.ReadinessTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
        Append(builder, connector.JitCooldownSeconds.ToString(CultureInfo.InvariantCulture));
        Append(builder, SandboxNetworkName);
        foreach (string argument in connector.Args)
        {
            Append(builder, argument);
        }

        foreach ((string key, string value) in connector.Environment.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            Append(builder, key);
            Append(builder, value);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    internal static void EnsureHostProcessArguments(IReadOnlyList<string> arguments)
    {
        // ModelContextProtocol.Core 2.2.0 starts this process through cmd.exe /c on Windows.
        // A quote is parsed again, and a percent sign is expanded from the environment.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (string argument in arguments)
        {
            if (argument.IndexOfAny(['"', '%', '\r', '\n', '\0']) >= 0)
            {
                throw new InvalidOperationException("Connector command arguments cannot contain a quote, a percent sign, or a line break on Windows.");
            }
        }
    }

    private static void AppendCommand(List<string> arguments, WorkspaceMcpContainerConnectorDefinition connector)
    {
        if (!string.IsNullOrWhiteSpace(connector.Command))
        {
            arguments.Add(connector.Command);
        }

        arguments.AddRange(BuildIndexPinnedArguments(connector));
    }

    private static List<string> BuildIndexPinnedArguments(WorkspaceMcpContainerConnectorDefinition connector)
    {
        List<string> arguments = [.. connector.Args.Where(static argument => !string.IsNullOrWhiteSpace(argument))];
        string command = connector.Command ?? string.Empty;

        if (string.Equals(command, "npx", StringComparison.OrdinalIgnoreCase)
            || string.Equals(command, "npm", StringComparison.OrdinalIgnoreCase))
        {
            string? registry = FindEnvironmentValue(connector, "NPM_CONFIG_REGISTRY");
            if (string.IsNullOrWhiteSpace(registry))
            {
                return arguments;
            }

            arguments = StripOptionFlags(arguments, "--registry", "--userconfig", "--globalconfig");
            arguments.Insert(0, registry);
            arguments.Insert(0, "--registry");
            return arguments;
        }

        if (string.Equals(command, "uv", StringComparison.OrdinalIgnoreCase)
            || string.Equals(command, "uvx", StringComparison.OrdinalIgnoreCase))
        {
            string? indexUrl = FindEnvironmentValue(connector, "UV_INDEX_URL")
                ?? FindEnvironmentValue(connector, "UV_DEFAULT_INDEX")
                ?? FindEnvironmentValue(connector, "PIP_INDEX_URL");
            if (string.IsNullOrWhiteSpace(indexUrl))
            {
                return arguments;
            }

            arguments = StripOptionFlags(arguments, "--index-url", "--default-index", "--extra-index-url", "--index");
            arguments.Insert(0, indexUrl);
            arguments.Insert(0, "--default-index");
            arguments.Insert(0, indexUrl);
            arguments.Insert(0, "--index-url");
            return arguments;
        }

        return arguments;
    }

    private static string? FindEnvironmentValue(WorkspaceMcpContainerConnectorDefinition connector, string key)
    {
        foreach (KeyValuePair<string, string> pair in connector.Environment)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(pair.Value))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static List<string> StripOptionFlags(List<string> arguments, params string[] flags)
    {
        var stripped = new List<string>(arguments.Count);
        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (string.Equals(argument, "--", StringComparison.Ordinal))
            {
                stripped.AddRange(arguments.Skip(index));
                break;
            }

            string? matchedFlag = flags.FirstOrDefault(flag =>
                string.Equals(argument, flag, StringComparison.Ordinal)
                || argument.StartsWith(flag + "=", StringComparison.Ordinal));
            if (matchedFlag is not null)
            {
                if (string.Equals(argument, matchedFlag, StringComparison.Ordinal)
                    && index + 1 < arguments.Count
                    && !arguments[index + 1].StartsWith('-'))
                {
                    index++;
                }

                continue;
            }

            stripped.Add(argument);
        }

        return stripped;
    }

    private static void Append(StringBuilder builder, string value)
        => builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('|');
}
