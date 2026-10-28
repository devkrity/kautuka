namespace Kautuka.Gateway.Domain.Connectors;

internal static class McpCommandTransportMapper
{
    // Node, Python, uv, and native binaries run in the finished sandbox image for this release.
    // A published runtime-images.json entry id kautuka-base supplies the digest.
    // Development, with no release entry, uses the local image.
    public static string NodeImage => ReleasedSandboxImage.Current;
    public static string PythonImage => ReleasedSandboxImage.Current;
    public static string UvImage => ReleasedSandboxImage.Current;
    public static string BinaryImage => ReleasedSandboxImage.Current;

    public static bool TryMapLocalCommandToContainer(
        string? command,
        IReadOnlyList<string>? args,
        out MappedContainerTransport mapped)
    {
        string normalizedCommand = (command ?? string.Empty).Trim();
        IReadOnlyList<string> normalizedArgs = args ?? [];

        if (string.IsNullOrWhiteSpace(normalizedCommand))
        {
            mapped = default;
            return false;
        }

        if (string.Equals(normalizedCommand, "npx", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedCommand, "node", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedCommand, "nodejs", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedCommand, "npm", StringComparison.OrdinalIgnoreCase))
        {
            mapped = new MappedContainerTransport(NodeImage, normalizedCommand, [.. normalizedArgs]);
            return true;
        }

        if (string.Equals(normalizedCommand, "python", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedCommand, "python3", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedCommand, "python3.12", StringComparison.OrdinalIgnoreCase))
        {
            mapped = new MappedContainerTransport(PythonImage, normalizedCommand, [.. normalizedArgs]);
            return true;
        }

        if (string.Equals(normalizedCommand, "uv", StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedCommand, "uvx", StringComparison.OrdinalIgnoreCase))
        {
            mapped = new MappedContainerTransport(UvImage, normalizedCommand, [.. normalizedArgs]);
            return true;
        }

        mapped = default;
        return false;
    }

    public static string BuildUnsupportedCommandMessage(string? command)
        => $"Command-based MCP connectors must map to a managed container runtime. Unsupported command '{command ?? string.Empty}'. Supported commands are npx/node, python/python3, and uv/uvx.";
}

internal readonly record struct MappedContainerTransport(
    string ContainerImage,
    string ContainerCommand,
    string[] ContainerArgs);
