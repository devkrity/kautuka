namespace Kautuka.Gateway.Infrastructure.Sandbox;

internal static class WorkspaceHostRootPolicy
{
    private const string workspaceFolderName = "workspaces";
    private const string workspaceBaseRootConfigurationKey = "Kautuka:Workspace:BaseRoot";

    public static string ResolveWorkspaceBaseRoot(IConfiguration configuration, string? configuredBaseRoot)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (!string.IsNullOrWhiteSpace(configuredBaseRoot))
        {
            return Path.GetFullPath(configuredBaseRoot);
        }

        string? configurationBaseRoot = configuration[workspaceBaseRootConfigurationKey];
        if (!string.IsNullOrWhiteSpace(configurationBaseRoot))
        {
            return Path.GetFullPath(configurationBaseRoot);
        }

        return Path.Combine(KautukaHostPathPolicy.Resolve(configuration).VisibleRoot, workspaceFolderName);
    }
}
