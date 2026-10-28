using Microsoft.Extensions.Logging;

// Logging source generators for ManagerRuntimeAdapter (Podman appliance).
namespace Kautuka.Manager.RuntimeContainer.Hosting;

public sealed partial class ManagerRuntimeAdapter
{
    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Stopping {Count} sandbox container(s) in parallel.")]
    private static partial void LogStoppingContainers(ILogger logger, int count);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Failed to stop container {ContainerName}.")]
    private static partial void LogStopContainerFailed(ILogger logger, Exception exception, string containerName);

    [LoggerMessage(EventId = 13, Level = LogLevel.Warning, Message = "Failed to discover managed agent containers from Podman during shutdown. Falling back to tracked container set only.")]
    private static partial void LogManagedContainerDiscoveryFailed(ILogger logger, Exception exception);
}
