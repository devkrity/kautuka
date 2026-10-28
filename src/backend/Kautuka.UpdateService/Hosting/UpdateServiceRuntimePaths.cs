using Kautuka.Hosting.Hosting;

namespace Kautuka.UpdateService.Hosting;

public sealed class UpdateServiceRuntimePaths
{
    public UpdateServiceRuntimePaths(IConfiguration configuration)
    {
        RuntimeRoot = Path.GetFullPath(KautukaHostPathPolicy.ResolveServiceStateRoot(configuration, "updateService"));
        SharedDataRoot = KautukaHostPathPolicy.ResolveSharedStateRoot(configuration);
        LogsRoot = Path.GetFullPath(KautukaHostPathPolicy.Resolve(configuration).LogsRoot);

        Directory.CreateDirectory(RuntimeRoot);
        Directory.CreateDirectory(LogsRoot);
    }

    public string RuntimeRoot { get; }

    public string SharedDataRoot { get; }

    public string LogsRoot { get; }
}
