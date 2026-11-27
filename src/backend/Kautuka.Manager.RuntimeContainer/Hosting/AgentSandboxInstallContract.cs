namespace Kautuka.Manager.RuntimeContainer.Hosting;

internal static class AgentSandboxInstallContract
{
    private const string installRoot = "/home/node/.kautuka/connectors";

    public static string BuildInstallRoot(WorkspaceMcpContainerConnectorDefinition connector, string? bundleDigest = null)
    {
        ArgumentNullException.ThrowIfNull(connector);
        string digest = ExtractDigest(McpbImageMaterializer.BuildSandboxArtifactImageReference(
            connector.ServerType ?? throw new InvalidOperationException($"Container connector '{connector.ConnectorId}' is missing server_type."),
            bundleDigest ?? McpbImageMaterializer.ComputeSha256Hex(connector.BundlePath
                ?? throw new InvalidOperationException($"Container connector '{connector.ConnectorId}' is missing bundle_path."))));
        return $"{installRoot}/by-digest/{digest}";
    }

    public static string QuoteShell(string value)
        => $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    public static WorkspaceMcpContainerConnectorDefinition ApplyRuntime(
        WorkspaceMcpContainerConnectorDefinition connector,
        string runtimeImage)
    {
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeImage);
        (string command, IReadOnlyList<string> args) = RewriteLaunch(
            connector.ServerType,
            McpbImageMaterializer.RewriteBundlePlaceholder(connector.Command),
            [.. connector.Args.Select(McpbImageMaterializer.RewriteBundlePlaceholder)],
            McpbImageMaterializer.BundleWorkdir);
        return connector with
        {
            Image = runtimeImage,
            BundlePath = null,
            Command = command,
            Args = args,
            Environment = [.. connector.Environment.Select(static pair => new KeyValuePair<string, string>(pair.Key, McpbImageMaterializer.RewriteBundlePlaceholder(pair.Value)))],
        };
    }

    public static (string Command, IReadOnlyList<string> Args) RewriteLaunch(
        string? serverType,
        string command,
        IReadOnlyList<string> args,
        string bundleRoot)
    {
        if (!string.Equals(serverType, "uv", StringComparison.OrdinalIgnoreCase)
            || (!string.Equals(command, "uv", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(command, "uvx", StringComparison.OrdinalIgnoreCase))
            || args.Count == 0
            || !string.Equals(args[0], "run", StringComparison.OrdinalIgnoreCase))
        {
            return (command, args);
        }

        List<string> remaining = [.. args.Skip(1)];
        if (remaining.Count == 0)
        {
            return (command, args);
        }

        if (string.Equals(remaining[0], "python", StringComparison.OrdinalIgnoreCase)
            || string.Equals(remaining[0], "python3", StringComparison.OrdinalIgnoreCase)
            || string.Equals(remaining[0], "python3.12", StringComparison.OrdinalIgnoreCase))
        {
            remaining.RemoveAt(0);
            return ($"{bundleRoot}/.venv/bin/python", remaining);
        }

        if (remaining[0][0] == '-')
        {
            return ($"{bundleRoot}/.venv/bin/python", remaining);
        }

        string entryPoint = remaining[0];
        remaining.RemoveAt(0);
        return ($"{bundleRoot}/.venv/bin/{entryPoint}", remaining);
    }

    private static string ExtractDigest(string runtimeImage)
    {
        int separator = runtimeImage.LastIndexOf(':');
        return separator >= 0 && separator + 1 < runtimeImage.Length ? runtimeImage[(separator + 1)..] : runtimeImage;
    }
}
