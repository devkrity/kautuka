using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;

using Kautuka.Manager.RuntimeContainer.Configuration;

using Microsoft.Extensions.Logging;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

/// <summary>
/// Manages appliance runtime readiness and container lifecycle cleanup using Podman.
/// </summary>
public sealed partial class ManagerRuntimeAdapter(
    PodmanCommandRunner podmanCommandRunner,
    IManagerRuntimeHealthProbe runtimeHealthProbe,
    ILogger<ManagerRuntimeAdapter> logger,
    RuntimeImageAllowlist? allowlist = null,
    PodmanImageArchiveStore? imageArchives = null) : IManagerRuntimeAdapter
{
    private const string sandboxSourceHashLabel = "io.kautuka.sandbox.source-hash";
    private const string sandboxUvInstallRecipeVersion = "sandbox-uv-pkgs-proxy-v3";
    private const string sandboxPipIndexUrl = "http://kautuka-pkgs-proxy:20101/pypi/simple/";
    private const string sandboxPipTrustedHost = "kautuka-pkgs-proxy";
    private const string sandboxUvPackage = "uv==0.11.19";

    private readonly PodmanCommandRunner podmanCommandRunner = podmanCommandRunner;
    private readonly IManagerRuntimeHealthProbe runtimeHealthProbe = runtimeHealthProbe;
    private readonly ILogger<ManagerRuntimeAdapter> logger = logger;
    private readonly RuntimeImageAllowlist allowlist = allowlist ?? RuntimeImageAllowlist.Empty;
    private readonly PodmanImageArchiveStore? imageArchives = imageArchives;

    public void ApplyRuntimeSettings(ManagerRuntimeSettings runtimeSettings)
    {
        ArgumentNullException.ThrowIfNull(runtimeSettings);
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
            "Applied runtime settings: image={Image} baseRoot={BaseRoot} defaultTimeout={DefaultTimeoutSeconds}s",
                runtimeSettings.Sandbox.Image ?? string.Empty,
                runtimeSettings.Workspace.BaseRoot ?? string.Empty,
                runtimeSettings.Sandbox.DefaultTimeout);
        }
    }

    public async Task<bool> EnsureReadyAsync(ManagerRuntimeSettings runtimeSettings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtimeSettings);

        if (!await runtimeHealthProbe.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
        {
            logger.LogWarning("Podman runtime is not available.");
            return false;
        }

        string? baseRoot = runtimeSettings.Workspace.BaseRoot;
        if (!string.IsNullOrWhiteSpace(baseRoot))
        {
            try
            {
                Directory.CreateDirectory(baseRoot);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Workspace base root '{BaseRoot}' is not accessible.", baseRoot);
                return false;
            }
        }

        return true;
    }

    public async Task<string?> PrepareSandboxImageAsync(SandboxRuntimeDefinition sandbox, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sandbox);

        if (string.IsNullOrWhiteSpace(sandbox.Image))
        {
            return "infra.sandbox.image is not configured in system_settings";
        }

        string? releasedImage = TryResolveReleasedSandboxImage(sandbox.Image);
        if (releasedImage is not null)
        {
            sandbox = new SandboxRuntimeDefinition
            {
                Image = releasedImage,
                DefaultCpus = sandbox.DefaultCpus,
                DefaultMemory = sandbox.DefaultMemory,
                DefaultTimeout = sandbox.DefaultTimeout,
                ContainerEnvironment = sandbox.ContainerEnvironment,
            };
        }

        if (this.imageArchives is not null)
        {
            await this.imageArchives.EnsureArchivedImagesLoadedAsync(cancellationToken).ConfigureAwait(false);
        }

        string sandboxImage = sandbox.Image;
        string sandboxAssetsRoot = Path.Combine(AppContext.BaseDirectory, "sandbox");
        string dockerfilePath = Path.Combine(sandboxAssetsRoot, "Dockerfile");

        // Check for the image using the qualified name to match what build produces.
        string effectiveImage = sandbox.Image.Contains('/', StringComparison.Ordinal)
            ? sandbox.Image
            : QualifyLocalImage(sandbox.Image);

        if (!sandbox.Image.Contains('/', StringComparison.Ordinal))
        {
            PodmanCommandResult existsResult = await this.podmanCommandRunner.RunAsync(
                ["image", "exists", effectiveImage],
                15,
                cancellationToken).ConfigureAwait(false);

            string expectedSourceHash = ComputeSandboxSourceHash(dockerfilePath);
            string? currentSourceHash = existsResult.ExitCode == 0
                ? await TryReadSandboxSourceHashAsync(effectiveImage, cancellationToken).ConfigureAwait(false)
                : null;

            if (existsResult.ExitCode == 0 && IsSandboxImageSourceCurrent(currentSourceHash, expectedSourceHash))
            {
                return null;
            }

            string? buildError = await TryBuildSandboxImageAsync(sandbox, dockerfilePath, sandboxAssetsRoot, expectedSourceHash, cancellationToken).ConfigureAwait(false);
            if (buildError is null)
            {
                return null;
            }

            return existsResult.ExitCode == 0
                ? $"Sandbox image '{sandbox.Image}' is stale and automatic rebuild failed: {buildError}"
                : $"Sandbox image '{sandbox.Image}' is not available locally and automatic build failed: {buildError}";
        }

        PodmanCommandResult remoteExistsResult = await this.podmanCommandRunner.RunAsync(
            ["image", "exists", effectiveImage],
            15,
            cancellationToken).ConfigureAwait(false);

        if (remoteExistsResult.ExitCode == 0)
        {
            return null;
        }

        try
        {
            ContainerImageProvenancePolicy.EnsureTrusted(
                sandboxImage,
                "Sandbox runtime",
                this.allowlist.ImageReferences,
                requireListed: true);
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }

        PodmanCommandResult pullResult = await this.podmanCommandRunner.RunAsync(
            ["pull", sandboxImage],
            Math.Max(30, sandbox.DefaultTimeout),
            cancellationToken).ConfigureAwait(false);

        if (pullResult.ExitCode == 0)
        {
            return null;
        }

        return PodmanFailureFormatter.FormatFailure($"Pull sandbox image '{sandboxImage}'", pullResult);
    }

    public string ResolveSandboxImage(string configuredImage)
        => this.allowlist.SelectReleasedSandboxImage(configuredImage);

    private string? TryResolveReleasedSandboxImage(string configuredImage)
    {
        string resolved = ResolveSandboxImage(configuredImage);
        return string.Equals(resolved, configuredImage, StringComparison.Ordinal) ? null : resolved;
    }

    private async Task<string?> TryReadSandboxSourceHashAsync(string image, CancellationToken cancellationToken)
    {
        PodmanCommandResult inspectResult = await this.podmanCommandRunner.RunAsync(
            ["image", "inspect", "--format", $"{{{{ index .Config.Labels \"{sandboxSourceHashLabel}\" }}}}", image],
            15,
            cancellationToken).ConfigureAwait(false);

        if (inspectResult.ExitCode != 0)
        {
            return null;
        }

        string value = inspectResult.StdOut.Trim();
        return string.IsNullOrWhiteSpace(value) || string.Equals(value, "<no value>", StringComparison.OrdinalIgnoreCase)
            ? null
            : value;
    }

    internal static string ComputeSandboxSourceHash(string dockerfilePath)
    {
        if (!File.Exists(dockerfilePath))
        {
            throw new FileNotFoundException("Sandbox Dockerfile was not found.", dockerfilePath);
        }

        byte[] dockerfileBytes = File.ReadAllBytes(dockerfilePath);
        byte[] recipeBytes = Encoding.UTF8.GetBytes("\n" + sandboxUvInstallRecipeVersion + "\n");
        byte[] bytes = new byte[dockerfileBytes.Length + recipeBytes.Length];
        Buffer.BlockCopy(dockerfileBytes, 0, bytes, 0, dockerfileBytes.Length);
        Buffer.BlockCopy(recipeBytes, 0, bytes, dockerfileBytes.Length, recipeBytes.Length);
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash);
    }

    internal static bool IsSandboxImageSourceCurrent(string? currentSourceHash, string expectedSourceHash)
        => !string.IsNullOrWhiteSpace(currentSourceHash)
            && string.Equals(currentSourceHash, expectedSourceHash, StringComparison.OrdinalIgnoreCase);

    private async Task<string?> TryBuildSandboxImageAsync(
        SandboxRuntimeDefinition sandbox,
        string dockerfilePath,
        string sandboxAssetsRoot,
        string sourceHash,
        CancellationToken cancellationToken)
    {
        string sandboxImage = sandbox.Image ?? string.Empty;
        // Locally-built images must be tagged with the 'localhost/' prefix so Podman can
        // resolve them without needing unqualified-search-registries in registries.conf.
        string localTag = QualifyLocalImage(sandboxImage);

        if (!Directory.Exists(sandboxAssetsRoot))
        {
            return $"Sandbox build context '{sandboxAssetsRoot}' does not exist.";
        }

        if (!File.Exists(dockerfilePath))
        {
            return $"Sandbox Dockerfile '{dockerfilePath}' does not exist.";
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Sandbox image '{Image}' is missing. Building from {Dockerfile} with context {Context}.",
                sandboxImage,
                dockerfilePath,
                sandboxAssetsRoot);
        }

        string systemTag = $"{localTag}-sys";
        PodmanCommandResult buildResult = await this.podmanCommandRunner.RunAsync(
            [
                "build",
                "-t", systemTag,
                "-f", dockerfilePath,
                sandboxAssetsRoot
            ],
            Math.Max(900, sandbox.DefaultTimeout),
            cancellationToken).ConfigureAwait(false);

        if (buildResult.ExitCode != 0)
        {
            return PodmanFailureFormatter.FormatFailure("Podman build for sandbox image", buildResult);
        }

        try
        {
            return await StampSandboxImageWithUvAsync(systemTag, localTag, sourceHash, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await TryRemoveImageAsync(systemTag, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string?> StampSandboxImageWithUvAsync(
        string systemImage,
        string finalImage,
        string sourceHash,
        CancellationToken cancellationToken)
    {
        PodmanCommandResult networkExists = await this.podmanCommandRunner
            .RunAsync(["network", "exists", ConnectorContainerSpecBuilder.SandboxNetworkName], 10, cancellationToken)
            .ConfigureAwait(false);
        if (networkExists.ExitCode != 0)
        {
            return $"Sandbox uv install requires Podman network '{ConnectorContainerSpecBuilder.SandboxNetworkName}' so pip can reach kautuka-pkgs-proxy. Refusing public-index fallback.";
        }

        string uvImage = $"{finalImage}-uv";
        string containerName = "kautuka-sandbox-uv-" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            PodmanCommandResult runResult = await this.podmanCommandRunner
                .RunAsync(
                    [
                        "run",
                        "--replace",
                        "--name", containerName,
                        "--network", ConnectorContainerSpecBuilder.SandboxNetworkName,
                        "--user", "0",
                        "--env", $"PIP_INDEX_URL={sandboxPipIndexUrl}",
                        "--env", $"PIP_TRUSTED_HOST={sandboxPipTrustedHost}",
                        "--env", "PIP_CONFIG_FILE=/dev/null",
                        systemImage,
                        "python3",
                        "-m",
                        "pip",
                        "install",
                        "--break-system-packages",
                        "--no-cache-dir",
                        "--index-url",
                        sandboxPipIndexUrl,
                        "--trusted-host",
                        sandboxPipTrustedHost,
                        sandboxUvPackage,
                    ],
                    900,
                    cancellationToken)
                .ConfigureAwait(false);
            if (runResult.ExitCode != 0)
            {
                return PodmanFailureFormatter.FormatFailure(
                    $"Install sandbox {sandboxUvPackage} via {ConnectorContainerSpecBuilder.SandboxNetworkName}",
                    runResult);
            }

            PodmanCommandResult commitResult = await this.podmanCommandRunner
                .RunAsync(["commit", containerName, uvImage], 120, cancellationToken)
                .ConfigureAwait(false);
            if (commitResult.ExitCode != 0)
            {
                return PodmanFailureFormatter.FormatFailure("Commit sandbox uv layer", commitResult);
            }
        }
        finally
        {
            await TryRemoveContainerAsync(containerName, cancellationToken).ConfigureAwait(false);
        }

        string stampContext = Path.Combine(Path.GetTempPath(), "kautuka-sandbox-uv-stamp", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(stampContext);
            string stampFile = Path.Combine(stampContext, "Containerfile");
            await File.WriteAllTextAsync(
                stampFile,
                $"FROM {uvImage}\nUSER node\nCMD [\"tail\", \"-f\", \"/dev/null\"]\n",
                cancellationToken).ConfigureAwait(false);

            PodmanCommandResult stampResult = await this.podmanCommandRunner
                .RunAsync(
                    [
                        "build",
                        "--pull=never",
                        "-t", finalImage,
                        "--label", $"{sandboxSourceHashLabel}={sourceHash}",
                        "-f", stampFile,
                        stampContext,
                    ],
                    120,
                    cancellationToken)
                .ConfigureAwait(false);
            if (stampResult.ExitCode != 0)
            {
                return PodmanFailureFormatter.FormatFailure("Stamp sandbox USER node after uv install", stampResult);
            }

            return null;
        }
        finally
        {
            await TryRemoveImageAsync(uvImage, cancellationToken).ConfigureAwait(false);
            try
            {
                if (Directory.Exists(stampContext))
                {
                    Directory.Delete(stampContext, recursive: true);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to clean sandbox uv stamp context '{StampContext}'.", stampContext);
            }
        }
    }

    private async Task TryRemoveImageAsync(string image, CancellationToken cancellationToken)
    {
        try
        {
            _ = await this.podmanCommandRunner.RunAsync(["rmi", "-f", image], 30, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to remove temporary sandbox image '{Image}'.", image);
        }
    }

    private async Task TryRemoveContainerAsync(string containerName, CancellationToken cancellationToken)
    {
        try
        {
            _ = await this.podmanCommandRunner.RunAsync(["rm", "-f", containerName], 30, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to remove temporary sandbox uv container '{Container}'.", containerName);
        }
    }

    public async Task<string[]> GetManagedContainerNamesAsync(IReadOnlyCollection<string> trackedContainers, CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(trackedContainers, StringComparer.OrdinalIgnoreCase);

        try
        {
            PodmanCommandResult managedContainerResult = await this.podmanCommandRunner.RunAsync(
                ["ps", "-a", "--filter", "label=kautuka.managed=manager", "--format", "{{.Names}}"],
                10,
                cancellationToken).ConfigureAwait(false);

            if (managedContainerResult.ExitCode == 0 && !string.IsNullOrWhiteSpace(managedContainerResult.StdOut))
            {
                foreach (string line in managedContainerResult.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    names.Add(line);
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or Win32Exception)
        {
            LogManagedContainerDiscoveryFailed(logger, ex);
        }

        return [.. names];
    }

    public async Task StopContainersAsync(IReadOnlyCollection<string> containerNames, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(containerNames);

        if (containerNames.Count == 0)
        {
            return;
        }

        LogStoppingContainers(logger, containerNames.Count);

        Task[] stopTasks = containerNames
            .Select(name => Task.Run(async () =>
            {
                try
                {
                    await this.podmanCommandRunner.RunAsync(["rm", "-f", name], 15, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or Win32Exception)
                {
                    LogStopContainerFailed(logger, ex, name);
                }
            }, cancellationToken))
            .ToArray();

        await Task.WhenAll(stopTasks).ConfigureAwait(false);
    }

    public async Task<ManagedContainerReconcileResult> RemoveOrphanedManagedContainersAsync(
        string role,
        IReadOnlyCollection<string> desiredContainerNames,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desiredContainerNames);
        if (role is not ("agent" or "connector" or "sidecar"))
        {
            throw new ArgumentOutOfRangeException(nameof(role));
        }
        var desired = new HashSet<string>(desiredContainerNames, StringComparer.OrdinalIgnoreCase);
        PodmanCommandResult listed = await podmanCommandRunner.RunAsync(
            ["ps", "-a", "--filter", "label=kautuka.managed=manager", "--filter", $"label=kautuka.role={role}", "--format", "{{.Names}}"],
            10,
            cancellationToken).ConfigureAwait(false);
        if (listed.ExitCode != 0)
        {
            return new ManagedContainerReconcileResult(false, 0, $"{role}_container_discovery_failed");
        }

        string[] stale = [.. listed.StdOut
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => !desired.Contains(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        int removed = 0;
        foreach (string name in stale)
        {
            PodmanCommandResult remove = await podmanCommandRunner.RunAsync(["rm", "-f", name], 15, cancellationToken).ConfigureAwait(false);
            PodmanCommandResult observed = await podmanCommandRunner.RunAsync(["container", "exists", name], 10, cancellationToken).ConfigureAwait(false);
            if (remove.ExitCode != 0 || observed.ExitCode == 0)
            {
                return new ManagedContainerReconcileResult(false, removed, "orphan_container_remove_failed");
            }

            removed++;
        }

        return new ManagedContainerReconcileResult(true, removed, null);
    }

    /// <summary>
    /// Returns the image name qualified with the 'localhost/' registry prefix when the name is
    /// a Podman short name (contains no '/'). Short names without a registry prefix cause
    /// 'podman run' to fail on systems that have no unqualified-search-registries configured.
    /// </summary>
    private static string QualifyLocalImage(string image)
        => image.Contains('/', StringComparison.Ordinal) ? image : $"localhost/{image}";

    /// <summary>
    /// Ensures a Podman network exists, creating it if absent.
    /// </summary>
    public async Task EnsureNetworkAsync(string networkName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(networkName);

        PodmanCommandResult existsResult = await this.podmanCommandRunner.RunAsync(
            ["network", "exists", networkName],
            10,
            cancellationToken).ConfigureAwait(false);

        if (existsResult.ExitCode == 0)
        {
            return;
        }

        logger.LogInformation("Creating Podman network '{Network}'.", networkName);
        PodmanCommandResult createResult = await this.podmanCommandRunner.RunAsync(
            ["network", "create", networkName],
            30,
            cancellationToken).ConfigureAwait(false);

        if (createResult.ExitCode != 0)
        {
            logger.LogWarning("Failed to create Podman network '{Network}': {Reason}",
                networkName, PodmanFailureFormatter.FormatDetails(createResult));
        }
    }
}
