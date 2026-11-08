namespace Kautuka.Manager.RuntimeContainer.Hosting;

public sealed partial class McpbImageMaterializer
{
    // The Gateway's /data mount is the Manager's gateway-data directory. Only
    // its managed bundle subtree crosses this namespace boundary; arbitrary
    // host paths retain their existing behavior.
    internal WorkspaceMcpContainerConnectorDefinition ResolveGatewayBundle(WorkspaceMcpContainerConnectorDefinition connector)
    {
        if (runtimePaths?.UsesManagedGatewayDataMount != true || connector.BundlePath is null) return connector;
        string mapped = ResolveGatewayBundlePath(connector.BundlePath, runtimePaths.SharedDataRoot);
        return mapped == connector.BundlePath ? connector : connector with { BundlePath = mapped };
    }

    internal static string ResolveGatewayBundlePath(string path, string sharedDataRoot)
    {
        const string prefix = "/data/mcpb-bundles/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return path;
        string relative = path[prefix.Length..];
        string[] parts = relative.Split('/');
        if (parts.Length == 0 || parts.Any(part => part is "" or "." or ".." || part.Contains('\\', StringComparison.Ordinal) || part.Contains('\0', StringComparison.Ordinal) || part.Contains(':', StringComparison.Ordinal) || Path.IsPathRooted(part)))
            throw new InvalidOperationException("The managed MCPB bundle path is invalid.");
        string gatewayRoot = Path.GetFullPath(Path.Combine(sharedDataRoot, "gateway-data"));
        CheckNotLink(gatewayRoot);
        string root = Path.Combine(gatewayRoot, "mcpb-bundles");
        string current = root;
        // Do not follow a link within the mapped subtree to an unrelated host file.
        CheckNotLink(current);
        foreach (string part in parts)
        {
            current = Path.Combine(current, part);
            CheckNotLink(current);
        }
        string resolved = Path.GetFullPath(current);
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!resolved.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new InvalidOperationException("The managed MCPB bundle path escapes its storage root.");
        return resolved;
    }

    private static void CheckNotLink(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("The managed MCPB bundle path contains a symbolic link.");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }
}
