using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
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
    PodmanImageArchiveStore? imageArchives = null,
    NetworkAccess.EgressTopologyManager? egressTopologyManager = null,
    bool applianceMode = false)
{
    private const string alwaysPolicyName = "ALWAYS";
    private readonly WorkspaceSidecarCatalog catalog = catalog;
    private readonly IHttpClientFactory httpClientFactory = httpClientFactory;
    private readonly PodmanCommandRunner podmanCommandRunner = podmanCommandRunner;
    private readonly ManagerContainerStartupOptions startupOptions = startupOptions.Value;
    private readonly ILogger<WorkspaceSidecarSupervisor> logger = logger;
    private readonly RuntimeImageAllowlist allowlist = allowlist ?? RuntimeImageAllowlist.Empty;
    private readonly PodmanImageArchiveStore? imageArchives = imageArchives;
    private readonly NetworkAccess.EgressTopologyManager? egressTopologyManager = egressTopologyManager;
    private const string managedLabel = "kautuka.managed=manager";
    private const string sidecarRoleLabel = "kautuka.role=sidecar";
    private const string sidecarSourceHashLabel = "io.kautuka.sidecar.source-hash";
    private const string sandboxNetworkName = "kautuka-sandbox";
    private const string telemetryNetworkName = "kautuka-telemetry";
    private const string telemetrySidecarName = "kautuka-openobserve";
    private const string packageProxySidecarName = "kautuka-pkgs-proxy";
    private readonly ConcurrentQueue<string> driftNotes = new();
    private const int defaultRunningProbeTimeoutSeconds = 45;
    private const int defaultRunningProbeIntervalMilliseconds = 1000;

    public IReadOnlyList<string> ManagedContainerNames => catalog.Sidecars.Select(static sidecar => sidecar.Name).ToArray();

    public async Task<bool> EnsurePackageProxyReadyForVerificationAsync(CancellationToken cancellationToken)
    {
        if (!applianceMode || egressTopologyManager?.IsStartupGateOpen != true)
            return false;

        WorkspaceSidecarDefinition? sidecar = catalog.Sidecars.FirstOrDefault(IsPackageProxy);
        if (sidecar is null || !ShouldEnableSidecar(sidecar))
        {
            egressTopologyManager.SetPackageProxyReady(false);
            logger.LogWarning("The package proxy is missing or disabled in the candidate release.");
            return false;
        }

        try
        {
            return await EnsureSidecarAsync(sidecar, cancellationToken).ConfigureAwait(false)
                && egressTopologyManager.IsPackageProxyReady;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            egressTopologyManager.SetPackageProxyReady(false);
            logger.LogWarning(error, "Could not verify the candidate release's package proxy.");
            return false;
        }
    }

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
        bool ready = false;
        try
        {
            ready = await EnsureSidecarCoreAsync(sidecar, cancellationToken).ConfigureAwait(false);
            return ready;
        }
        catch (ContainerInspectionUnavailableException ex)
        {
            logger.LogWarning(ex, "Reconciliation of sidecar {Name} deferred because inspection is unavailable.", sidecar.Name);
            driftNotes.Enqueue(sidecar.Name + " inspection is unavailable. Reconciliation is deferred.");
            return false;
        }
        finally
        {
            if (!ready && IsPackageProxy(sidecar))
            {
                egressTopologyManager?.SetPackageProxyReady(false);
            }
        }
    }

    private async Task<bool> EnsureSidecarCoreAsync(WorkspaceSidecarDefinition sidecar, CancellationToken cancellationToken)
    {
        bool isPackageProxy = IsPackageProxy(sidecar);
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
        bool hasScheduledPackageHealth = isPackageProxy && sidecar.HealthCheck is null
            && string.Equals(state.HealthStatus, "healthy", StringComparison.OrdinalIgnoreCase);
        if (isPackageProxy && egressTopologyManager is not null
            && !(state.IsRunning && hasScheduledPackageHealth && state.InspectJson is not null
                && egressTopologyManager.IsRegisteredPackageProxyContainer(ReadInspectString(state.InspectJson, "Id"))))
        {
            egressTopologyManager.SetPackageProxyReady(false);
        }

        if (state.IsRunning && (hasScheduledPackageHealth || await IsHealthyAsync(sidecar, cancellationToken).ConfigureAwait(false)))
        {
            (string? drift, string imageId) = await DescribeDriftAsync(sidecar, state.InspectJson!, cancellationToken).ConfigureAwait(false);
            if (drift is null)
            {
                if (IsPackageProxy(sidecar)
                    && !await AuthorizeExistingPackageProxyAsync(sidecar, state.InspectJson!, imageId, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }

                if (isPackageProxy) egressTopologyManager?.SetPackageProxyReady(true);

                logger.LogInformation("Sidecar {Name} is ready.", sidecar.Name);
                return true;
            }

            logger.LogInformation("Sidecar {Name} is healthy but {Drift}. Replacing it.", sidecar.Name, drift);
            driftNotes.Enqueue(sidecar.Name + " was replaced because " + drift + ".");
        }

        if (isPackageProxy)
        {
            egressTopologyManager?.SetPackageProxyReady(false);
        }

        if (!await EnsureImageAvailableAsync(sidecar, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        if (state.Exists)
        {
            PodmanCommandResult removal;
            if (isPackageProxy && state.InspectJson is not null && egressTopologyManager is not null)
            {
                try
                {
                    await egressTopologyManager.RevokePackageProxyContainerAsync(
                        ReadInspectString(state.InspectJson, "Id"), cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    removal = await this.podmanCommandRunner.RunAsync(
                        ["rm", "-f", sidecar.Name], 15, cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                removal = await this.podmanCommandRunner.RunAsync(
                    ["rm", "-f", sidecar.Name], 15, cancellationToken).ConfigureAwait(false);
            }

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

        bool started = IsPackageProxy(sidecar)
            ? await CreateAuthorizeAndStartPackageProxyAsync(sidecar, cancellationToken).ConfigureAwait(false)
            : await RunSidecarAsync(sidecar, cancellationToken).ConfigureAwait(false);
        if (!started)
        {
            return false;
        }

        bool ready = await IsHealthyAsync(sidecar, cancellationToken).ConfigureAwait(false);
        if (isPackageProxy && ready)
        {
            ContainerState verifiedState = await InspectStateAsync(sidecar.Name, cancellationToken).ConfigureAwait(false);
            ready = verifiedState.IsRunning
                && string.Equals(verifiedState.HealthStatus, "healthy", StringComparison.OrdinalIgnoreCase)
                && verifiedState.InspectJson is not null
                && await AuthorizeExistingPackageProxyAsync(sidecar, verifiedState.InspectJson, imageId: null, cancellationToken).ConfigureAwait(false);
            if (!ready)
            {
                if (verifiedState.InspectJson is not null)
                {
                    if (egressTopologyManager is not null)
                    {
                        await RevokeAndRemovePackageProxyAsync(
                            ReadInspectString(verifiedState.InspectJson, "Id"), sidecar.Name, cancellationToken).ConfigureAwait(false);
                    }
                }
                else
                {
                    await RemoveSidecarBestEffortAsync(sidecar.Name).ConfigureAwait(false);
                }
                logger.LogError("Package proxy failed health, identity, or special ruleset verification and was removed.");
            }
        }

        if (isPackageProxy) egressTopologyManager?.SetPackageProxyReady(ready);
        if (ready)
        {
            logger.LogInformation("Sidecar {Name} is ready.", sidecar.Name);
        }
        return ready;
    }

    private async Task<(string? Drift, string ImageId)> DescribeDriftAsync(WorkspaceSidecarDefinition sidecar, string inspectJson, CancellationToken cancellationToken)
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
            string imageId = image.StdOut.Trim();
            string? drift = LiveContainerSpec.DescribeSidecarDrift(
                inspectJson,
                sidecar.Image,
                sidecar.Ports,
                imageId,
                sidecar.Volumes,
                sidecar.Environment,
                ResolveNetwork(sidecar));
            if (drift is null && IsPackageProxy(sidecar))
            {
                drift = DescribePackageProxyIdentityDrift(inspectJson, NormalizeImageId(imageId));
            }

            return (drift, imageId);
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
            bool ready = await WaitUntilPodmanReadyAsync(
                sidecar.Name,
                defaultRunningProbeTimeoutSeconds,
                defaultRunningProbeIntervalMilliseconds,
                cancellationToken).ConfigureAwait(false);
            if (!ready || !IsPackageProxy(sidecar)) return ready;

            return await WaitForPackageProxyHealthAsync(sidecar.Name, cancellationToken).ConfigureAwait(false);
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

    private async Task<bool> WaitForPackageProxyHealthAsync(string name, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(defaultRunningProbeTimeoutSeconds));
        try
        {
            while (!deadline.IsCancellationRequested)
            {
                // Podman may not schedule image health checks in a rootless session.
                PodmanCommandResult probe = await podmanCommandRunner.RunAsync(
                    ["healthcheck", "run", name], 10, deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                if (probe.ExitCode == 0)
                {
                    ContainerState state = await InspectStateAsync(name, deadline.Token).ConfigureAwait(false);
                    if (state.IsRunning && string.Equals(state.HealthStatus, "healthy", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                await Task.Delay(defaultRunningProbeIntervalMilliseconds, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            // The caller may retry reconciliation. Keep the proxy unavailable meanwhile.
        }

        cancellationToken.ThrowIfCancellationRequested();
        logger.LogWarning("Package proxy image healthcheck did not become healthy within {TimeoutSeconds}s.", defaultRunningProbeTimeoutSeconds);
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
                if (string.IsNullOrEmpty(state.HealthStatus)
                    || string.Equals(sidecarName, packageProxySidecarName, StringComparison.Ordinal))
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

        if (!string.IsNullOrWhiteSpace(sidecar.BuildContext))
        {
            string dockerfile = !string.IsNullOrWhiteSpace(sidecar.Dockerfile)
                ? sidecar.Dockerfile
                : Path.Combine(sidecar.BuildContext, "Dockerfile");
            string expectedSourceHash = ComputeBuildContextSourceHash(sidecar.BuildContext, dockerfile);
            bool imageExists = await ImageExistsAsync(sidecar.Image, cancellationToken).ConfigureAwait(false);
            if (imageExists)
            {
                string? currentSourceHash = await TryReadBuildContextSourceHashAsync(sidecar.Image, cancellationToken).ConfigureAwait(false);
                if (string.Equals(currentSourceHash, expectedSourceHash, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            if (applianceMode && IsPackageProxy(sidecar) && imageArchives is not null
                && allowlist.Images.FirstOrDefault(static entry => entry.Id == "git-pkgs-proxy") is { } baseImage)
                await imageArchives.TryEnsureImageAsync(baseImage.Image, cancellationToken).ConfigureAwait(false);

            return await BuildImageAsync(sidecar, dockerfile, expectedSourceHash, cancellationToken).ConfigureAwait(false);
        }

        if (await ImageExistsAsync(sidecar.Image, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        if (applianceMode && IsPackageProxy(sidecar))
        {
            logger.LogWarning("Package proxy image {Image} is missing from the appliance's local image store.", sidecar.Image);
            return false;
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

    private async Task<bool> BuildImageAsync(
        WorkspaceSidecarDefinition sidecar,
        string dockerfile,
        string sourceHash,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sidecar.BuildContext) || !Directory.Exists(sidecar.BuildContext))
        {
            logger.LogWarning("Sidecar {Name} build context '{BuildContext}' does not exist.", sidecar.Name, sidecar.BuildContext ?? string.Empty);
            return false;
        }

        List<string> args =
        [
            "build",
            "--label", $"{sidecarSourceHashLabel}={sourceHash}",
            "-t", sidecar.Image,
            "-f", dockerfile,
            sidecar.BuildContext
        ];
        if (applianceMode && IsPackageProxy(sidecar))
        {
            // The release supplies the pinned base as an OCI archive. Build offline
            // and retain the Docker health check used by Podman's scheduled probe.
            args.InsertRange(1, ["--pull=never", "--network=none", "--format=docker"]);
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Building sidecar image {Image} from {BuildContext}; build input hash is {SourceHash}.", sidecar.Image, sidecar.BuildContext, sourceHash);
        }
        PodmanCommandResult buildResult = await this.podmanCommandRunner.RunAsync(args, 300, cancellationToken).ConfigureAwait(false);
        if (buildResult.ExitCode == 0)
        {
            return true;
        }

        logger.LogWarning("Failed to build sidecar image {Image}: {Reason}", sidecar.Image, PodmanFailureFormatter.FormatDetails(buildResult));
        return false;
    }

    internal static string ComputeBuildContextSourceHash(string buildContext, string dockerfile)
    {
        if (!Directory.Exists(buildContext))
        {
            throw new DirectoryNotFoundException($"Sidecar build context '{buildContext}' was not found.");
        }

        if (!File.Exists(dockerfile))
        {
            throw new FileNotFoundException("Sidecar Dockerfile was not found.", dockerfile);
        }

        string root = Path.GetFullPath(buildContext);
        string dockerfilePath = Path.GetFullPath(dockerfile);
        string[] files = [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .Append(dockerfilePath)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static path => path, StringComparer.Ordinal)];

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in files)
        {
            string relativePath = Path.GetRelativePath(root, file).Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relativePath));
            hash.AppendData([0]);
            using FileStream stream = File.OpenRead(file);
            byte[] buffer = new byte[64 * 1024];
            int bytesRead;
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                hash.AppendData(buffer, 0, bytesRead);
            }
            hash.AppendData([0]);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private async Task<string?> TryReadBuildContextSourceHashAsync(string image, CancellationToken cancellationToken)
    {
        PodmanCommandResult inspectResult = await this.podmanCommandRunner.RunAsync(
            ["image", "inspect", "--format", $"{{{{ index .Config.Labels \"{sidecarSourceHashLabel}\" }}}}", image],
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

    private async Task<bool> RunSidecarAsync(WorkspaceSidecarDefinition sidecar, CancellationToken cancellationToken)
    {
        PodmanCommandResult runResult = await podmanCommandRunner.RunAsync(BuildRunArguments(sidecar), 30, cancellationToken).ConfigureAwait(false);
        if (runResult.ExitCode == 0)
        {
            return true;
        }

        logger.LogWarning("Failed to run sidecar {Name}: {Reason}", sidecar.Name, PodmanFailureFormatter.FormatDetails(runResult));
        return false;
    }

    internal async Task<bool> CreateAuthorizeAndStartPackageProxyAsync(WorkspaceSidecarDefinition sidecar, CancellationToken cancellationToken)
    {
        if (egressTopologyManager is null)
        {
            logger.LogError("Package proxy cannot start without the NAC egress allowlist manager.");
            return false;
        }

        PodmanCommandResult imageInspect = await podmanCommandRunner.RunAsync(
            ["image", "inspect", "--format", "{{.Id}}", sidecar.Image],
            10,
            cancellationToken).ConfigureAwait(false);
        string imageId = NormalizeImageId(imageInspect.StdOut.Trim());
        if (imageInspect.ExitCode != 0 || !IsImageId(imageId))
        {
            logger.LogWarning(
                "Package proxy image ID could not be verified (exit {ExitCode}, output {Output}, error {Error}); refusing to start it.",
                imageInspect.ExitCode,
                imageInspect.StdOut.Trim(),
                imageInspect.StdErr.Trim());
            return false;
        }

        List<string> createArguments = BuildRunArguments(sidecar);
        createArguments[0] = "create";
        createArguments.Remove("-d");
        int imageIndex = createArguments.IndexOf(sidecar.Image);
        if (imageIndex < 0)
        {
            throw new InvalidOperationException("Package proxy create arguments do not contain the image reference.");
        }

        createArguments.InsertRange(imageIndex, ["--pull=never", "--annotation", $"io.kautuka.image-id={imageId}"]);
        PodmanCommandResult createResult = await podmanCommandRunner.RunAsync(createArguments, 30, cancellationToken).ConfigureAwait(false);
        if (createResult.ExitCode != 0)
        {
            logger.LogWarning("Failed to create package proxy container: {Reason}", PodmanFailureFormatter.FormatDetails(createResult));
            return false;
        }

        string containerId = createResult.StdOut.Trim();
        if (string.IsNullOrWhiteSpace(containerId))
        {
            PodmanCommandResult idResult = await podmanCommandRunner.RunAsync(
                ["inspect", "--format", "{{.Id}}", sidecar.Name], 10, cancellationToken).ConfigureAwait(false);
            containerId = idResult.StdOut.Trim();
            if (idResult.ExitCode != 0 || string.IsNullOrWhiteSpace(containerId))
            {
                await RemoveSidecarBestEffortAsync(sidecar.Name).ConfigureAwait(false);
                return false;
            }
        }

        PodmanCommandResult createdInspect = await podmanCommandRunner.RunAsync(
            ["inspect", containerId], 10, cancellationToken).ConfigureAwait(false);
        if (createdInspect.ExitCode != 0 || !IsPackageProxyInspectAuthorized(createdInspect.StdOut, imageId, requireRunningIp: false))
        {
            await RemoveSidecarBestEffortAsync(sidecar.Name).ConfigureAwait(false);
            logger.LogWarning("Created package proxy did not match the verified image/identity spec; refusing to start it.");
            return false;
        }

        await egressTopologyManager.AuthorizePackageProxyContainerAsync(containerId, imageId, cancellationToken).ConfigureAwait(false);
        PodmanCommandResult start = await podmanCommandRunner.RunAsync(["start", sidecar.Name], 30, cancellationToken).ConfigureAwait(false);
        if (start.ExitCode != 0)
        {
            await RevokeAndRemovePackageProxyAsync(containerId, sidecar.Name, cancellationToken).ConfigureAwait(false);
            logger.LogWarning("Failed to start package proxy after NAC authorization: {Reason}", PodmanFailureFormatter.FormatDetails(start));
            return false;
        }

        ContainerState running = await InspectStateAsync(sidecar.Name, cancellationToken).ConfigureAwait(false);
        if (!running.IsRunning
            || running.InspectJson is null
            || !IsPackageProxyInspectAuthorized(running.InspectJson, imageId, requireRunningIp: true))
        {
            await RevokeAndRemovePackageProxyAsync(containerId, sidecar.Name, cancellationToken).ConfigureAwait(false);
            logger.LogError("Running package proxy failed its post-start image/IP/identity verification; it was removed.");
            return false;
        }

        return true;
    }

    private async Task<bool> AuthorizeExistingPackageProxyAsync(
        WorkspaceSidecarDefinition sidecar,
        string inspectJson,
        string? imageId,
        CancellationToken cancellationToken)
    {
        if (egressTopologyManager is null)
        {
            logger.LogError("Package proxy cannot be admitted without the NAC egress allowlist manager.");
            return false;
        }

        if (imageId is null)
        {
            PodmanCommandResult imageInspect = await podmanCommandRunner.RunAsync(
                ["image", "inspect", "--format", "{{.Id}}", sidecar.Image], 10, cancellationToken).ConfigureAwait(false);
            imageId = imageInspect.ExitCode == 0 ? imageInspect.StdOut.Trim() : string.Empty;
        }
        imageId = NormalizeImageId(imageId);
        if (!IsImageId(imageId)
            || !IsPackageProxyInspectAuthorized(inspectJson, imageId, requireRunningIp: true))
        {
            logger.LogWarning("Existing package proxy failed Manager identity/image/IP verification.");
            return false;
        }

        string containerId = ReadInspectString(inspectJson, "Id");
        await egressTopologyManager.AuthorizePackageProxyContainerAsync(containerId, imageId, cancellationToken).ConfigureAwait(false);
        if (!await egressTopologyManager.VerifyPackageProxyRulesetAsync(containerId, cancellationToken).ConfigureAwait(false))
        {
            egressTopologyManager.SetPackageProxyReady(false);
            await RevokeAndRemovePackageProxyAsync(containerId, sidecar.Name, cancellationToken).ConfigureAwait(false);
            logger.LogError("Package proxy did not have the required Envoy-only workload rules; it was removed.");
            return false;
        }

        egressTopologyManager.RegisterVerifiedPackageProxyContainer(containerId);
        return true;
    }

    private async Task RevokeAndRemovePackageProxyAsync(
        string containerId,
        string sidecarName,
        CancellationToken cancellationToken)
    {
        try
        {
            await egressTopologyManager!.RevokePackageProxyContainerAsync(containerId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await RemoveSidecarBestEffortAsync(sidecarName).ConfigureAwait(false);
        }
    }

    private static bool IsPackageProxy(WorkspaceSidecarDefinition sidecar)
        => string.Equals(sidecar.Name, packageProxySidecarName, StringComparison.Ordinal);

    private static bool IsImageId(string imageId)
        => imageId.StartsWith("sha256:", StringComparison.Ordinal)
            && imageId.Length == 71
            && imageId.AsSpan("sha256:".Length).ToString().All(Uri.IsHexDigit);

    private static string NormalizeImageId(string imageId)
    {
        imageId = imageId.Trim();
        if (imageId.Length == 64 && imageId.All(Uri.IsHexDigit))
        {
            return $"sha256:{imageId.ToLowerInvariant()}";
        }

        if (imageId.StartsWith("sha256:", StringComparison.Ordinal)
            && imageId.Length == 71
            && imageId.AsSpan("sha256:".Length).ToString().All(Uri.IsHexDigit))
        {
            return $"sha256:{imageId["sha256:".Length..].ToLowerInvariant()}";
        }

        return string.Empty;
    }

    private static string? DescribePackageProxyIdentityDrift(string inspectJson, string imageId)
        => IsPackageProxyInspectAuthorized(inspectJson, imageId, requireRunningIp: true)
            ? null
            : "its NAC image, identity, workload IP, or DNS differs";

    private static bool IsPackageProxyInspectAuthorized(string inspectJson, string imageId, bool requireRunningIp)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(inspectJson);
            JsonElement root = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement[0] : document.RootElement;
            JsonElement config = root.GetProperty("Config");
            JsonElement hostConfig = root.GetProperty("HostConfig");
            JsonElement labels = config.GetProperty("Labels");
            JsonElement annotations = config.TryGetProperty("Annotations", out JsonElement annotationElem) ? annotationElem : default;
            string actualImageId = NormalizeImageId(ReadInspectString(root, "Image"));
            bool labelsMatch = ReadInspectString(labels, "io.kautuka.user") == "system"
                && ReadInspectString(labels, "io.kautuka.agent") == "package-proxy"
                && ReadInspectString(labels, "io.kautuka.kind") == "agent-sandbox"
                && ReadInspectString(labels, "io.kautuka.exec-context") == "agent-sandbox"
                && ReadInspectString(labels, "io.kautuka.egress") == "normal";
            bool annotationsMatch = ReadInspectString(annotations, "io.kautuka.role") == "package-proxy"
                && ReadInspectString(annotations, "io.kautuka.image-id") == imageId;
            bool dnsMatches = hostConfig.TryGetProperty("Dns", out JsonElement dns)
                && dns.ValueKind == JsonValueKind.Array
                && dns.GetArrayLength() == 1
                && string.Equals(dns[0].GetString(), NetworkAccess.EgressTopologyManager.EgressPodIp, StringComparison.Ordinal);
            if (!string.Equals(actualImageId, imageId, StringComparison.Ordinal)
                || !labelsMatch
                || !annotationsMatch
                || !dnsMatches)
            {
                return false;
            }

            if (!requireRunningIp)
            {
                return true;
            }

            return root.TryGetProperty("NetworkSettings", out JsonElement networkSettings)
                && networkSettings.TryGetProperty("Networks", out JsonElement networks)
                && networks.TryGetProperty(NetworkAccess.WorkloadContainerSpec.DefaultNetworkName, out JsonElement workloadNetwork)
                && ReadInspectString(workloadNetwork, "IPAddress") == NetworkAccess.WorkloadRulesetRenderer.PackageProxyIp;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return false;
        }
    }

    private static string ReadInspectString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

    private static string ReadInspectString(string inspectJson, string property)
    {
        using JsonDocument document = JsonDocument.Parse(inspectJson);
        JsonElement root = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement[0] : document.RootElement;
        return ReadInspectString(root, property);
    }

    private async Task RemoveSidecarBestEffortAsync(string name)
    {
        try
        {
            await podmanCommandRunner.RunAsync(["rm", "-f", name], 15, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to remove invalid package proxy container '{Name}'.", name);
        }
    }

    internal static string ResolveNetwork(WorkspaceSidecarDefinition sidecar)
        => string.Equals(sidecar.Name, telemetrySidecarName, StringComparison.Ordinal)
            ? telemetryNetworkName
            : string.Equals(sidecar.Name, packageProxySidecarName, StringComparison.Ordinal)
                ? NetworkAccess.WorkloadContainerSpec.DefaultNetworkName
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
            "--network", string.Equals(sidecar.Name, packageProxySidecarName, StringComparison.Ordinal)
                ? $"{ResolveNetwork(sidecar)}:ip={NetworkAccess.WorkloadRulesetRenderer.PackageProxyIp}"
                : ResolveNetwork(sidecar),
            "--network-alias", sidecar.Name
        ];

        if (string.Equals(sidecar.Name, packageProxySidecarName, StringComparison.Ordinal))
        {
            args.AddRange([
                "--annotation", "io.kautuka.role=package-proxy",
                "--dns", NetworkAccess.EgressTopologyManager.EgressPodIp,
                "--health-cmd", "wget -q -O /dev/null http://127.0.0.1:20101/health || exit 1",
                "--health-interval", "5s",
                "--health-timeout", "3s",
                "--health-start-period", "15s",
                "--health-retries", "6",
                "--label", "io.kautuka.user=system",
                "--label", "io.kautuka.agent=package-proxy",
                "--label", "io.kautuka.exec-context=agent-sandbox",
                "--label", "io.kautuka.kind=agent-sandbox",
                "--label", "io.kautuka.egress=normal",
                "--label", "io.kautuka.compat=",
            ]);
        }

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
                ? $"{volume.HostPath}:{volume.ContainerPath}:ro,Z"
                : string.Equals(sidecar.Name, "kautuka-openobserve", StringComparison.Ordinal)
                    ? $"{volume.HostPath}:{volume.ContainerPath}:Z"
                    : $"{volume.HostPath}:{volume.ContainerPath}:U,Z");
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
