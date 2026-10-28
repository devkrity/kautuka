using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;

using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Sandbox;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

public sealed class WorkspaceSidecarSupervisor(
    WorkspaceSidecarCatalog catalog,
    IHttpClientFactory httpClientFactory,
    PodmanCommandRunner podmanCommandRunner,
    IOptions<ManagerContainerStartupOptions> startupOptions,
    ILogger<WorkspaceSidecarSupervisor> logger,
    RuntimeImageAllowlist? allowlist = null,
    PodmanImageArchiveStore? imageArchives = null)
{
    private const string alwaysPolicyName = "ALWAYS";
    private readonly WorkspaceSidecarCatalog catalog = catalog;
    private readonly IHttpClientFactory httpClientFactory = httpClientFactory;
    private readonly PodmanCommandRunner podmanCommandRunner = podmanCommandRunner;
    private readonly ManagerContainerStartupOptions startupOptions = startupOptions.Value;
    private readonly ILogger<WorkspaceSidecarSupervisor> logger = logger;
    private readonly RuntimeImageAllowlist allowlist = allowlist ?? RuntimeImageAllowlist.Empty;
    private readonly PodmanImageArchiveStore? imageArchives = imageArchives;
    private const string managedLabel = "kautuka.managed=manager";
    private const string sidecarRoleLabel = "kautuka.role=sidecar";
    private const string sandboxNetworkName = "kautuka-sandbox";
    private const string telemetryNetworkName = "kautuka-telemetry";
    private const string telemetrySidecarName = "kautuka-openobserve";
    private readonly ConcurrentQueue<string> driftNotes = new();
    private const int defaultRunningProbeTimeoutSeconds = 45;
    private const int defaultRunningProbeIntervalMilliseconds = 1000;

    public IReadOnlyList<string> ManagedContainerNames => catalog.Sidecars.Select(static sidecar => sidecar.Name).ToArray();

    public IReadOnlyList<string> TakeDriftNotes()
    {
        var notes = new List<string>();
        while (driftNotes.TryDequeue(out string? note))
        {
            notes.Add(note);
        }

        return notes;
    }

    public async Task<bool> EnsureReadyAsync(ManagerRuntimeSettings runtimeSettings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runtimeSettings);

        if (this.imageArchives is not null)
        {
            await this.imageArchives.EnsureArchivedImagesLoadedAsync(cancellationToken).ConfigureAwait(false);
        }

        int maxParallelism = startupOptions.GetEffectiveSidecarMaxConcurrency();
        Stopwatch stopwatch = Stopwatch.StartNew();
        ConcurrentBag<SidecarEnsureOutcome> outcomes = [];

        await Parallel.ForEachAsync(
            catalog.Sidecars,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxParallelism,
                CancellationToken = cancellationToken,
            },
            async (sidecar, ct) =>
            {
                bool desiredEnabled = ShouldEnableSidecar(sidecar);

                try
                {
                    if (!desiredEnabled)
                    {
                        await EnsureStoppedAsync(sidecar.Name, ct).ConfigureAwait(false);
                        outcomes.Add(new SidecarEnsureOutcome(sidecar.Name, desiredEnabled, true, sidecar.RequiredForReady));
                        return;
                    }

                    bool ready = await EnsureSidecarAsync(sidecar, ct).ConfigureAwait(false);
                    outcomes.Add(new SidecarEnsureOutcome(sidecar.Name, desiredEnabled, ready, sidecar.RequiredForReady));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Failed to ensure sidecar {Name}.", sidecar.Name);
                    outcomes.Add(new SidecarEnsureOutcome(sidecar.Name, desiredEnabled, false, sidecar.RequiredForReady));
                }
            }).ConfigureAwait(false);

        stopwatch.Stop();

        bool readyForCoreRuntime = true;
        foreach (SidecarEnsureOutcome outcome in outcomes)
        {
            if (!outcome.Enabled || outcome.Ready)
            {
                continue;
            }

            if (outcome.RequiredForReady)
            {
                readyForCoreRuntime = false;
                continue;
            }

            logger.LogWarning(
                "Sidecar {Name} is unavailable but marked non-blocking for core readiness.",
                outcome.Name);
        }

        logger.LogInformation(
            "Sidecar reconcile phase completed in {ElapsedMilliseconds}ms for {SidecarCount} sidecars with max parallelism {MaxParallelism}. ReadyForCoreRuntime={ReadyForCoreRuntime}",
            stopwatch.ElapsedMilliseconds,
            catalog.Sidecars.Count,
            maxParallelism,
            readyForCoreRuntime);

        return readyForCoreRuntime;
    }

    private readonly record struct SidecarEnsureOutcome(string Name, bool Enabled, bool Ready, bool RequiredForReady);

    private bool ShouldEnableSidecar(WorkspaceSidecarDefinition sidecar)
    {
        if (sidecar.ManagerInfrastructure)
        {
            return true;
        }

        string? policy = sidecar.Policy;
        if (string.IsNullOrWhiteSpace(policy))
        {
            return false;
        }

        return policy.Trim().ToUpperInvariant() switch
        {
            alwaysPolicyName => true,
            _ => LogUnknownPolicyAndDisable(sidecar),
        };
    }

    private bool LogUnknownPolicyAndDisable(WorkspaceSidecarDefinition sidecar)
    {
        logger.LogWarning(
            "Sidecar {Name} declares unknown activation policy '{Policy}'. Treating it as disabled.",
            sidecar.Name,
            sidecar.Policy ?? string.Empty);
        return false;
    }

    private async Task EnsureStoppedAsync(string sidecarName, CancellationToken cancellationToken)
    {
        ContainerState state;
        try
        {
            state = await InspectStateAsync(sidecarName, cancellationToken).ConfigureAwait(false);
        }
        catch (ContainerInspectionUnavailableException ex)
        {
            logger.LogWarning(ex, "Removal of disabled sidecar {Name} deferred because inspection is unavailable.", sidecarName);
            return;
        }
        if (!state.Exists)
        {
            return;
        }

        PodmanCommandResult removeResult = await this.podmanCommandRunner.RunAsync(
            ["rm", "-f", sidecarName],
            15,
            CancellationToken.None).ConfigureAwait(false);

        if (removeResult.ExitCode != 0)
        {
            logger.LogWarning("Failed to remove disabled sidecar {Name}: {Reason}", sidecarName, PodmanFailureFormatter.FormatDetails(removeResult));
        }
    }

    private async Task<bool> EnsureSidecarAsync(WorkspaceSidecarDefinition sidecar, CancellationToken cancellationToken)
    {
        try
        {
            return await EnsureSidecarCoreAsync(sidecar, cancellationToken).ConfigureAwait(false);
        }
        catch (ContainerInspectionUnavailableException ex)
        {
            logger.LogWarning(ex, "Reconciliation of sidecar {Name} deferred because inspection is unavailable.", sidecar.Name);
            driftNotes.Enqueue(sidecar.Name + " inspection is unavailable. Reconciliation is deferred.");
            return false;
        }
    }

    private async Task<bool> EnsureSidecarCoreAsync(WorkspaceSidecarDefinition sidecar, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sidecar.Name) || string.IsNullOrWhiteSpace(sidecar.Image))
        {
            logger.LogWarning("Sidecar configuration is incomplete for image '{Image}'.", sidecar.Image);
            return false;
        }

        try
        {
            ContainerImageProvenancePolicy.EnsureTrusted(
                sidecar.Image,
                $"Sidecar '{sidecar.Name}'",
                this.allowlist.ImageReferences,
                requireListed: true);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Sidecar {Name} image failed provenance checks.", sidecar.Name);
            return false;
        }

        ContainerState state = await InspectStateAsync(sidecar.Name, cancellationToken).ConfigureAwait(false);
        if (state.IsRunning && await IsHealthyAsync(sidecar, cancellationToken).ConfigureAwait(false))
        {
            string? drift = await DescribeDriftAsync(sidecar, state.InspectJson!, cancellationToken).ConfigureAwait(false);
            if (drift is null)
            {
                return true;
            }

            logger.LogInformation("Sidecar {Name} is healthy but {Drift}. Replacing it.", sidecar.Name, drift);
            driftNotes.Enqueue(sidecar.Name + " was replaced because " + drift + ".");
        }

        if (!await EnsureImageAvailableAsync(sidecar, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        if (state.Exists)
        {
            PodmanCommandResult removal = await this.podmanCommandRunner.RunAsync(["rm", "-f", sidecar.Name], 15, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (removal.ExitCode != 0)
            {
                logger.LogWarning("Recreation of sidecar {Name} deferred because removal did not complete.", sidecar.Name);
                return false;
            }
        }

        if (!EnsureVolumeHostPaths(sidecar))
        {
            return false;
        }

        if (!await EnsurePodmanNetworkAsync(ResolveNetwork(sidecar), cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Starting sidecar {Name} from image '{Image}'.", sidecar.Name, sidecar.Image);
        }

        PodmanCommandResult runResult = await this.podmanCommandRunner.RunAsync(
            BuildRunArguments(sidecar),
            30,
            cancellationToken).ConfigureAwait(false);

        if (runResult.ExitCode != 0)
        {
            logger.LogWarning("Failed to run sidecar {Name}: {Reason}", sidecar.Name, PodmanFailureFormatter.FormatDetails(runResult));
            return false;
        }

        return await IsHealthyAsync(sidecar, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> DescribeDriftAsync(WorkspaceSidecarDefinition sidecar, string inspectJson, CancellationToken cancellationToken)
    {
        PodmanCommandResult image = await this.podmanCommandRunner.RunAsync(
            ["image", "inspect", "--format", "{{.Id}}", sidecar.Image],
            10,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (image.ExitCode != 0 || string.IsNullOrWhiteSpace(image.StdOut))
        {
            throw new ContainerInspectionUnavailableException($"Image for sidecar '{sidecar.Name}' could not be inspected. Container changes are deferred.");
        }

        try
        {
            return LiveContainerSpec.DescribeSidecarDrift(
                inspectJson,
                sidecar.Image,
                sidecar.Ports,
                image.StdOut.Trim(),
                sidecar.Volumes,
                sidecar.Environment,
                ResolveNetwork(sidecar));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new ContainerInspectionUnavailableException($"Sidecar '{sidecar.Name}' returned an invalid inspection. Container changes are deferred.", ex);
        }
    }

    private async Task<bool> IsHealthyAsync(WorkspaceSidecarDefinition sidecar, CancellationToken cancellationToken)
    {
        if (sidecar.HealthCheck is null)
        {
            // Prefer image HEALTHCHECK via podman inspect when present (wrapper images).
            // Otherwise require running only — host cannot reach unpublished sandbox aliases.
            return await WaitUntilPodmanReadyAsync(
                sidecar.Name,
                defaultRunningProbeTimeoutSeconds,
                defaultRunningProbeIntervalMilliseconds,
                cancellationToken).ConfigureAwait(false);
        }

        if (!IsLoopbackHost(sidecar.HealthCheck.Url.Host))
        {
            logger.LogWarning(
                "Sidecar {Name} healthCheck URL '{Url}' is not loopback; host HTTP probes require a loopback publish. Falling back to Podman readiness.",
                sidecar.Name,
                sidecar.HealthCheck.Url);
            return await WaitUntilPodmanReadyAsync(
                sidecar.Name,
                Math.Max(1, sidecar.HealthCheck.TimeoutSeconds),
                Math.Max(100, sidecar.HealthCheck.IntervalMilliseconds),
                cancellationToken).ConfigureAwait(false);
        }

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, sidecar.HealthCheck.TimeoutSeconds)));

        HttpClient client = httpClientFactory.CreateClient("manager.sidecars");
        Uri probeUrl = sidecar.HealthCheck.Url;

        while (!timeoutCts.Token.IsCancellationRequested)
        {
            try
            {
                if (await ProbeHostHttpAsync(client, probeUrl, timeoutCts.Token).ConfigureAwait(false))
                {
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpRequestException)
            {
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(100, sidecar.HealthCheck.IntervalMilliseconds)), timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogWarning("Sidecar {Name} did not become healthy at {Url}.", sidecar.Name, sidecar.HealthCheck.Url);
        return false;
    }

    private async Task<bool> WaitUntilPodmanReadyAsync(
        string sidecarName,
        int timeoutSeconds,
        int intervalMilliseconds,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));

        while (!timeoutCts.Token.IsCancellationRequested)
        {
            ContainerState state = await InspectStateAsync(sidecarName, timeoutCts.Token).ConfigureAwait(false);
            if (state.IsRunning)
            {
                if (string.IsNullOrEmpty(state.HealthStatus))
                {
                    return true;
                }

                if (string.Equals(state.HealthStatus, "healthy", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (string.Equals(state.HealthStatus, "unhealthy", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning("Sidecar {Name} reported Podman health status 'unhealthy'.", sidecarName);
                    return false;
                }

                // "starting" (and any other transitional value): keep waiting.
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(100, intervalMilliseconds)), timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogWarning("Sidecar {Name} did not become ready (running/healthy).", sidecarName);
        return false;
    }

    private static async Task<bool> ProbeHostHttpAsync(HttpClient client, Uri probeUrl, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await client.GetAsync(probeUrl, cancellationToken).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    private static bool IsLoopbackHost(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out IPAddress? address) && IPAddress.IsLoopback(address);
    }

    private async Task<bool> EnsureImageAvailableAsync(WorkspaceSidecarDefinition sidecar, CancellationToken cancellationToken)
    {
        if (this.imageArchives is not null)
        {
            await this.imageArchives.TryEnsureImageAsync(sidecar.Image, cancellationToken).ConfigureAwait(false);
        }

        if (await ImageExistsAsync(sidecar.Image, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(sidecar.BuildContext))
        {
            return await BuildImageAsync(sidecar, cancellationToken).ConfigureAwait(false);
        }

        if (IsFullyQualifiedImage(sidecar.Image))
        {
            return await PullImageAsync(sidecar, cancellationToken).ConfigureAwait(false);
        }

        logger.LogWarning(
            "Sidecar image {Image} is not available locally and has no build context. Use a fully qualified registry image or configure a build context.",
            sidecar.Image);
        return false;
    }

    private async Task<bool> BuildImageAsync(WorkspaceSidecarDefinition sidecar, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sidecar.BuildContext) || !Directory.Exists(sidecar.BuildContext))
        {
            logger.LogWarning("Sidecar {Name} build context '{BuildContext}' does not exist.", sidecar.Name, sidecar.BuildContext ?? string.Empty);
            return false;
        }

        List<string> args =
        [
            "build",
            "-t", sidecar.Image,
            "-f", !string.IsNullOrWhiteSpace(sidecar.Dockerfile) ? sidecar.Dockerfile : Path.Combine(sidecar.BuildContext, "Dockerfile"),
            sidecar.BuildContext
        ];

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Building sidecar image {Image} from {BuildContext}.", sidecar.Image, sidecar.BuildContext);
        }
        PodmanCommandResult buildResult = await this.podmanCommandRunner.RunAsync(args, 300, cancellationToken).ConfigureAwait(false);
        if (buildResult.ExitCode == 0)
        {
            return true;
        }

        logger.LogWarning("Failed to build sidecar image {Image}: {Reason}", sidecar.Image, PodmanFailureFormatter.FormatDetails(buildResult));
        return false;
    }

    private async Task<bool> PullImageAsync(WorkspaceSidecarDefinition sidecar, CancellationToken cancellationToken)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Pulling sidecar image {Image}.", sidecar.Image);
        }
        PodmanCommandResult pullResult = await this.podmanCommandRunner.RunAsync(["pull", sidecar.Image], 300, cancellationToken).ConfigureAwait(false);
        if (pullResult.ExitCode == 0)
        {
            return true;
        }

        logger.LogWarning("Failed to pull sidecar image {Image}: {Reason}", sidecar.Image, PodmanFailureFormatter.FormatDetails(pullResult));
        return false;
    }

    private async Task<bool> ImageExistsAsync(string image, CancellationToken cancellationToken)
    {
        PodmanCommandResult existsResult = await this.podmanCommandRunner.RunAsync(
            ["image", "exists", image],
            30,
            cancellationToken).ConfigureAwait(false);

        return existsResult.ExitCode == 0;
    }

    private static bool IsFullyQualifiedImage(string image)
    {
        string[] segments = image.Split('/', 2, StringSplitOptions.TrimEntries);
        if (segments.Length < 2)
        {
            return false;
        }

        string registry = segments[0];
        return registry.Contains('.', StringComparison.Ordinal)
            || registry.Contains(':', StringComparison.Ordinal)
            || registry.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    internal static string ResolveNetwork(WorkspaceSidecarDefinition sidecar)
        => string.Equals(sidecar.Name, telemetrySidecarName, StringComparison.Ordinal)
            ? telemetryNetworkName
            : sandboxNetworkName;

    private async Task<bool> EnsurePodmanNetworkAsync(string networkName, CancellationToken cancellationToken)
    {
        PodmanCommandResult existsResult = await this.podmanCommandRunner.RunAsync(
            ["network", "exists", networkName],
            10,
            cancellationToken).ConfigureAwait(false);
        if (existsResult.ExitCode == 0)
        {
            return true;
        }

        logger.LogInformation("Creating Podman network '{Network}'.", networkName);
        PodmanCommandResult createResult = await this.podmanCommandRunner.RunAsync(
            ["network", "create", networkName],
            30,
            cancellationToken).ConfigureAwait(false);
        if (createResult.ExitCode == 0)
        {
            return true;
        }

        logger.LogWarning(
            "Failed to create Podman network '{Network}': {Reason}",
            networkName,
            PodmanFailureFormatter.FormatDetails(createResult));
        return false;
    }

    private static List<string> BuildRunArguments(WorkspaceSidecarDefinition sidecar)
    {
        List<string> args =
        [
            "run",
            "-d",
            "--name", sidecar.Name,
            "--label", managedLabel,
            "--label", sidecarRoleLabel,
            "--network", ResolveNetwork(sidecar),
            "--network-alias", sidecar.Name
        ];

        foreach (WorkspaceSidecarPortBinding port in sidecar.Ports)
        {
            args.Add("-p");
            args.Add(FormatPortPublish(port));
        }

        foreach (WorkspaceSidecarVolumeBinding volume in sidecar.Volumes ?? [])
        {
            args.Add("-v");
            // OpenObserve runs as container root, which maps to the rootless host user.
            // Avoid recursively chowning its growing data directory on each recreation.
            // Non-root sidecars such as the package proxy still require :U.
            args.Add(volume.ReadOnly
                ? $"{volume.HostPath}:{volume.ContainerPath}:ro"
                : string.Equals(sidecar.Name, "kautuka-openobserve", StringComparison.Ordinal)
                    ? $"{volume.HostPath}:{volume.ContainerPath}"
                    : $"{volume.HostPath}:{volume.ContainerPath}:U");
        }

        foreach (KeyValuePair<string, string> kv in sidecar.Environment)
        {
            args.Add("-e");
            args.Add($"{kv.Key}={kv.Value}");
        }

        args.Add(sidecar.Image);

        foreach (string commandPart in sidecar.Command)
        {
            args.Add(commandPart);
        }

        return args;
    }

    internal static string FormatPortPublish(WorkspaceSidecarPortBinding port)
    {
        if (string.IsNullOrWhiteSpace(port.HostBindAddress))
        {
            return $"{port.HostPort}:{port.ContainerPort}";
        }

        return $"{port.HostBindAddress.Trim()}:{port.HostPort}:{port.ContainerPort}";
    }

    private bool EnsureVolumeHostPaths(WorkspaceSidecarDefinition sidecar)
    {
        foreach (WorkspaceSidecarVolumeBinding volume in sidecar.Volumes ?? [])
        {
            if (string.IsNullOrWhiteSpace(volume.HostPath))
            {
                continue;
            }

            try
            {
                // Writable bind mounts should be auto-provisioned when missing.
                // Under rootless Podman the host directory (owned by the host user) maps to
                // uid 0 (root) inside the container's user namespace. Non-root container users
                // still write via the :U volume option on `podman run` (see BuildRunArguments).
                // Reapply owner/group mode on the mount root every reconcile because asset
                // staging may create these directories before the supervisor sees them.
                if (!volume.ReadOnly)
                {
                    Directory.CreateDirectory(volume.HostPath);
                    EnsureWritableVolumePermissions(volume.HostPath);
                }
                else if (!PathExists(volume.HostPath))
                {
                    logger.LogWarning(
                        "Read-only sidecar volume path does not exist for {Name}: {HostPath}",
                        sidecar.Name,
                        volume.HostPath);
                    return false;
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "Failed to prepare sidecar volume path for {Name}: {HostPath}",
                    sidecar.Name,
                    volume.HostPath);
                return false;
            }
        }

        return true;
    }

    private static void EnsureWritableVolumePermissions(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        // Owner/group rwx only — never world writable (matches PodmanContainerToolTests contract).
        const UnixFileMode directoryMode =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute;

        try
        {
            new DirectoryInfo(path).UnixFileMode = directoryMode;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Writable mounts use Podman :U, which may leave the host path owned by a
            // container-mapped subordinate UID. The host service user then cannot chmod it;
            // failing closed here would block recreate forever. :U on the next run still
            // grants the container user write access.
        }
    }

    private static bool PathExists(string path)
        => Directory.Exists(path) || File.Exists(path);

    private async Task<ContainerState> InspectStateAsync(string containerName, CancellationToken cancellationToken)
    {
        string? inspectJson = await PodmanContainerInspector.ReadAsync(this.podmanCommandRunner, containerName, cancellationToken).ConfigureAwait(false);
        if (inspectJson is null)
        {
            return new ContainerState(false, false, null);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(inspectJson);
            JsonElement state = PodmanContainerInspector.Container(document.RootElement).GetProperty("State");
            string? health = state.TryGetProperty("Health", out JsonElement healthElement)
                && healthElement.ValueKind == JsonValueKind.Object
                && healthElement.TryGetProperty("Status", out JsonElement healthStatus)
                ? healthStatus.GetString()
                : null;
            return new ContainerState(
                true,
                string.Equals(state.GetProperty("Status").GetString(), "running", StringComparison.OrdinalIgnoreCase),
                health,
                inspectJson);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new ContainerInspectionUnavailableException($"Sidecar '{containerName}' returned an invalid inspection. Container changes are deferred.", ex);
        }
    }

    private sealed record ContainerState(bool Exists, bool IsRunning, string? HealthStatus, string? InspectJson = null);
}
