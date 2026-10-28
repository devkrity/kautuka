using Microsoft.Extensions.Configuration;

namespace Kautuka.Hosting.Hosting;

public enum KautukaHostMode
{
    Dev,
    Prod,
}

public sealed record KautukaHostPathLayout(
    KautukaHostMode Mode,
    string InternalStateRoot,
    string VisibleRoot,
    string LogsRoot,
    string SharedDataRoot);

public static class KautukaHostPathPolicy
{
    private const string devFolderName = ".kautuka";
    private const string runtimeFolderName = "runtime";
    private const string logsFolderName = "logs";

    public static KautukaHostPathLayout Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return ResolveMode(configuration) switch
        {
            KautukaHostMode.Prod => CreateProdLayout(),
            _ => CreateDevLayout(),
        };
    }

    public static string ResolveSharedStateRoot(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        string? containerStateRoot = configuration["Kautuka:ContainerStateRoot"];
        if (!string.IsNullOrWhiteSpace(containerStateRoot))
        {
            return Path.GetFullPath(containerStateRoot);
        }

        return Resolve(configuration).SharedDataRoot;
    }

    public static string ResolveServiceStateRoot(IConfiguration configuration, string serviceDirectoryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceDirectoryName);

        return Path.Combine(ResolveSharedStateRoot(configuration), serviceDirectoryName);
    }

    public static string ResolveSharedFilePath(IConfiguration configuration, string fileName)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        return Path.Combine(ResolveSharedStateRoot(configuration), fileName);
    }

    public static string ResolveServiceFilePath(
        IConfiguration configuration,
        string serviceDirectoryName,
        string fileName)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceDirectoryName);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        return Path.Combine(ResolveServiceStateRoot(configuration, serviceDirectoryName), fileName);
    }

    private static KautukaHostMode ResolveMode(IConfiguration configuration)
    {
        string? configuredMode = configuration["Kautuka:Mode"];
        if (TryParseMode(configuredMode, out KautukaHostMode mode))
        {
            return mode;
        }

        if (IsDevelopmentEnvironment(configuration))
        {
            return KautukaHostMode.Dev;
        }

        return KautukaHostMode.Prod;
    }

    private static bool TryParseMode(string? value, out KautukaHostMode mode)
    {
        if (string.Equals(value, "prod", StringComparison.OrdinalIgnoreCase))
        {
            mode = KautukaHostMode.Prod;
            return true;
        }

        if (string.Equals(value, "dev", StringComparison.OrdinalIgnoreCase))
        {
            mode = KautukaHostMode.Dev;
            return true;
        }

        mode = KautukaHostMode.Dev;
        return false;
    }

    private static bool IsDevelopmentEnvironment(IConfiguration configuration)
    {
        string? aspNetCoreEnvironment = configuration["ASPNETCORE_ENVIRONMENT"];
        if (string.Equals(aspNetCoreEnvironment, "Development", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string? dotNetEnvironment = configuration["DOTNET_ENVIRONMENT"];
        return string.Equals(dotNetEnvironment, "Development", StringComparison.OrdinalIgnoreCase);
    }

    private static KautukaHostPathLayout CreateDevLayout()
    {
        string visibleRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            devFolderName);
        string internalStateRoot = Path.Combine(visibleRoot, runtimeFolderName);

        return CreateLayout(
            KautukaHostMode.Dev,
            internalStateRoot,
            visibleRoot);
    }

    private static KautukaHostPathLayout CreateProdLayout()
        => CreateLayout(
            KautukaHostMode.Prod,
            internalStateRoot: "/var/lib/kautuka",
            visibleRoot: "/var/lib/kautuka");

    private static KautukaHostPathLayout CreateLayout(
        KautukaHostMode mode,
        string internalStateRoot,
        string visibleRoot)
    {
        string normalizedInternalStateRoot = Path.GetFullPath(internalStateRoot);
        string normalizedVisibleRoot = Path.GetFullPath(visibleRoot);
        string logsRoot = Path.Combine(normalizedVisibleRoot, logsFolderName);

        return new KautukaHostPathLayout(
            mode,
            normalizedInternalStateRoot,
            normalizedVisibleRoot,
            logsRoot,
            normalizedInternalStateRoot);
    }
}
