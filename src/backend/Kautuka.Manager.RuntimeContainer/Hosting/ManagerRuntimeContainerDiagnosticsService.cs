using System.ComponentModel;

using Kautuka.Manager.RuntimeContainer.Configuration;

using Microsoft.Extensions.Logging;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

public sealed class ManagerRuntimeContainerDiagnosticsService(
    PodmanCommandRunner podmanCommandRunner,
    WorkspaceSidecarCatalog sidecarCatalog,
    ILogger<ManagerRuntimeContainerDiagnosticsService> logger)
{
    private readonly PodmanCommandRunner podmanCommandRunner = podmanCommandRunner;
    private readonly WorkspaceSidecarCatalog sidecarCatalog = sidecarCatalog;
    private readonly ILogger<ManagerRuntimeContainerDiagnosticsService> logger = logger;

    public async Task<ManagerRuntimeContainerDiagnosticsResult> ListManagedContainersAsync(CancellationToken cancellationToken)
    {
        try
        {
            PodmanCommandResult result = await podmanCommandRunner.RunAsync(
                ["ps", "-a", "--filter", "label=kautuka.managed=manager", "--format", "{{.Names}}\t{{.Image}}\t{{.Status}}"],
                10,
                cancellationToken).ConfigureAwait(false);

            if (result.ExitCode != 0)
            {
                return new ManagerRuntimeContainerDiagnosticsResult(
                    false,
                    PodmanFailureFormatter.FormatFailure("Inspect manager containers", result),
                    []);
            }

            Dictionary<string, string> sidecarImages = sidecarCatalog.Sidecars
                .GroupBy(static sidecar => sidecar.Name, StringComparer.Ordinal)
                .ToDictionary(static group => group.Key, static group => group.Last().Image, StringComparer.Ordinal);
            HashSet<string> sidecarNames = [.. sidecarCatalog.Sidecars.Select(static sidecar => sidecar.Name)];

            List<ManagerRuntimeContainerSnapshot> containers = [];
            foreach (string line in result.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] parts = line.Split('\t');
                if (parts.Length < 3)
                {
                    continue;
                }

                string name = parts[0];
                string image = string.IsNullOrWhiteSpace(parts[1]) && sidecarImages.TryGetValue(name, out string? sidecarImage)
                    ? sidecarImage
                    : parts[1];

                containers.Add(new ManagerRuntimeContainerSnapshot(
                    name,
                    image,
                    parts[2],
                    sidecarNames.Contains(name) ? "sidecar" : "managed"));
            }

            string message = containers.Count == 0
                ? "No manager containers found."
                : $"{containers.Count} manager container(s) discovered.";

            return new ManagerRuntimeContainerDiagnosticsResult(true, message, containers);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Win32Exception)
        {
            logger.LogWarning(ex, "Failed to inspect manager-owned containers.");
            return new ManagerRuntimeContainerDiagnosticsResult(false, "Manager container diagnostics unavailable.", []);
        }
    }
}

public sealed record ManagerRuntimeContainerSnapshot(
    string Name,
    string Image,
    string Status,
    string Role);

public sealed record ManagerRuntimeContainerDiagnosticsResult(
    bool Ok,
    string Message,
    IReadOnlyList<ManagerRuntimeContainerSnapshot> Containers);
