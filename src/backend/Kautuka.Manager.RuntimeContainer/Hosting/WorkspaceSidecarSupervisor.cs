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
    ContainerEgressRuntime? egressRuntime = null,
    WorkspaceRuntimePaths? runtimePaths = null)
{
    private const string alwaysPolicyName = "ALWAYS";
    private const string packageHealthCommand = "wget -q -O /dev/null http://127.0.0.1:20101/health";
    private readonly WorkspaceSidecarCatalog catalog = catalog;
    private readonly IHttpClientFactory httpClientFactory = httpClientFactory;
    private readonly PodmanCommandRunner podmanCommandRunner = podmanCommandRunner;
    private readonly ManagerContainerStartupOptions startupOptions = startupOptions.Value;
    private readonly ILogger<WorkspaceSidecarSupervisor> logger = logger;
    private readonly RuntimeImageAllowlist allowlist = allowlist ?? RuntimeImageAllowlist.Empty;
    private readonly PodmanImageArchiveStore? imageArchives = imageArchives;
    private readonly WorkspaceRuntimePaths? runtimePaths = runtimePaths;
    private const string managedLabel = "kautuka.managed=manager";
    private const string sidecarRoleLabel = "kautuka.role=sidecar";
    private const string sidecarSourceHashLabel = "io.kautuka.sidecar.source-hash";
    private const string sandboxNetworkName = "kautuka-sandbox";
    private const string telemetryNetworkName = "kautuka-telemetry";
    private const string telemetrySidecarName = "kautuka-openobserve";
    private readonly ConcurrentQueue<string> driftNotes = new();
    private const int defaultRunningProbeTimeoutSeconds = 45;
    private const int defaultRunningProbeIntervalMilliseconds = 1000;

    public IReadOnlyList<string> ManagedContainerNames => catalog.Sidecars
        .Where(sidecar => egressRuntime?.Enabled != true || sidecar.Name == "kautuka-pkgs-proxy" || egressRuntime.TelemetryEnabled && sidecar.Name == telemetrySidecarName).Select(static sidecar => sidecar.Name).ToArray();

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

        if (egressRuntime?.Enabled == true)
        {
            WorkspaceSidecarDefinition package = catalog.Sidecars.SingleOrDefault(static sidecar => sidecar.Name == "kautuka-pkgs-proxy")
                ?? throw new InvalidOperationException("The required package service is missing from the verified catalog.");
            bool packageReady = await EnsurePackageEgressAsync(package, cancellationToken).ConfigureAwait(false);
            if (egressRuntime.TelemetryEnabled)
            {
                WorkspaceSidecarDefinition? telemetry = catalog.Sidecars.SingleOrDefault(static sidecar => sidecar.Name == telemetrySidecarName);
                if (telemetry is not null && ShouldEnableSidecar(telemetry))
                {
                    bool telemetryReady;
                    try { telemetryReady = await EnsureTelemetryEgressAsync(telemetry, cancellationToken).ConfigureAwait(false); }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        logger.LogWarning(error, "Optional managed telemetry is not ready.");
                        telemetryReady = false;
                    }
                    if (telemetry.RequiredForReady && !telemetryReady) return false;
                }
            }
            return packageReady;
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
                logger.LogInformation("Sidecar {Name} is ready.", sidecar.Name);
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

        bool ready = await IsHealthyAsync(sidecar, cancellationToken).ConfigureAwait(false);
        if (ready)
        {
            logger.LogInformation("Sidecar {Name} is ready.", sidecar.Name);
        }
        return ready;
    }

    private async Task<bool> EnsurePackageEgressAsync(WorkspaceSidecarDefinition sidecar, CancellationToken cancellationToken)
    {
        ContainerEgressRuntime runtime = egressRuntime!;
        if (sidecar.Ports.Count != 0 || sidecar.Command.Count != 0 || sidecar.HealthCheck is not null
            || !sidecar.Environment.TryGetValue("PROXY_LISTEN", out string? listen) || listen != ":20101"
            || !sidecar.Environment.TryGetValue("PROXY_BASE_URL", out string? baseUrl) || baseUrl != "http://kautuka-pkgs-proxy:20101")
            throw new InvalidOperationException("The package service must use its unpublished fixed endpoint and released command.");
        string image = allowlist.Images.SingleOrDefault(static entry => entry.Id == "git-pkgs-proxy")?.Image
            ?? throw new InvalidOperationException("The verified package image is missing from the release inventory.");
        ContainerImageProvenancePolicy.EnsureTrusted(image, "package service");
        ResolvedRuntimeImage resolved = await runtime.ResolveRuntimeImageAsync(image, cancellationToken).ConfigureAwait(false);
        string executionImage = resolved.ForSource(image);
        sidecar = sidecar with { Image = image, BuildContext = null, Dockerfile = null };
        await runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        ContainerState state = await InspectStateAsync(sidecar.Name, cancellationToken).ConfigureAwait(false);
        PreparedPodmanContainer? existing = state.IsRunning ? await runtime.FindSourceAsync(sidecar.Name, cancellationToken).ConfigureAwait(false) : null;
        if (existing is not null)
        {
            string imageId = (await podmanCommandRunner.RunAsync(["image", "inspect", "--format", "{{.Id}}", executionImage], 10, cancellationToken).ConfigureAwait(false)).StdOut.Trim();
            if (await PackageSecurityMatchesAsync(state.InspectJson!, executionImage, cancellationToken).ConfigureAwait(false)
                && LiveContainerSpec.DescribeSidecarDrift(state.InspectJson!, executionImage, [], imageId, sidecar.Volumes, sidecar.Environment, "kautuka-core") is null)
                return await IsHealthyAsync(sidecar, cancellationToken).ConfigureAwait(false);
        }
        if (state.Exists)
        {
            using JsonDocument inspected = JsonDocument.Parse(state.InspectJson!);
            await runtime.RetireAsync(inspected.RootElement[0].GetProperty("Id").GetString()!, cancellationToken).ConfigureAwait(false);
        }
        if (!EnsureVolumeHostPaths(sidecar)) return false;
        var identity = new ContainerEgressRuntime.SourceIdentity(new ContainerEgressRuntime.Scope("system", "core", "core"), "package", sidecar.Name);
        PreparedPodmanContainer prepared = await runtime.PrepareAndAuthorizeAsync(identity, sidecar.Name, (network, dns) =>
        {
            List<string> args = BuildRunArguments(sidecar, resolved);
            args[0] = "create"; args.Remove("-d");
            args[args.IndexOf("--network") + 1] = network;
            int alias = args.IndexOf("--network-alias"); args.RemoveRange(alias, 2);
            args.InsertRange(1, ["--pull=never", "--restart=no", "--dns", dns, "--sysctl", "net.ipv6.conf.all.disable_ipv6=1",
                "--user", "65532:65532", "--cap-drop=ALL", "--security-opt=no-new-privileges", "--read-only", "--tmpfs", "/tmp:rw,noexec,nosuid",
                "--health-cmd", packageHealthCommand, "--health-interval", "5s", "--health-timeout", "3s", "--health-retries", "3"]);
            return args;
        }, cancellationToken).ConfigureAwait(false);
        try
        {
            PodmanCommandResult started = await runtime.StartWithAdmissionAsync(prepared,
                token => podmanCommandRunner.RunAsync(prepared.StartArguments(attach: false), 15, token), cancellationToken).ConfigureAwait(false);
            if (started.ExitCode != 0) throw new InvalidOperationException(PodmanFailureFormatter.FormatFailure("Start package service", started));
            return await IsHealthyAsync(sidecar, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await runtime.RetireAsync(prepared.Id, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<bool> EnsureTelemetryEgressAsync(WorkspaceSidecarDefinition sidecar, CancellationToken cancellationToken)
    {
        ContainerEgressRuntime runtime = egressRuntime!;
        OpenObserveCredentialStore credentialStore = new(runtimePaths?.SharedDataRoot
            ?? throw new InvalidOperationException("Managed telemetry credentials require the Manager shared state root."));
        try
        {
            _ = credentialStore.ReadIngestionAuthorizationHeader();
        }
        catch (InvalidOperationException error)
        {
            credentialStore.ClearIngestionAuthorizationHeader();
            logger.LogWarning(error, "The persisted managed telemetry ingestion credential was invalid and has been disabled.");
        }
        string rootPassword = credentialStore.GetOrCreateRootPassword();
        Dictionary<string, string> collectorEnvironment = sidecar.Environment.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);
        collectorEnvironment["ZO_ROOT_USER_PASSWORD"] = rootPassword;
        sidecar = sidecar with { Environment = collectorEnvironment };
        if (sidecar.Command.Count != 0) throw new InvalidOperationException("Managed telemetry requires the released collector command.");
        string image = allowlist.Images.SingleOrDefault(static entry => entry.Id == "openobserve")?.Image
            ?? throw new InvalidOperationException("The verified telemetry image is missing from the release inventory.");
        ResolvedRuntimeImage resolved = await runtime.ResolveRuntimeImageAsync(image, cancellationToken).ConfigureAwait(false);
        string executionImage = resolved.ForSource(image);
        sidecar = sidecar with { Image = image, Ports = [], BuildContext = null, Dockerfile = null };
        await runtime.EnsureReadyAsync(cancellationToken).ConfigureAwait(false);
        ContainerState state = await InspectStateAsync(sidecar.Name, cancellationToken).ConfigureAwait(false);
        PreparedPodmanContainer? existing = state.IsRunning ? await runtime.FindSourceAsync(sidecar.Name, cancellationToken).ConfigureAwait(false) : null;
        if (existing is not null)
        {
            PodmanCommandResult imageInspection = await podmanCommandRunner.RunAsync(["image", "inspect", "--format", "{{.Id}}", executionImage], 10, cancellationToken).ConfigureAwait(false);
            if (imageInspection.ExitCode != 0) throw new ContainerInspectionUnavailableException("Telemetry image identity inspection failed.");
            if (await TelemetrySecurityMatchesAsync(state.InspectJson!, executionImage, cancellationToken).ConfigureAwait(false)
                && LiveContainerSpec.DescribeSidecarDrift(state.InspectJson!, executionImage, [], imageInspection.StdOut.Trim(), sidecar.Volumes, sidecar.Environment, "kautuka-core") is null)
            {
                bool healthy = await runtime.ProbeTelemetryAsync(existing, cancellationToken).ConfigureAwait(false);
                return healthy && await EnsureTelemetryIngestionAuthorizationAsync(rootPassword, cancellationToken).ConfigureAwait(false);
            }
        }
        if (state.Exists)
        {
            using JsonDocument inspected = JsonDocument.Parse(state.InspectJson!);
            await runtime.RetireAsync(inspected.RootElement[0].GetProperty("Id").GetString()!, cancellationToken).ConfigureAwait(false);
        }
        if (!EnsureVolumeHostPaths(sidecar)) return false;
        var identity = new ContainerEgressRuntime.SourceIdentity(new ContainerEgressRuntime.Scope("system", "core", "core"), "core", telemetrySidecarName);
        string environmentFile = await PodmanContainerTool.WriteEphemeralEnvironmentFileAsync(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["ZO_ROOT_USER_PASSWORD"] = rootPassword },
            cancellationToken).ConfigureAwait(false);
        PreparedPodmanContainer prepared;
        try
        {
            WorkspaceSidecarDefinition launchDefinition = sidecar with
            {
                Environment = sidecar.Environment
                    .Where(static pair => pair.Key != "ZO_ROOT_USER_PASSWORD")
                    .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal),
            };
            prepared = await runtime.PrepareAndAuthorizeAsync(identity, sidecar.Name, (network, dns) =>
            {
                List<string> args = BuildRunArguments(launchDefinition, resolved);
                args[0] = "create"; args.Remove("-d");
                args[args.IndexOf("--network") + 1] = network;
                int alias = args.IndexOf("--network-alias"); args.RemoveRange(alias, 2);
                args.InsertRange(1, ["--env-file", environmentFile, "--pull=never", "--restart=no", "--dns", dns, "--sysctl", "net.ipv6.conf.all.disable_ipv6=1",
                    "--user", "0:0", "--cap-drop=ALL", "--security-opt=no-new-privileges", "--read-only", "--tmpfs", "/tmp:rw,noexec,nosuid"]);
                return args;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (File.Exists(environmentFile)) PodmanContainerTool.DeleteSecretFile(environmentFile);
        }
        try
        {
            PodmanCommandResult started = await runtime.StartWithAdmissionAsync(prepared,
                token => podmanCommandRunner.RunAsync(prepared.StartArguments(attach: false), 15, token), cancellationToken).ConfigureAwait(false);
            if (started.ExitCode != 0) throw new InvalidOperationException(PodmanFailureFormatter.FormatFailure("Start telemetry service", started));
            bool healthy = await runtime.ProbeTelemetryAsync(prepared, cancellationToken).ConfigureAwait(false);
            return healthy && await EnsureTelemetryIngestionAuthorizationAsync(rootPassword, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await runtime.RetireAsync(prepared.Id, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<bool> EnsureTelemetryIngestionAuthorizationAsync(string rootPassword, CancellationToken cancellationToken)
    {
        try
        {
            string header = await OpenObserveIngestionTokenProvisioner.EnsureAsync(
                egressRuntime!.ArtifactSocketPath,
                rootPassword,
                cancellationToken).ConfigureAwait(false);
            new OpenObserveCredentialStore(runtimePaths!.SharedDataRoot).SaveIngestionAuthorizationHeader(header);
            return true;
        }
        catch (OpenObserveIngestionTokenDisabledException error)
        {
            new OpenObserveCredentialStore(runtimePaths!.SharedDataRoot).ClearIngestionAuthorizationHeader();
            logger.LogWarning(error, "The managed telemetry ingestion token is disabled; telemetry exporters remain disabled until repaired.");
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            logger.LogWarning(error, "Managed telemetry ingestion credentials could not be provisioned; telemetry exporters remain disabled.");
            return false;
        }
    }

    private async Task<bool> TelemetrySecurityMatchesAsync(string inspected, string image, CancellationToken cancellationToken)
    {
        using JsonDocument actual = JsonDocument.Parse(inspected);
        PodmanCommandResult imageInspection = await podmanCommandRunner.RunAsync(["image", "inspect", image], 10, cancellationToken).ConfigureAwait(false);
        if (imageInspection.ExitCode != 0) throw new ContainerInspectionUnavailableException("Telemetry image inspection failed.");
        using JsonDocument expected = JsonDocument.Parse(imageInspection.StdOut);
        JsonElement container = actual.RootElement[0];
        JsonElement config = container.GetProperty("Config");
        JsonElement host = container.GetProperty("HostConfig");
        JsonElement expectedConfig = expected.RootElement[0].GetProperty("Config");
        return config.GetProperty("User").GetString() == "0:0"
            && host.GetProperty("ReadonlyRootfs").GetBoolean()
            && host.GetProperty("RestartPolicy").GetProperty("Name").GetString() is "no" or ""
            && host.GetProperty("SecurityOpt").EnumerateArray().Any(static value => value.GetString() == "no-new-privileges")
            && NoCapabilities(container.GetProperty("BoundingCaps")) && NoCapabilities(container.GetProperty("EffectiveCaps"))
            && TelemetryImageCommandMatches(config, expectedConfig, "Entrypoint")
            && TelemetryImageCommandMatches(config, expectedConfig, "Cmd");
    }

    internal static bool TelemetryImageCommandMatches(JsonElement actual, JsonElement image, string property)
    {
        static string[]? Parts(JsonElement config, string key)
        {
            if (!config.TryGetProperty(key, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return [];
            if (value.ValueKind != JsonValueKind.Array || value.EnumerateArray().Any(static item => item.ValueKind != JsonValueKind.String)) return null;
            return [.. value.EnumerateArray().Select(static item => item.GetString()!)];
        }
        string[]? observed = Parts(actual, property);
        string[]? expected = Parts(image, property);
        return observed is not null && expected is not null && observed.SequenceEqual(expected, StringComparer.Ordinal);
    }

    private static bool NoCapabilities(JsonElement capabilities)
        => capabilities.ValueKind == JsonValueKind.Null || capabilities.ValueKind == JsonValueKind.Array && capabilities.GetArrayLength() == 0;

    private async Task<bool> PackageSecurityMatchesAsync(string inspected, string image, CancellationToken cancellationToken)
    {
        using JsonDocument actual = JsonDocument.Parse(inspected);
        using JsonDocument expected = JsonDocument.Parse((await podmanCommandRunner.RunAsync(["image", "inspect", image], 10, cancellationToken).ConfigureAwait(false)).StdOut);
        JsonElement container = actual.RootElement[0];
        JsonElement config = container.GetProperty("Config");
        JsonElement host = container.GetProperty("HostConfig");
        JsonElement expectedConfig = expected.RootElement[0].GetProperty("Config");
        return config.GetProperty("User").GetString() == "65532:65532"
            && host.GetProperty("ReadonlyRootfs").GetBoolean()
            && host.GetProperty("RestartPolicy").GetProperty("Name").GetString() is "no" or ""
            && host.GetProperty("SecurityOpt").EnumerateArray().Any(static value => value.GetString() == "no-new-privileges")
            && NoCapabilities(container.GetProperty("BoundingCaps")) && NoCapabilities(container.GetProperty("EffectiveCaps"))
            && JsonElement.DeepEquals(config.GetProperty("Entrypoint"), expectedConfig.GetProperty("Entrypoint"))
            && JsonElement.DeepEquals(config.GetProperty("Cmd"), expectedConfig.GetProperty("Cmd"))
            && config.TryGetProperty("Healthcheck", out JsonElement health)
            && health.GetProperty("Test").EnumerateArray().Select(static value => value.GetString()).SequenceEqual(new[] { "CMD-SHELL", packageHealthCommand })
            && health.GetProperty("Interval").GetInt64() == 5_000_000_000 && health.GetProperty("Timeout").GetInt64() == 3_000_000_000
            && health.GetProperty("Retries").GetInt32() == 3;
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

            return await BuildImageAsync(sidecar, dockerfile, expectedSourceHash, cancellationToken).ConfigureAwait(false);
        }

        if (await ImageExistsAsync(sidecar.Image, cancellationToken).ConfigureAwait(false))
        {
            return true;
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

    private static List<string> BuildRunArguments(WorkspaceSidecarDefinition sidecar, ResolvedRuntimeImage? resolved = null)
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

        args.Add(resolved?.ForSource(sidecar.Image) ?? sidecar.Image);

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
