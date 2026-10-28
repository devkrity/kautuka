using Kautuka.Manager.RuntimeContainer.Configuration;

using Microsoft.Extensions.Logging;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

/// <summary>
/// Loads first-boot OCI archives from the appliance image cache before registry pulls.
/// </summary>
public sealed class PodmanImageArchiveStore(
    PodmanCommandRunner podmanCommandRunner,
    WorkspaceRuntimePaths runtimePaths,
    RuntimeImageAllowlist allowlist,
    ILogger<PodmanImageArchiveStore> logger)
{
    public async Task EnsureArchivedImagesLoadedAsync(CancellationToken cancellationToken)
    {
        foreach (RuntimeImageEntry entry in allowlist.Images)
        {
            await TryEnsureImageAsync(entry, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task TryEnsureImageAsync(string image, CancellationToken cancellationToken)
    {
        RuntimeImageEntry? entry = allowlist.Images.FirstOrDefault(
            candidate => string.Equals(candidate.Image, image, StringComparison.Ordinal));
        return entry is null
            ? Task.CompletedTask
            : TryEnsureImageAsync(entry, cancellationToken);
    }

    private async Task TryEnsureImageAsync(RuntimeImageEntry entry, CancellationToken cancellationToken)
    {
        if (await ImageExistsAsync(entry.Image, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(entry.Archive))
        {
            return;
        }

        string archivePath = Path.Combine(runtimePaths.OciImagesRoot, entry.Archive);
        if (!File.Exists(archivePath))
        {
            logger.LogDebug(
                "OCI archive '{Archive}' for image '{Image}' is not present under {Root}.",
                entry.Archive,
                entry.Image,
                runtimePaths.OciImagesRoot);
            return;
        }

        logger.LogInformation("Loading OCI archive {Archive} for image {Image}.", archivePath, entry.Image);
        PodmanCommandResult loadResult = await podmanCommandRunner
            .RunAsync(["load", "-i", archivePath], 300, cancellationToken)
            .ConfigureAwait(false);
        if (loadResult.ExitCode != 0)
        {
            logger.LogWarning(
                "Failed to load OCI archive '{Archive}' for '{Image}': {Reason}",
                archivePath,
                entry.Image,
                PodmanFailureFormatter.FormatDetails(loadResult));
        }
    }

    private async Task<bool> ImageExistsAsync(string image, CancellationToken cancellationToken)
    {
        PodmanCommandResult existsResult = await podmanCommandRunner
            .RunAsync(["image", "exists", image], 30, cancellationToken)
            .ConfigureAwait(false);
        return existsResult.ExitCode == 0;
    }
}
