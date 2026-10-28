using Kautuka.Hosting.Hosting;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kautuka.Manager.RuntimeContainer.Configuration;

public sealed class WorkspaceRuntimePaths
{
    public WorkspaceRuntimePaths(IConfiguration configuration, IHostEnvironment hostEnvironment, ILogger<WorkspaceRuntimePaths> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(hostEnvironment);
        ArgumentNullException.ThrowIfNull(logger);

        KautukaHostPathLayout layout = KautukaHostPathPolicy.Resolve(configuration);
        string runtimeRoot = KautukaHostPathPolicy.ResolveServiceStateRoot(configuration, "manager");

        string normalizedRuntimeRoot = Path.GetFullPath(runtimeRoot);

        try
        {
            EnsureRuntimeDirectories(normalizedRuntimeRoot);
        }
        catch (Exception ex) when (layout.Mode == KautukaHostMode.Prod && IsPermissionLikeFailure(ex))
        {
            throw new InvalidOperationException(
                $"Runtime root '{normalizedRuntimeRoot}' is not writable for prod mode.",
                ex);
        }

        RuntimeRoot = normalizedRuntimeRoot;
        SharedDataRoot = KautukaHostPathPolicy.ResolveSharedStateRoot(configuration);
        OciImagesRoot = Path.GetFullPath(Path.Combine(SharedDataRoot, "oci-images"));
        Directory.CreateDirectory(OciImagesRoot);

        LogsRoot = Path.GetFullPath(layout.LogsRoot);
        Directory.CreateDirectory(LogsRoot);

        RequireSidecars = !hostEnvironment.IsDevelopment();

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Workspace runtime paths resolved. root={RuntimeRoot} requireSidecars={RequireSidecars}",
                RuntimeRoot,
                RequireSidecars);
        }
    }

    public string RuntimeRoot { get; }

    public string LogsRoot { get; }

    public string SharedDataRoot { get; }

    public string OciImagesRoot { get; }

    public bool RequireSidecars { get; }

    private static void EnsureRuntimeDirectories(string runtimeRoot)
    {
        Directory.CreateDirectory(runtimeRoot);
    }

    private static bool IsPermissionLikeFailure(Exception ex)
        => ex is UnauthorizedAccessException
            || ex is IOException ioEx && ioEx.Message.Contains("Permission", StringComparison.OrdinalIgnoreCase);
}
