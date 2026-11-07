using System.Security.Cryptography;
using System.Text.Json;

using Kautuka.Manager.RuntimeContainer.Configuration;

using Microsoft.Extensions.Logging;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

/// <summary>
/// Owns the offline OCI bootstrap cache and verifies signed source/config bindings.
/// </summary>
public sealed class PodmanImageArchiveStore
{
    private readonly PodmanCommandRunner podmanCommandRunner;
    private readonly string ociImagesRoot;
    private readonly RuntimeImageAllowlist allowlist;
    private readonly ILogger<PodmanImageArchiveStore> logger;

    public PodmanImageArchiveStore(PodmanCommandRunner podmanCommandRunner, WorkspaceRuntimePaths runtimePaths,
        RuntimeImageAllowlist allowlist, ILogger<PodmanImageArchiveStore> logger)
        : this(podmanCommandRunner, (runtimePaths ?? throw new ArgumentNullException(nameof(runtimePaths))).OciImagesRoot, allowlist, logger) { }

    public PodmanImageArchiveStore(PodmanCommandRunner podmanCommandRunner, string ociImagesRoot,
        RuntimeImageAllowlist allowlist, ILogger<PodmanImageArchiveStore> logger)
    {
        this.podmanCommandRunner = podmanCommandRunner;
        this.ociImagesRoot = Path.GetFullPath(ociImagesRoot);
        this.allowlist = allowlist;
        this.logger = logger;
    }

    internal async Task<ResolvedRuntimeImage> ResolveCachedAsync(string sourceReference, CancellationToken ct, bool allowLoad = true)
    {
        RuntimeImageEntry[] entries = [.. allowlist.Images.Where(item => item.Image == sourceReference)];
        if (entries.Length == 0) throw new InvalidOperationException("The requested bootstrap image is not in the verified release inventory.");
        RuntimeImageEntry entry = entries[0];
        ContainerImageProvenancePolicy.EnsureTrusted(entry.Image, entry.Id, allowlist.ImageReferences, requireListed: true);
        foreach (RuntimeImageEntry candidate in entries)
        {
            candidate.ValidateArchiveIdentity();
            if (candidate.Platform != entry.Platform || !string.Equals(candidate.LocalImageId, entry.LocalImageId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The release inventory contains conflicting config/platform bindings for one source image.");
        }
        if (!entry.HasArchiveIdentity && (!ContainerImageProvenancePolicy.HasSha256Digest(entry.Image)
            || ContainerImageProvenancePolicy.IsLocalImage(entry.Image)))
            throw new InvalidOperationException("A mutable local image cannot serve as a verified release identity without signed config metadata.");
        string reference = entry.LocalImageId ?? entry.Image;
        if (!await ImageExistsAsync(reference, ct).ConfigureAwait(false))
        {
            if (!allowLoad) throw new InvalidOperationException("The runtime image is not ready; background preparation must finish before execution.");
            if (!entry.HasArchiveIdentity)
                throw new InvalidOperationException("A missing bootstrap image requires a signed archive/config identity; network acquisition cannot bootstrap itself.");
            string archive = Path.Combine(ociImagesRoot, entry.Archive!);
            using (FileStream input = File.OpenRead(archive))
            {
                string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct).ConfigureAwait(false));
                if (!hash.Equals(entry.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The cached OCI archive does not match its signed SHA-256.");
            }
            PodmanCommandResult loaded = await podmanCommandRunner.RunAsync(["load", "-i", archive], 300, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (loaded.ExitCode != 0) throw new InvalidOperationException(PodmanFailureFormatter.FormatFailure("Verified OCI load", loaded));
        }
        PodmanCommandResult inspected = await podmanCommandRunner.RunAsync(["image", "inspect", reference], 30, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (inspected.ExitCode != 0) throw new InvalidOperationException(PodmanFailureFormatter.FormatFailure("Verified OCI identity inspection", inspected));
        using JsonDocument document = JsonDocument.Parse(inspected.StdOut);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() != 1)
            throw new InvalidOperationException("The OCI image inspection is ambiguous.");
        JsonElement image = document.RootElement[0];
        ResolvedRuntimeImage resolved = ResolvedRuntimeImage.FromInspection(entry.Image, image);
        if ((entry.LocalImageId is not null && !entry.LocalImageId.Equals(resolved.LocalImageId, StringComparison.OrdinalIgnoreCase))
            || (entry.Platform is not null && entry.Platform != resolved.Platform))
            throw new InvalidOperationException("The loaded OCI config identity or platform differs from its signed release entry.");
        return resolved;
    }

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
        if (entry.HasArchiveIdentity)
        {
            _ = await ResolveCachedAsync(entry.Image, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (await ImageExistsAsync(entry.Image, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(entry.Archive))
        {
            return;
        }

        string archivePath = Path.Combine(ociImagesRoot, entry.Archive);
        if (!File.Exists(archivePath))
        {
            logger.LogDebug(
                "OCI archive '{Archive}' for image '{Image}' is not present under {Root}.",
                entry.Archive,
                entry.Image,
                ociImagesRoot);
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
        cancellationToken.ThrowIfCancellationRequested();
        if (existsResult.ExitCode == 0) return true;
        if (existsResult.ExitCode == 1 && existsResult.TerminationReason == ProcessTerminationReason.NonZeroExit) return false;
        throw new InvalidOperationException(PodmanFailureFormatter.FormatFailure("OCI cache inspection", existsResult));
    }
}
