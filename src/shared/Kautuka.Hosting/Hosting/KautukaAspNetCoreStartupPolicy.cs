namespace Kautuka.Hosting.Hosting;

public static class KautukaAspNetCoreStartupPolicy
{
    private const string hostingStartupAssembliesEnvVar = "ASPNETCORE_HOSTINGSTARTUPASSEMBLIES";
    private const string browserRefreshAssemblyName = "Microsoft.AspNetCore.Watch.BrowserRefresh";

    public static void SanitizeHostingStartupAssemblies()
    {
        string? hostingStartupAssemblies = Environment.GetEnvironmentVariable(hostingStartupAssembliesEnvVar);
        if (string.IsNullOrWhiteSpace(hostingStartupAssemblies)
            || !hostingStartupAssemblies.Contains(browserRefreshAssemblyName, StringComparison.Ordinal))
        {
            return;
        }

        string[] filtered = hostingStartupAssemblies
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static assembly => !assembly.Equals(browserRefreshAssemblyName, StringComparison.Ordinal))
            .ToArray();

        Environment.SetEnvironmentVariable(
            hostingStartupAssembliesEnvVar,
            filtered.Length == 0 ? null : string.Join(';', filtered));
    }
}
