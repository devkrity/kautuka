using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using Kautuka.Manager.Protos;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.Manager.RuntimeContainer.Sandbox.Models;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

internal static class ConnectorContainerSpecBuilder
{
    internal const string SandboxNetworkName = "kautuka-sandbox";
    internal const string WorkloadNetworkName = "kautuka-workload";

    internal readonly record struct ConnectorIdentity(
        string UserId,
        string AgentId,
        bool Systemwide,
        string Egress,
        string Kind);

    /// <summary>
    /// Single source of truth for connector workload identity (v7-22). The owner comes only from the
    /// workspace context; it is never guessed from the connector name. Internal system connectors remain
    /// deny-all even when a user context is present; a non-internal connector with no owner also gets deny-all.
    /// </summary>
    internal static ConnectorIdentity ResolveIdentity(
        WorkspaceMcpContainerConnectorDefinition connector,
        ContainerContext? workspaceContext,
        string? purpose)
    {
        ArgumentNullException.ThrowIfNull(connector);
        string userId = workspaceContext?.UserId ?? string.Empty;
        string agentId = string.Equals(workspaceContext?.ScopeType, "agent", StringComparison.OrdinalIgnoreCase)
            ? (workspaceContext?.ScopeId ?? string.Empty)
            : string.Empty;
        bool systemwide = string.Equals(connector.CatalogScope, "system", StringComparison.OrdinalIgnoreCase);

        if (connector.IsInternalSystemConnector)
        {
            return new ConnectorIdentity(
                userId,
                string.Empty,
                systemwide,
                "deny-all",
                "system");
        }

        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new InvalidOperationException("A user identity is required before starting a connector workload.");
        }

        if (!systemwide && string.IsNullOrWhiteSpace(agentId) && !string.Equals(purpose, "probe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("An agent identity is required before starting a connector workload.");
        }

        return new ConnectorIdentity(userId, systemwide ? string.Empty : agentId, systemwide, "normal", "connector");
    }

    public static List<string> BuildRun(
        WorkspaceMcpContainerConnectorDefinition connector,
        string containerName,
        WorkspaceMcpExecutionContext executionContext,
        ContainerContext? workspaceContext,
        bool persistent,
        string allocatedIp,
        bool isBraked,
        string? purpose = null,
        ImageCompatProfile? compatProfile = null,
        ContainerContext? identityContext = null)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentException.ThrowIfNullOrWhiteSpace(containerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(allocatedIp);

        ConnectorIdentity identity = ResolveIdentity(connector, identityContext ?? workspaceContext, purpose);
        string userId = identity.UserId;
        string agentId = identity.AgentId;

        string execContext = executionContext switch
        {
            WorkspaceMcpExecutionContext.Isolated => "isolated",
            WorkspaceMcpExecutionContext.IsolatedWithAgentFiles => "isolated-with-agent-files",
            WorkspaceMcpExecutionContext.AgentSandbox => "agent-sandbox",
            _ => "isolated"
        };

        bool systemwide = identity.Systemwide;
        string egress = identity.Egress;
        string effectiveIp = allocatedIp;

        var envList = new List<KeyValuePair<string, string>>
        {
            new("HOME", "/tmp"),
            new("NPM_CONFIG_CACHE", "/tmp/.npm"),
            new("PIP_CACHE_DIR", "/tmp/.pip"),
            new("UV_CACHE_DIR", "/tmp/.uv"),
        };

        foreach (KeyValuePair<string, string> pair in ForwardedEnvironment(connector))
        {
            envList.Add(new KeyValuePair<string, string>(pair.Key, string.Empty));
        }

        var mounts = new List<WorkloadMount>();
        if (workspaceContext is not null
            && executionContext == WorkspaceMcpExecutionContext.IsolatedWithAgentFiles)
        {
            mounts.Add(new WorkloadMount(workspaceContext.HostWorkspacePath, workspaceContext.ContainerWorkspacePath, ReadOnly: true));
        }

        var cmdArgs = new List<string>();
        string? command = null;
        if (!persistent)
        {
            if (!string.IsNullOrWhiteSpace(connector.Command))
            {
                command = connector.Command;
                cmdArgs.AddRange(BuildIndexPinnedArguments(connector));
            }
            else if (connector.Args.Count > 0)
            {
                cmdArgs.AddRange(connector.Args);
            }
        }

        var spec = new WorkloadContainerSpec(containerName, connector.Image, effectiveIp)
        {
            UserId = userId,
            AgentId = agentId,
            ConnectorId = connector.ConnectorId,
            ExecContext = execContext,
            Kind = identity.Kind,
            Systemwide = systemwide,
            Egress = egress,
            Purpose = purpose,
            Persistent = persistent,
            DesiredStateVersion = connector.DesiredStateVersion,
            SpecFingerprint = ComputeWorkloadFingerprint(connector, identity, executionContext, compatProfile),
            ManagerRole = "connector",
            StartupGateOpen = true,
            IsBraked = isBraked,
            Environment = envList,
            Mounts = mounts,
            AllowedMountRoots = workspaceContext?.WorkspaceBaseRoot is { } workspaceBaseRoot ? [workspaceBaseRoot] : [],
            Command = command,
            Args = cmdArgs,
            PidsLimit = 128,
            CpuLimit = 1,
            MemoryLimit = "536870912",
            CompatibilityProfile = compatProfile,
        };

        var arguments = spec.Render().ToList();
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

    public static string ComputeFingerprint(
        WorkspaceMcpContainerConnectorDefinition connector,
        string? userId = null,
        string? agentId = null,
        string? execContext = null,
        string? kind = null,
        bool systemwide = false,
        string? egress = null,
        string? compat = null)
    {
        ArgumentNullException.ThrowIfNull(connector);
        var builder = new StringBuilder();
        Append(builder, connector.ConnectorId);
        Append(builder, connector.Image);
        Append(builder, connector.Command);
        Append(builder, connector.Enabled ? "1" : "0");
        Append(builder, connector.IsInternalSystemConnector ? "1" : "0");
        Append(builder, connector.CatalogScope);
        Append(builder, connector.BundlePath ?? string.Empty);
        Append(builder, connector.ServerType ?? string.Empty);
        Append(builder, connector.ReadinessTimeoutSeconds.ToString(CultureInfo.InvariantCulture));
        Append(builder, connector.JitCooldownSeconds.ToString(CultureInfo.InvariantCulture));
        Append(builder, WorkloadNetworkName);
        Append(builder, userId ?? string.Empty);
        Append(builder, agentId ?? string.Empty);
        Append(builder, connector.ConnectorId);
        Append(builder, execContext ?? "isolated");
        Append(builder, kind ?? "connector");
        Append(builder, systemwide ? "true" : "false");
        Append(builder, egress ?? "normal");
        Append(builder, compat ?? string.Empty);
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

    internal static string ComputeWorkloadFingerprint(
        WorkspaceMcpContainerConnectorDefinition connector,
        ConnectorIdentity identity,
        WorkspaceMcpExecutionContext executionContext,
        ImageCompatProfile? compatProfile = null)
    {
        string execContext = executionContext switch
        {
            WorkspaceMcpExecutionContext.Isolated => "isolated",
            WorkspaceMcpExecutionContext.IsolatedWithAgentFiles => "isolated-with-agent-files",
            WorkspaceMcpExecutionContext.AgentSandbox => "agent-sandbox",
            _ => "isolated",
        };
        return ComputeFingerprint(
            connector,
            identity.UserId,
            identity.AgentId,
            execContext,
            identity.Kind,
            identity.Systemwide,
            identity.Egress,
            compatProfile?.ImageDigest);
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
