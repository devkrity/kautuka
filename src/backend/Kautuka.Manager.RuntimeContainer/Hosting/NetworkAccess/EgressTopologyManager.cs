using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Kautuka.NetworkAccessController;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;

public sealed record InfraAllowlistEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("expected_ipv4")] IReadOnlyList<string> ExpectedIpv4,
    [property: JsonPropertyName("image_digest")] string ImageDigest,
    [property: JsonPropertyName("expected_ipv4_alternates"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<IReadOnlyList<string>>? ExpectedIpv4Alternates = null);

public sealed record InfraAllowlistDocument(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("entries")] IReadOnlyList<InfraAllowlistEntry> Entries);

[JsonSerializable(typeof(InfraAllowlistDocument))]
internal sealed partial class InfraAllowlistJsonContext : JsonSerializerContext;

/// <summary>
/// Manages rootless Podman egress infrastructure (Section 4.2, 4.3):
/// 1. kautuka-workload and kautuka-outside networks.
/// 2. infra-allowlist.json writer (mode 0600, atomic replace).
/// 3. kautuka-egress multi-homed pod, NET_ADMIN init container, Envoy v1.40.2, CoreDNS 1.14.6.
/// 4. kautuka-pkgs-proxy at 10.247.0.3:20101 (Phase 6).
/// 5. Boundary ruleset verification via normalised nft hash (MF6).
/// 6. In-process registry rebuild from io.kautuka.* labels before Envoy listener accepts connections.
/// 7. Startup canary self-test gating workload creation.
/// 8. 30s background reconcile loop and emergency brake container stops.
/// </summary>
public sealed class EgressTopologyManager : IHostedService, IAsyncDisposable, IWorkloadStartupState, IWorkloadLaunchAdmission
{
    private sealed class StartupWorkloadContainmentException : InvalidOperationException
    {
        public StartupWorkloadContainmentException() { }
        public StartupWorkloadContainmentException(string message) : base(message) { }
        public StartupWorkloadContainmentException(string message, Exception innerException) : base(message, innerException) { }
    }
    private readonly record struct StartupGateStateChange(bool IsOpen, LaunchAdmissionGeneration? CancelAdmission);
    public const string WorkloadNetworkName = "kautuka-workload";
    public const string OutsideNetworkName = "kautuka-outside";
    public const string EgressPodName = "kautuka-egress";
    public const string PackageProxyContainerName = "kautuka-pkgs-proxy";
    public const string EgressPodIp = "10.247.0.2";
    public const string OutsidePodIp = "10.248.0.2";
    public const string OutsideGatewayIp = "10.248.0.1";
    public const string OutsideSubnet = "10.248.0.0/24";
    public const string OutsideLanSourceIp = NetworkAddressClassifier.LanSourceBindAddress;
    public const string PackageProxyIp = "10.247.0.3";
    public const string WorkloadSubnet = "10.247.0.0/16";

    private const string WorkloadNetworkRouteMetric = "1000";
    private const string OutsideNetworkRouteMetric = "100";

    // Image pins are embedded from the same manifest used by release tooling.
    private static readonly RuntimeImageAllowlist runtimeImages = LoadRuntimeImages();
    public static string EnvoyX64Image => FindRuntimeImage("envoy").Image;
    public static string CoreDnsX64Image => FindRuntimeImage("coredns").Image;
    public static string EnvoyPinnedImage => SelectRuntimeImage("envoy", RuntimeInformation.OSArchitecture);
    public static string CoreDnsPinnedImage => SelectRuntimeImage("coredns", RuntimeInformation.OSArchitecture);
    public static string BoundaryInitImage => FindRuntimeImage("egress-boundary").Image;
    public static string CanaryBasePinnedImage => ResolveCanaryBaseImage(runtimeImages);
    internal static string ResolveCanaryBaseImage(RuntimeImageAllowlist images)
        => images.Images.Single(entry => string.Equals(entry.Id, "kautuka-base", StringComparison.Ordinal)).Image;

    private static RuntimeImageAllowlist LoadRuntimeImages()
    {
        using Stream stream = typeof(EgressTopologyManager).Assembly.GetManifestResourceStream("Kautuka.RuntimeImages.json")
            ?? throw new InvalidOperationException("Embedded runtime image manifest is missing.");
        return JsonSerializer.Deserialize(stream, RuntimeImageAllowlistJsonContext.Default.RuntimeImageAllowlist)
            ?? throw new InvalidOperationException("Embedded runtime image manifest is invalid.");
    }

    private static RuntimeImageEntry FindRuntimeImage(string id)
        => runtimeImages.Images.Single(entry => string.Equals(entry.Id, id, StringComparison.Ordinal));

    internal static string SelectRuntimeImage(string id, Architecture architecture)
    {
        RuntimeImageEntry entry = FindRuntimeImage(id);
        return architecture == Architecture.X64 ? entry.Image
            : entry.Arm64Image ?? throw new InvalidOperationException($"No non-x64 image is configured for '{id}'.");
    }

    private readonly PodmanCommandRunner podmanRunner;
    private readonly WorkloadAddressAllocator addressAllocator;
    private readonly WorkloadPostStartVerifier postStartVerifier;
    private readonly WorkspaceDesiredStateStore? desiredStateStore;
    private readonly ImageCompatibilityStore? imageCompatibilityStore;
    private readonly NetworkAccessPolicyStore? policyStore;
    private readonly IWorkloadRegistry registry;
    private readonly string stateDirectory;
    private readonly string envoySocketHostDir;
    private readonly string allowlistFilePath;
    private readonly string startupGateFilePath;
    private readonly string hookLogFilePath;
    private readonly ILogger<EgressTopologyManager> logger;
    private readonly PodmanImageArchiveStore? imageArchives;
    private readonly string? windowsDecisionEndpointIp;
    private readonly string? windowsEnvoyCertificateDirectory;
    private readonly string? windowsPodmanMachineName;
    private readonly string? windowsPodmanHookStateDirectory;
    private readonly bool useDevelopmentSelinuxDomain;

    // Gate and launch-admission state are changed together under gateLock. Closing
    // admission cancels the current lease generation; reopening starts a new one.
    // Recovery flags describe independent checks that may need retrying and do not
    // by themselves authorize a reopen. A latched incident keeps admission closed.
    private readonly object gateLock = new();
    private bool isStartupGateOpen;
    private bool launchAdmissionClosed = true;
    private int activeLaunchLeases;
    private LaunchAdmissionGeneration launchAdmissionGeneration = new();
    private volatile bool topologyIncidentLatched;
    private volatile bool topologyContainmentComplete;
    private volatile bool transientRecoveryPending;
    private volatile bool workloadSnapshotRetryPending;
    private volatile bool startupCanaryRetryPending;
    private volatile bool startupInitializationRetryPending;
    private long admissionCloseVersion;
    private volatile bool startupCanaryBrakeObserved;
    private bool startupIsWindowsHostNativeDev;
    private string? startupDecisionEndpointIp;
    private bool startupIncludeLanGrants;
    private string? expectedBoundaryRulesetHash;
    private string? expectedBoundaryNftSourceHash;
    private long unlabelledWorkloadCount;
    private CancellationTokenSource? reconcileCts;
    private Task? reconcileTask;
    private readonly object disposeLock = new();
    private Task? disposeTask;
    private readonly HashSet<string> knownInfraContainerIds = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<InfraAllowlistEntry> baseInfraAllowlistEntries = [];
    private InfraAllowlistEntry? packageProxyAllowlistEntry;
    private readonly Dictionary<string, InfraAllowlistEntry> applianceAllowlistEntries = new(StringComparer.Ordinal);
    private readonly object infraAllowlistLock = new();
    private volatile bool packageProxyStartupReady;
    private readonly ConcurrentDictionary<string, RegistryEntry> transientWorkloadDenials = new(StringComparer.OrdinalIgnoreCase);

    private readonly string? gatewayConnectEnvoyDirectory;

    public EgressTopologyManager(
        PodmanCommandRunner podmanRunner,
        WorkloadAddressAllocator addressAllocator,
        WorkloadPostStartVerifier postStartVerifier,
        IWorkloadRegistry registry,
        string stateDirectory,
        ILogger<EgressTopologyManager>? logger = null,
        string? envoySocketHostDir = null,
        string? hookStateDirectory = null,
        string? windowsDecisionEndpointIp = null,
        string? windowsEnvoyCertificateDirectory = null,
        string? windowsPodmanMachineName = null,
        string? windowsPodmanHookStateDirectory = null,
        WorkspaceDesiredStateStore? desiredStateStore = null,
        ImageCompatibilityStore? imageCompatibilityStore = null,
        NetworkAccessPolicyStore? policyStore = null,
        bool useDevelopmentSelinuxDomain = false,
        PodmanImageArchiveStore? imageArchives = null,
        string? gatewayConnectEnvoyDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(podmanRunner);
        ArgumentNullException.ThrowIfNull(addressAllocator);
        ArgumentNullException.ThrowIfNull(postStartVerifier);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);

        this.podmanRunner = podmanRunner;
        this.addressAllocator = addressAllocator;
        this.postStartVerifier = postStartVerifier;
        this.desiredStateStore = desiredStateStore;
        this.imageCompatibilityStore = imageCompatibilityStore;
        this.policyStore = policyStore;
        this.useDevelopmentSelinuxDomain = useDevelopmentSelinuxDomain;
        this.imageArchives = imageArchives;
        this.gatewayConnectEnvoyDirectory = gatewayConnectEnvoyDirectory;
        this.registry = registry;
        this.windowsDecisionEndpointIp = windowsDecisionEndpointIp;
        this.windowsEnvoyCertificateDirectory = string.IsNullOrWhiteSpace(windowsEnvoyCertificateDirectory)
            ? null
            : Path.GetFullPath(windowsEnvoyCertificateDirectory);
        this.windowsPodmanMachineName = windowsPodmanMachineName;
        this.windowsPodmanHookStateDirectory = windowsPodmanHookStateDirectory;
        this.stateDirectory = Path.GetFullPath(stateDirectory);
        this.logger = logger ?? NullLogger<EgressTopologyManager>.Instance;
        this.envoySocketHostDir = Path.GetFullPath(string.IsNullOrWhiteSpace(envoySocketHostDir)
            ? Path.Combine(this.stateDirectory, "sockets")
            : envoySocketHostDir);

        string nacDir = Path.Combine(this.stateDirectory, "nac");
        Directory.CreateDirectory(nacDir);
        string resolvedHookDir = !string.IsNullOrWhiteSpace(hookStateDirectory)
            ? Path.GetFullPath(hookStateDirectory)
            : (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XDG_STATE_HOME"))
                ? Path.Combine(Environment.GetEnvironmentVariable("XDG_STATE_HOME")!, "kautuka", "nac")
                : nacDir);
        Directory.CreateDirectory(resolvedHookDir);
        allowlistFilePath = Path.Combine(resolvedHookDir, "infra-allowlist.json");
        startupGateFilePath = Path.Combine(nacDir, "startup-gate.status");
        hookLogFilePath = Path.Combine(resolvedHookDir, "hook.log");
    }

    public EgressTopologyManager(
        PodmanCommandRunner podmanRunner,
        WorkloadAddressAllocator addressAllocator,
        WorkloadPostStartVerifier postStartVerifier,
        string stateDirectory,
        ILogger<EgressTopologyManager>? logger = null)
        : this(podmanRunner, addressAllocator, postStartVerifier, new WorkloadRegistry(), stateDirectory, logger, null, null)
    {
    }

    public static bool ShouldUseEnvoySelinuxDomain(bool isLinux, bool isDevelopment, string? installedSetting, string? developmentSetting)
        => isLinux && (string.Equals(installedSetting, "1", StringComparison.Ordinal)
            || (isDevelopment && string.Equals(developmentSetting, "1", StringComparison.Ordinal)));

    public bool IsStartupGateOpen
    {
        get
        {
            lock (gateLock)
            {
                return isStartupGateOpen;
            }
        }
        private set
        {
            UpdateStartupGate(value);
        }
    }

    public bool IsStartupRetryPending => startupCanaryRetryPending;

    public long UnlabelledWorkloadCount => Interlocked.Read(ref unlabelledWorkloadCount);

    internal bool TopologyContainmentCompleteForTests => topologyContainmentComplete;

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The returned lease transfers ownership to the caller, which disposes it after launch verification and registration.")]
    public ValueTask<IWorkloadLaunchLease> AcquireLaunchLeaseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gateLock)
        {
            if (launchAdmissionClosed || !isStartupGateOpen)
            {
                throw new InvalidOperationException("Workload launch rejected: NAC launch admission is closed.");
            }

            LaunchAdmissionGeneration generation = launchAdmissionGeneration;
            activeLaunchLeases++;
            generation.ActiveLeases++;
            return ValueTask.FromResult<IWorkloadLaunchLease>(new WorkloadLaunchLease(
                () => ReleaseLaunchLease(generation),
                generation.Cancellation.Token));
        }
    }

    public void CloseLaunchAdmission()
    {
        StartupGateStateChange change;
        lock (gateLock)
        {
            admissionCloseVersion++;
            startupInitializationRetryPending = false;
            startupCanaryRetryPending = false;
            transientRecoveryPending = false;
            workloadSnapshotRetryPending = false;
            change = UpdateStartupGateStateUnderLock(isOpen: false);
        }

        CompleteStartupGateStateChange(change);
    }

    private void CloseForQuarantineCleanupRetry()
    {
        StartupGateStateChange change;
        lock (gateLock)
        {
            if (isStartupGateOpen)
            {
                transientRecoveryPending = true;
                workloadSnapshotRetryPending = true;
            }
            change = UpdateStartupGateStateUnderLock(isOpen: false);
        }
        CompleteStartupGateStateChange(change);
    }

    private void UpdateStartupGate(bool isOpen)
    {
        StartupGateStateChange change;
        lock (gateLock)
        {
            if (isOpen)
            {
                transientRecoveryPending = false;
            }
            change = UpdateStartupGateStateUnderLock(isOpen);
        }

        CompleteStartupGateStateChange(change);
    }

    private StartupGateStateChange UpdateStartupGateStateUnderLock(bool isOpen)
    {
        if (isOpen && launchAdmissionClosed && launchAdmissionGeneration.CancellationStarted)
        {
            launchAdmissionGeneration = new LaunchAdmissionGeneration();
        }

        isStartupGateOpen = isOpen;
        launchAdmissionClosed = !isOpen;
        LaunchAdmissionGeneration? cancelAdmission = null;
        if (!isOpen && !launchAdmissionGeneration.CancellationStarted)
        {
            launchAdmissionGeneration.CancellationStarted = true;
            cancelAdmission = launchAdmissionGeneration;
        }

        return new StartupGateStateChange(isStartupGateOpen, cancelAdmission);
    }

    private void CompleteStartupGateStateChange(StartupGateStateChange change)
    {
        if (change.CancelAdmission is not null)
        {
            LaunchAdmissionGeneration generation = change.CancelAdmission;
            _ = Task.Run(() => CancelLaunchAdmissionCallbacksAsync(generation));
        }

        try
        {
            File.WriteAllText(startupGateFilePath, change.IsOpen ? "open" : "closed");
        }
        catch
        {
            // Ignore file write errors
        }
    }

    private int ActiveLaunchLeaseCount
    {
        get
        {
            lock (gateLock)
            {
                return activeLaunchLeases;
            }
        }
    }

    private async Task CancelLaunchAdmissionCallbacksAsync(LaunchAdmissionGeneration generation)
    {
        try
        {
            await generation.Cancellation.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "A workload launch cancellation callback failed; topology containment continues with admission closed.");
        }
        finally
        {
            CancellationTokenSource? dispose = null;
            lock (gateLock)
            {
                generation.CancellationFinished = true;
                if (generation.ActiveLeases == 0 && !generation.Disposed)
                {
                    generation.Disposed = true;
                    dispose = generation.Cancellation;
                }
            }

            dispose?.Dispose();
        }
    }

    private void ReleaseLaunchLease(LaunchAdmissionGeneration generation)
    {
        CancellationTokenSource? dispose = null;
        lock (gateLock)
        {
            if (activeLaunchLeases <= 0 || generation.ActiveLeases <= 0)
            {
                logger.LogError("Ignored an unbalanced NAC launch lease release.");
                return;
            }

            activeLaunchLeases--;
            generation.ActiveLeases--;
            if (generation.CancellationFinished && generation.ActiveLeases == 0 && !generation.Disposed)
            {
                generation.Disposed = true;
                dispose = generation.Cancellation;
            }
        }

        dispose?.Dispose();
    }

    private sealed class LaunchAdmissionGeneration
    {
        public CancellationTokenSource Cancellation { get; } = new();

        public int ActiveLeases { get; set; }

        public bool CancellationStarted { get; set; }

        public bool CancellationFinished { get; set; }

        public bool Disposed { get; set; }
    }

    private sealed class WorkloadLaunchLease(Action release, CancellationToken cancellationToken) : IWorkloadLaunchLease
    {
        private Action? release = release;

        public CancellationToken CancellationToken { get; } = cancellationToken;

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref release, null)?.Invoke();
            return ValueTask.CompletedTask;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        long closeVersion;
        lock (gateLock) closeVersion = admissionCloseVersion;
        try
        {
            await InitializeProductionAsync(
                registry,
                isWindowsHostNativeDev: OperatingSystem.IsWindows(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            IsStartupGateOpen = false;
            logger.LogError(ex, "Failed to initialize EgressTopologyManager; closed startup gate.");
            lock (gateLock)
            {
                if (!topologyIncidentLatched && closeVersion == admissionCloseVersion)
                    startupInitializationRetryPending = true;
            }
            if (startupInitializationRetryPending) StartReconcileLoop(registry);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        CloseLaunchAdmission();
        await DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Initializes the production egress topology, including the kernel path for approved LAN traffic.
    /// The production Manager hosted service and the rootless acceptance harness share this entry point.
    /// </summary>
    public Task InitializeProductionAsync(
        IWorkloadRegistry registry,
        bool isWindowsHostNativeDev,
        CancellationToken cancellationToken = default)
        => InitializeAsync(
            registry,
            isWindowsHostNativeDev,
            decisionEndpointIp: windowsDecisionEndpointIp,
            includeLanGrants: true,
            cancellationToken);

    /// <summary>
    /// Ensures networks and allowlist are provisioned, egress pod is live with boundary rules,
    /// registry is rebuilt from container labels, startup canary passes, and reconcile loop starts.
    /// </summary>
    public Task InitializeAsync(
        IWorkloadRegistry registry,
        bool isWindowsHostNativeDev = false,
        string? decisionEndpointIp = null,
        bool includeLanGrants = false,
        CancellationToken cancellationToken = default)
        => InitializeCoreAsync(registry, isWindowsHostNativeDev, decisionEndpointIp, includeLanGrants, false, cancellationToken);

    private async Task InitializeCoreAsync(
        IWorkloadRegistry registry,
        bool isWindowsHostNativeDev,
        string? decisionEndpointIp,
        bool includeLanGrants,
        bool requireStartupRetryPermission,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registry);
        startupIsWindowsHostNativeDev = isWindowsHostNativeDev;
        startupDecisionEndpointIp = decisionEndpointIp ?? windowsDecisionEndpointIp;
        startupIncludeLanGrants = includeLanGrants;

        if (topologyIncidentLatched)
        {
            CloseLaunchAdmission();
            await ContainWorkloadNetworkMembersAsync(registry, "previous topology incident", cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("A topology incident is latched. This Manager process cannot reopen the NAC startup gate.");
        }

        logger.LogInformation("Initializing Kautuka egress topology...");

        // 0. Verify prerequisites (Podman info)
        PodmanCommandResult infoResult = await podmanRunner
            .RunAsync(["info", "--format", "{{.Host.Distribution.Distribution}}"], 10, cancellationToken)
            .ConfigureAwait(false);

        if (infoResult.ExitCode != 0)
        {
            IsStartupGateOpen = false;
            logger.LogError("Prerequisite check failed: Podman is not operational: {StdErr}", infoResult.StdErr);
            throw new InvalidOperationException($"Podman prerequisite check failed: {infoResult.StdErr}");
        }

        if (imageArchives is not null)
        {
            await imageArchives.TryEnsureImageAsync(BoundaryInitImage, cancellationToken).ConfigureAwait(false);
            await imageArchives.TryEnsureImageAsync(EnvoyPinnedImage, cancellationToken).ConfigureAwait(false);
            await imageArchives.TryEnsureImageAsync(CoreDnsPinnedImage, cancellationToken).ConfigureAwait(false);
        }

        // 1. Ensure networks exist
        await EnsureNetworksAsync(cancellationToken).ConfigureAwait(false);

        // 2. Ensure egress pod and containers exist, and write infra allowlist before start
        await EnsureEgressPodAsync(
            isWindowsHostNativeDev,
            startupDecisionEndpointIp,
            includeLanGrants,
            windowsEnvoyCertificateDirectory,
            cancellationToken).ConfigureAwait(false);

        if (OperatingSystem.IsLinux() || isWindowsHostNativeDev)
        {
            string renderedBoundaryHash = await ReadRenderedBoundaryRulesetHashAsync(cancellationToken).ConfigureAwait(false);
            string liveBoundaryHash = await ReadBoundaryRulesetHashAsync(cancellationToken).ConfigureAwait(false);
            RequireMatchingBoundaryRulesetHash(renderedBoundaryHash, liveBoundaryHash);
            expectedBoundaryRulesetHash = renderedBoundaryHash;
        }

        // 3. Rebuild registry from live containers before opening listener (Section 4.3.4 item 6)
        await RebuildRegistryFromLiveContainersAsync(registry, cancellationToken).ConfigureAwait(false);

        // 4. Run startup self-test canary (Section 4.3.4 item 5)
        bool canaryPassed = await RunStartupCanaryOrDeferAsync(registry, cancellationToken).ConfigureAwait(false);
        if (!canaryPassed)
        {
            IsStartupGateOpen = false;
            if (startupCanaryRetryPending)
            {
                if (requireStartupRetryPermission)
                {
                    lock (gateLock)
                    {
                        if (!startupInitializationRetryPending) startupCanaryRetryPending = false;
                        startupInitializationRetryPending = false;
                    }
                }
                return;
            }

            logger.LogError("Startup canary self-test failed; startup gate closed.");
            throw new InvalidOperationException("Network access control self-test failed.");
        }

        startupCanaryRetryPending = false;
        if (requireStartupRetryPermission)
        {
            StartupGateStateChange gateChange;
            lock (gateLock)
            {
                if (!startupInitializationRetryPending || topologyIncidentLatched) return;
                startupInitializationRetryPending = false;
                gateChange = UpdateStartupGateStateUnderLock(isOpen: true);
            }
            CompleteStartupGateStateChange(gateChange);
        }
        else IsStartupGateOpen = true;
        logger.LogInformation("Startup canary self-test passed; startup gate open.");

        // 5. Start 30s reconcile loop
        StartReconcileLoop(registry);
    }

    private async Task<bool> RunStartupCanaryOrDeferAsync(IWorkloadRegistry registry, CancellationToken cancellationToken)
    {
        bool canaryPassed = await RunStartupSelfTestAsync(cancellationToken).ConfigureAwait(false);
        if (!canaryPassed && (startupCanaryBrakeObserved || IsCanaryIdentityBraked()))
        {
            IsStartupGateOpen = false;
            QueueStartupCanaryRetry(registry);
        }

        return canaryPassed;
    }

    /// <summary>
    /// Reconstructs candidate workload identities from live container io.kautuka.* labels before
    /// the Envoy listener starts accepting connections. Labels are not post-start verification:
    /// rebuilt entries stay deny-all until their current desired owner re-verifies them. Kills and
    /// removes any unlabelled containers.
    /// </summary>
    public async Task RebuildRegistryFromLiveContainersAsync(
        IWorkloadRegistry registry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);

        logger.LogInformation("Rebuilding workload registry from live container labels...");
        PodmanCommandResult psResult = await podmanRunner
            .RunAsync(["ps", "-a", "--format", "json"], 30, cancellationToken)
            .ConfigureAwait(false);

        if (psResult.ExitCode != 0)
        {
            logger.LogError("Failed to list Podman containers for registry rebuild: {StdErr}", psResult.StdErr);
            throw new InvalidOperationException($"podman ps failed during registry rebuild: {psResult.StdErr}");
        }

        var rebuiltEntries = new List<RegistryEntry>();
        RegistryEntry? previouslyVerifiedPackageProxy = registry.FindByIp(PackageProxyIp);
        if (!string.IsNullOrWhiteSpace(psResult.StdOut))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(psResult.StdOut);
                JsonElement root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement container in root.EnumerateArray())
                    {
                        try
                        {
                            string cid = container.TryGetProperty("Id", out JsonElement idElem) ? idElem.GetString() ?? string.Empty : string.Empty;
                            if (string.IsNullOrWhiteSpace(cid))
                            {
                                continue;
                            }

                            // The appliance supervisor re-verifies these reserved profiles independently.
                            if (knownInfraContainerIds.Contains(cid) || IsAuthorizedApplianceService(cid))
                            {
                                continue;
                            }

                            // Inspect to extract exact network IP and labels
                            PodmanCommandResult inspectResult = await podmanRunner
                                .RunAsync(["inspect", cid], 10, cancellationToken)
                                .ConfigureAwait(false);

                            if (inspectResult.ExitCode != 0)
                            {
                                continue;
                            }

                            using JsonDocument inspectDoc = JsonDocument.Parse(inspectResult.StdOut);
                            JsonElement inspected = inspectDoc.RootElement.ValueKind == JsonValueKind.Array
                                ? inspectDoc.RootElement[0]
                                : inspectDoc.RootElement;

                            JsonElement config = inspected.GetProperty("Config");
                            JsonElement labels = config.TryGetProperty("Labels", out JsonElement lElem) ? lElem : default;

                            string user = GetStringProperty(labels, "io.kautuka.user");
                            string agent = GetStringProperty(labels, "io.kautuka.agent");
                            string connector = GetStringProperty(labels, "io.kautuka.connector");
                            string execContext = GetStringProperty(labels, "io.kautuka.exec-context");
                            string kind = GetStringProperty(labels, "io.kautuka.kind");
                            string egress = GetStringProperty(labels, "io.kautuka.egress");
                            string compat = GetStringProperty(labels, "io.kautuka.compat");
                            string purpose = GetStringProperty(labels, "io.kautuka.purpose");

                            // The package proxy is a reserved service identity. Never rebuild it
                            // from self-asserted labels; the sidecar supervisor registers it only
                            // after checking Manager-owned image provenance, annotations and IP.
                            if (string.Equals(agent, "package-proxy", StringComparison.Ordinal))
                            {
                                if (previouslyVerifiedPackageProxy is not null
                                    && string.Equals(previouslyVerifiedPackageProxy.ContainerId, cid, StringComparison.OrdinalIgnoreCase)
                                    && IsPreviouslyAuthorizedPackageProxy(inspected, labels, cid))
                                {
                                    try
                                    {
                                        await postStartVerifier.VerifyWorkloadRulesetAsync(
                                            cid, PackageProxyIp, isPackageProxy: true, cancellationToken: cancellationToken).ConfigureAwait(false);
                                        rebuiltEntries.Add(previouslyVerifiedPackageProxy);
                                    }
                                    catch (Exception verificationException) when (verificationException is not OperationCanceledException)
                                    {
                                        packageProxyStartupReady = false;
                                        logger.LogWarning(verificationException, "Previously verified package proxy {ContainerId} no longer has the required workload rules.", cid);
                                    }
                                }
                                else packageProxyStartupReady = false;

                                continue;
                            }

                            // Find IP on kautuka-workload
                            string ip = string.Empty;
                            if (inspected.TryGetProperty("NetworkSettings", out JsonElement netSettings)
                                && netSettings.TryGetProperty("Networks", out JsonElement networks)
                                && networks.TryGetProperty(WorkloadNetworkName, out JsonElement wlNet)
                                && wlNet.TryGetProperty("IPAddress", out JsonElement ipElem))
                            {
                                ip = ipElem.GetString() ?? string.Empty;
                            }

                            // Skip sidecars (openobserve, etc.) if not on workload network
                            if (string.IsNullOrWhiteSpace(ip))
                            {
                                continue;
                            }

                            // Validation of required labels:
                            bool isValid = !string.IsNullOrWhiteSpace(kind)
                                && !string.IsNullOrWhiteSpace(execContext);

                            if (isValid && kind is "connector" or "agent-sandbox")
                            {
                                if (string.IsNullOrWhiteSpace(user)
                                    && egress is not ("deny-all" or "proxy-only"))
                                {
                                    isValid = false;
                                }
                            }

                            if (!isValid)
                            {
                                // Kill and remove unlabelled or invalid workload (Section 4.3.4 item 6)
                                Interlocked.Increment(ref unlabelledWorkloadCount);
                                logger.LogWarning("Killing unlabelled/invalid workload container '{ContainerId}'.", cid);
                                try
                                {
                                    await podmanRunner.RunAsync(["rm", "-f", "--time", "1", cid], 5, cancellationToken).ConfigureAwait(false);
                                }
                                catch
                                {
                                    // Ignore cleanup failure
                                }
                                continue;
                            }

                            var entry = new RegistryEntry(
                                ip,
                                string.IsNullOrWhiteSpace(user) ? null : user,
                                string.IsNullOrWhiteSpace(agent) ? null : agent,
                                cid,
                                kind,
                                string.IsNullOrWhiteSpace(connector) ? null : connector,
                                execContext,
                                // Labels identify a candidate workload, but they are not proof that
                                // its post-start security verification completed in this process.
                                // Rebuilt entries stay quarantined until an authoritative desired
                                // workload is matched and re-verified by its owner.
                                "deny-all",
                                "none",
                                string.IsNullOrWhiteSpace(compat) ? null : compat,
                                DateTimeOffset.UtcNow,
                                string.IsNullOrWhiteSpace(purpose) ? null : purpose);
                            string inspectedContainerId = GetStringProperty(inspected, "Id");
                            bool alreadyQuarantined = desiredStateStore?.IsWorkloadQuarantined(cid) == true
                                || (!string.IsNullOrWhiteSpace(inspectedContainerId) && desiredStateStore?.IsWorkloadQuarantined(inspectedContainerId) == true);
                            if (alreadyQuarantined)
                            {
                                if (WorkloadPostStartVerifier.IsCanonicalContainerId(inspectedContainerId))
                                {
                                    try
                                    {
                                        await podmanRunner.RunAsync(["rm", "-f", "--time", "1", inspectedContainerId], 5, cancellationToken).ConfigureAwait(false);
                                    }
                                    catch (Exception cleanupException) when (cleanupException is not OperationCanceledException)
                                    {
                                        logger.LogWarning(cleanupException, "Could not retry removal of quarantined workload {ContainerId} during restart.", inspectedContainerId);
                                    }
                                }
                                continue;
                            }
                            // Keep restored workloads outside the live registry until they pass verification.
                            var candidateRegistry = new WorkloadRegistry();
                            candidateRegistry.Register(entry);
                            try
                            {
                                ImageCompatProfile? compatProfile = await ResolveInspectedImageCompatibilityProfileAsync(inspected, cancellationToken).ConfigureAwait(false);
                                await postStartVerifier.VerifyWorkloadAsync(
                                    cid, ip, compatProfile, candidateRegistry,
                                    isPackageProxy: string.Equals(entry.AgentId, "package-proxy", StringComparison.Ordinal),
                                    cancellationToken: cancellationToken).ConfigureAwait(false);
                            }
                            catch (Exception verificationException) when (verificationException is not OperationCanceledException)
                            {
                                string immutableId = GetStringProperty(inspected, "Id");
                                logger.LogWarning(verificationException, "Removing restored workload {ContainerId} because verification or profile resolution failed.", cid);
                                if (WorkloadPostStartVerifier.IsCanonicalContainerId(immutableId))
                                {
                                    Exception? persistenceFailure = null;
                                    Exception? cleanupFailure = null;
                                    try { desiredStateStore?.QuarantineWorkload(immutableId); }
                                    catch (Exception persistenceException) when (persistenceException is not OperationCanceledException)
                                    {
                                        persistenceFailure = persistenceException;
                                        logger.LogError(persistenceException, "Could not persist quarantine for restored workload {ContainerId}.", immutableId);
                                    }
                                    try
                                    {
                                        PodmanCommandResult removed = await podmanRunner.RunAsync(["rm", "-f", "--time", "1", immutableId], 5, cancellationToken).ConfigureAwait(false);
                                        if (removed.ExitCode != 0) cleanupFailure = new InvalidOperationException($"podman rm failed for restored workload '{immutableId}' (rc {removed.ExitCode}): {removed.StdErr}");
                                    }
                                    catch (Exception cleanupException) when (cleanupException is not OperationCanceledException)
                                    {
                                        cleanupFailure = cleanupException;
                                        logger.LogError(cleanupException, "Could not remove failed restored workload {ContainerId}.", immutableId);
                                    }
                                    if (persistenceFailure is not null && cleanupFailure is not null)
                                    {
                                        CloseLaunchAdmission();
                                        throw new StartupWorkloadContainmentException($"Failed to quarantine and remove restored workload '{immutableId}'. Startup must remain closed.", new AggregateException(persistenceFailure, cleanupFailure));
                                    }
                                }
                                else
                                {
                                    CloseLaunchAdmission();
                                    throw new StartupWorkloadContainmentException($"Could not establish immutable identity for restored workload '{cid}'. Startup must remain closed.", verificationException);
                                }
                                continue;
                            }

                            rebuiltEntries.Add(entry);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException and not StartupWorkloadContainmentException)
                        {
                            logger.LogWarning(ex, "Skipping malformed container entry during workload registry rebuild.");
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not StartupWorkloadContainmentException)
            {
                logger.LogError(ex, "Error parsing containers during registry rebuild.");
            }
        }

        registry.Rebuild(rebuiltEntries);
        logger.LogInformation("Workload registry rebuilt with {Count} entries.", registry.Count);
    }

    public Task RebuildRegistryFromLiveContainersAsync(CancellationToken cancellationToken = default)
        => RebuildRegistryFromLiveContainersAsync(registry, cancellationToken);

    /// <summary>
    /// Executes the startup canary test (Section 4.3.4 item 5).
    /// Starts a canary workload, verifies the hook log entry and live ruleset, and removes the canary.
    /// </summary>
    private async Task<ImageCompatProfile?> ResolveInspectedImageCompatibilityProfileAsync(JsonElement inspectedContainer, CancellationToken cancellationToken)
    {
        if (imageCompatibilityStore is null) return null;
        string imageId = GetStringProperty(inspectedContainer, "Image");
        if (string.IsNullOrWhiteSpace(imageId)) return null;

        PodmanCommandResult imageInspect = await podmanRunner
            .RunAsync(["image", "inspect", imageId], 15, cancellationToken)
            .ConfigureAwait(false);
        if (imageInspect.ExitCode != 0)
        {
            throw new InvalidOperationException($"podman image inspect failed while resolving compatibility for '{imageId}' (rc {imageInspect.ExitCode}).");
        }

        using JsonDocument imageDocument = JsonDocument.Parse(imageInspect.StdOut);
        JsonElement image = imageDocument.RootElement.ValueKind == JsonValueKind.Array
            ? imageDocument.RootElement[0]
            : imageDocument.RootElement;
        if (image.TryGetProperty("RepoDigests", out JsonElement repoDigests)
            && repoDigests.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement repoDigestElement in repoDigests.EnumerateArray())
            {
                if (repoDigestElement.ValueKind != JsonValueKind.String) continue;
                string repoDigest = repoDigestElement.GetString() ?? string.Empty;
                int separator = repoDigest.LastIndexOf('@');
                if (separator < 0 || separator + 1 >= repoDigest.Length) continue;
                string digest = repoDigest[(separator + 1)..];
                if (digest.StartsWith("sha256:", StringComparison.Ordinal))
                {
                    ImageCompatProfile? profile = imageCompatibilityStore.GetProfileByDigest(digest);
                    if (profile is not null) return profile;
                }
            }
        }

        // Locally built images may not have repository digests. Podman's inspected immutable
        // image digest is trusted metadata too; never derive compatibility from container labels.
        if (image.TryGetProperty("Digest", out JsonElement digestElement)
            && digestElement.ValueKind == JsonValueKind.String)
        {
            string digest = digestElement.GetString() ?? string.Empty;
            if (digest.StartsWith("sha256:", StringComparison.Ordinal))
            {
                ImageCompatProfile? profile = imageCompatibilityStore.GetProfileByDigest(digest);
                if (profile is not null) return profile;
            }
        }

        // Locally built images may expose no manifest digest. Their immutable Podman ID
        // is still a trusted SHA-256 identity; labels from the restored container are not.
        if (image.TryGetProperty("Id", out JsonElement imageIdElement)
            && imageIdElement.ValueKind == JsonValueKind.String)
        {
            string immutableImageId = imageIdElement.GetString() ?? string.Empty;
            if (immutableImageId.StartsWith("sha256:", StringComparison.Ordinal))
            {
                return imageCompatibilityStore.GetProfileByDigest(immutableImageId);
            }
        }

        return null;
    }
    public async Task<bool> RunStartupSelfTestAsync(CancellationToken cancellationToken = default)
    {
        startupCanaryBrakeObserved = IsCanaryIdentityBraked();
        string canaryName = $"kautuka-canary-{Guid.NewGuid():N}"[..24];
        string canaryIp = addressAllocator.AllocateNextIp(registry);

        logger.LogInformation("Running startup canary self-test '{CanaryName}' at IP {CanaryIp}...", canaryName, canaryIp);
        try
        {
            var spec = new WorkloadContainerSpec(canaryName, CanaryBasePinnedImage, canaryIp)
            {
                UserId = "system",
                AgentId = "system",
                ExecContext = "system",
                Kind = "system",
                Egress = "deny-all",
                Persistent = true,
                StartupGateOpen = true,
                IsBraked = startupCanaryBrakeObserved,
            };

            IReadOnlyList<string> runArgs = spec.Render();
            PodmanCommandResult runResult = await podmanRunner
                .RunAsync(runArgs, 30, cancellationToken)
                .ConfigureAwait(false);

            if (runResult.ExitCode != 0)
            {
                logger.LogError("Canary start failed (rc {ExitCode}): {StdErr}", runResult.ExitCode, runResult.StdErr);
                return false;
            }

            string canaryId = runResult.StdOut.Trim();
            if (canaryId.Length != 64 || !canaryId.All(char.IsAsciiHexDigit))
            {
                logger.LogError("Canary start did not return a full container ID.");
                return false;
            }

            // Verify live ruleset via verifier
            WorkloadVerificationResult verification = await postStartVerifier
                .VerifyWorkloadAsync(canaryName, canaryIp, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!verification.Passed)
            {
                return false;
            }

            // The canary is only proof of the applied boundary if the hook recorded a successful
            // ruleset application for this exact workload. A missing log must keep startup closed.
            string logContent;
            if (!string.IsNullOrWhiteSpace(windowsPodmanMachineName)
                && !string.IsNullOrWhiteSpace(windowsPodmanHookStateDirectory))
            {
                PodmanCommandResult hookLog = await podmanRunner.RunAsync(
                    ["machine", "ssh", windowsPodmanMachineName, $"cat '{windowsPodmanHookStateDirectory}/hook.log'"],
                    10,
                    cancellationToken).ConfigureAwait(false);
                logContent = hookLog.ExitCode == 0 ? hookLog.StdOut : string.Empty;
            }
            else
            {
                logContent = File.Exists(hookLogFilePath)
                    ? await File.ReadAllTextAsync(hookLogFilePath, cancellationToken).ConfigureAwait(false)
                    : string.Empty;
            }
            if (!HasSuccessfulCanaryHookRecord(logContent, canaryIp, canaryId))
            {
                logger.LogError("Canary hook did not record an applied workload ruleset for {CanaryIp}.", canaryIp);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Startup canary self-test encountered an error.");
            return false;
        }
        finally
        {
            try
            {
                await podmanRunner.RunAsync(["rm", "-f", canaryName], 10, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Ignore cleanup error
            }
        }
    }

    private bool IsCanaryIdentityBraked()
    {
        BrakeState? brake = policyStore?.GetBrakeSnapshot();
        return brake is not null && (brake.System || brake.IsUserBraked("system"));
    }

    private void QueueStartupCanaryRetry(IWorkloadRegistry registry)
    {
        startupCanaryRetryPending = true;
        logger.LogWarning("Startup canary is paused by the persisted brake; the startup gate remains closed and reconcile will retry after brake release.");
        StartReconcileLoop(registry);
    }

    internal static bool HasSuccessfulCanaryHookRecord(string? hookLog, string canaryIp, string canaryId)
    {
        if (string.IsNullOrWhiteSpace(hookLog) || string.IsNullOrWhiteSpace(canaryIp)
            || canaryId is null || canaryId.Length != 64 || !canaryId.All(char.IsAsciiHexDigit))
        {
            return false;
        }

        string expectedRulesHash = WorkloadRulesetRenderer.ComputeExpectedCanonicalRulesetHash(canaryIp);

        foreach (string line in hookLog.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement record = doc.RootElement;
                if (!record.TryGetProperty("id", out JsonElement id)
                    || id.ValueKind != JsonValueKind.String
                    || !string.Equals(id.GetString(), canaryId, StringComparison.Ordinal)
                    || !record.TryGetProperty("action", out JsonElement action)
                    || !string.Equals(action.GetString(), "allow", StringComparison.Ordinal)
                    || !record.TryGetProperty("rules_sha256", out JsonElement rulesHash)
                    || !string.Equals(rulesHash.GetString(), expectedRulesHash, StringComparison.Ordinal)
                    || !record.TryGetProperty("ips", out JsonElement ips)
                    || ips.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                if (ips.EnumerateArray().Any(ip => string.Equals(ip.GetString(), canaryIp, StringComparison.Ordinal)))
                {
                    return true;
                }
            }
            catch (JsonException)
            {
                // Ignore unrelated malformed JSONL records; a valid canary record is still required.
            }
        }

        return false;
    }

    /// <summary>
    /// Stops affected running workload containers and deregisters them when the emergency brake engages (Section 4.3.4 item 10).
    /// </summary>
    public async Task EngageBrakeAsync(
        string? userId,
        IWorkloadRegistry registry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        IReadOnlyList<RegistryEntry> all = registry.GetAll();

        IEnumerable<RegistryEntry> affected = string.IsNullOrWhiteSpace(userId)
            ? all // System brake: stop all workloads
            : all.Where(e => string.Equals(e.UserId, userId, StringComparison.Ordinal));

        foreach (RegistryEntry entry in affected)
        {
            logger.LogWarning("Emergency brake stopping container '{ContainerId}' (IP: {Ip}, User: {User})...", entry.ContainerId, entry.Ip, entry.UserId);
            bool stopped = false;
            try
            {
                PodmanCommandResult stopResult = await podmanRunner.RunAsync(["stop", "-t", "2", entry.ContainerId], 10, cancellationToken).ConfigureAwait(false);
                stopped = stopResult.ExitCode == 0;
            }
            catch
            {
                stopped = false;
            }

            if (!stopped)
            {
                bool killed = false;
                try
                {
                    PodmanCommandResult killResult = await podmanRunner
                        .RunAsync(["kill", entry.ContainerId], 10, cancellationToken)
                        .ConfigureAwait(false);
                    killed = killResult.ExitCode == 0;
                }
                catch
                {
                    // Best-effort kill fallback
                }

                if (!killed)
                {
                    logger.LogError("Emergency brake could not stop or kill container '{ContainerId}' (IP: {Ip}); retaining its registry identity for fail-closed authorization.", entry.ContainerId, entry.Ip);
                    continue;
                }
            }

            registry.Deregister(entry.Ip, entry.ContainerId);
        }
    }

    private async Task EnsureNetworksAsync(CancellationToken cancellationToken)
    {
        // 1. kautuka-workload
        PodmanCommandResult inspectWl = await podmanRunner
            .RunAsync(["network", "inspect", WorkloadNetworkName], 10, cancellationToken)
            .ConfigureAwait(false);

        if (inspectWl.ExitCode == 0)
        {
            if (OperatingSystem.IsLinux() && !string.IsNullOrWhiteSpace(inspectWl.StdOut))
            {
                using JsonDocument netDoc = JsonDocument.Parse(inspectWl.StdOut);
                JsonElement netElem = netDoc.RootElement.ValueKind == JsonValueKind.Array && netDoc.RootElement.GetArrayLength() > 0
                    ? netDoc.RootElement[0]
                    : netDoc.RootElement;
                bool isInternal = netElem.TryGetProperty("internal", out JsonElement intElem) && intElem.GetBoolean();
                bool dnsEnabled = netElem.TryGetProperty("dns_enabled", out JsonElement dnsElem) && dnsElem.GetBoolean();
                string routeMetric = ReadNetworkRouteMetric(netElem);
                if (!isInternal || dnsEnabled || !string.Equals(routeMetric, WorkloadNetworkRouteMetric, StringComparison.Ordinal))
                {
                    IsStartupGateOpen = false;
                    throw new InvalidOperationException($"Existing network '{WorkloadNetworkName}' drifted (internal={isInternal}, dns_enabled={dnsEnabled}, metric={routeMetric}).");
                }
            }
        }
        else
        {
            logger.LogInformation("Creating {NetworkName}...", WorkloadNetworkName);
            List<string> createArgs =
            [
                "network", "create",
                "--internal",
                "--disable-dns",
                "--opt", $"metric={WorkloadNetworkRouteMetric}",
                "--subnet", WorkloadSubnet,
                "--gateway", "10.247.0.1",
                "--route", $"0.0.0.0/0,10.247.0.2,{WorkloadNetworkRouteMetric}",
                WorkloadNetworkName
            ];

            PodmanCommandResult createResult = await podmanRunner
                .RunAsync(createArgs, 30, cancellationToken)
                .ConfigureAwait(false);

            if (createResult.ExitCode != 0)
            {
                throw new InvalidOperationException($"Failed to create network {WorkloadNetworkName}: {createResult.StdErr}");
            }
        }

        // 2. kautuka-outside
        PodmanCommandResult inspectOut = await podmanRunner
            .RunAsync(["network", "inspect", OutsideNetworkName], 10, cancellationToken)
            .ConfigureAwait(false);

        if (inspectOut.ExitCode == 0)
        {
            if (OperatingSystem.IsLinux() && !string.IsNullOrWhiteSpace(inspectOut.StdOut))
            {
                using JsonDocument netDoc = JsonDocument.Parse(inspectOut.StdOut);
                JsonElement netElem = netDoc.RootElement.ValueKind == JsonValueKind.Array && netDoc.RootElement.GetArrayLength() > 0
                    ? netDoc.RootElement[0]
                    : netDoc.RootElement;
                string routeMetric = ReadNetworkRouteMetric(netElem);
                if (!string.Equals(routeMetric, OutsideNetworkRouteMetric, StringComparison.Ordinal))
                {
                    IsStartupGateOpen = false;
                    throw new InvalidOperationException($"Existing network '{OutsideNetworkName}' drifted (metric={routeMetric}).");
                }
            }
        }
        else
        {
            logger.LogInformation("Creating {NetworkName}...", OutsideNetworkName);
            PodmanCommandResult createOut = await podmanRunner
                .RunAsync(["network", "create", "--opt", $"metric={OutsideNetworkRouteMetric}", "--subnet", OutsideSubnet, "--gateway", OutsideGatewayIp, OutsideNetworkName], 30, cancellationToken)
                .ConfigureAwait(false);

            if (createOut.ExitCode != 0)
            {
                throw new InvalidOperationException($"Failed to create network {OutsideNetworkName}: {createOut.StdErr}");
            }
        }
    }

    private static string ReadNetworkRouteMetric(JsonElement network)
    {
        if (!network.TryGetProperty("options", out JsonElement options)
            || options.ValueKind != JsonValueKind.Object
            || !options.TryGetProperty("metric", out JsonElement metric))
        {
            return string.Empty;
        }

        return metric.ValueKind == JsonValueKind.String
            ? metric.GetString() ?? string.Empty
            : metric.ToString();
    }

    public void WriteInfraAllowlist(IReadOnlyList<InfraAllowlistEntry>? entries = null)
    {
        lock (infraAllowlistLock)
        {
            entries ??=
            [
                new(EgressPodName, "egress-pod", [EgressPodIp, OutsidePodIp], "pinned"),
                new("kautuka-egress-envoy", "egress-envoy", [EgressPodIp, OutsidePodIp, OutsideLanSourceIp], EnvoyPinnedImage),
                new("kautuka-egress-dns", "egress-dns", [EgressPodIp, OutsidePodIp, OutsideLanSourceIp], CoreDnsPinnedImage),
            ];
            baseInfraAllowlistEntries = entries.Where(entry => entry.Role is not ("pkgs-proxy" or "appliance-gateway" or "appliance-webhost")).ToArray();
            PersistInfraAllowlist();
        }
    }

    private void PersistInfraAllowlist()
    {
        List<InfraAllowlistEntry> entries = [.. baseInfraAllowlistEntries];
        if (packageProxyAllowlistEntry is not null) entries.Add(packageProxyAllowlistEntry);
        entries.AddRange(applianceAllowlistEntries.Values);
        WriteInfraAllowlistDocument(new InfraAllowlistDocument(1, entries));
    }

    private bool IsAuthorizedApplianceService(string containerId)
    {
        lock (infraAllowlistLock) return applianceAllowlistEntries.Values.Any(entry => entry.Id == containerId);
    }

    private static string GetApplianceAddress(string role) => role switch
    {
        "appliance-gateway" => WorkloadRulesetRenderer.ApplianceGatewayIp,
        "appliance-webhost" => WorkloadRulesetRenderer.ApplianceWebHostIp,
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    public void AuthorizeApplianceService(string role, string containerId, string imageId)
    {
        if (gatewayConnectEnvoyDirectory is null || !WorkloadPostStartVerifier.IsCanonicalContainerId(containerId)
            || string.IsNullOrEmpty(imageId) || NormalizeContainerImageId(imageId) != imageId)
            throw new InvalidOperationException("Invalid appliance service identity for the workload network.");

        string address = GetApplianceAddress(role);
        lock (infraAllowlistLock)
        {
            applianceAllowlistEntries.TryGetValue(role, out InfraAllowlistEntry? previous);
            if (previous is not null && previous.Id != containerId)
                throw new InvalidOperationException("A different appliance container holds the reserved address.");
            if (previous?.ImageDigest == imageId) return;
            SetApplianceAllowlistEntry(role, new(containerId, role, [address], imageId), previous);
        }
    }

    public void RevokeApplianceService(string role, string containerId)
    {
        lock (infraAllowlistLock)
        {
            if (!applianceAllowlistEntries.TryGetValue(role, out InfraAllowlistEntry? previous) || previous.Id != containerId) return;
            SetApplianceAllowlistEntry(role, null, previous);
        }
    }

    private void SetApplianceAllowlistEntry(string role, InfraAllowlistEntry? value, InfraAllowlistEntry? previous)
    {
        if (value is null) applianceAllowlistEntries.Remove(role);
        else applianceAllowlistEntries[role] = value;
        try { PersistInfraAllowlist(); }
        catch
        {
            if (previous is null) applianceAllowlistEntries.Remove(role);
            else applianceAllowlistEntries[role] = previous;
            throw;
        }
    }

    public Task<string> VerifyApplianceServiceAsync(string role, string containerId, JsonElement? inspection, CancellationToken cancellationToken)
    {
        string address = GetApplianceAddress(role);
        bool gateway = role == "appliance-gateway";
        return inspection is { } container
            ? postStartVerifier.VerifyInspectedWorkloadRulesetAsync(container, containerId, address,
                isPackageProxy: false, isApplianceGateway: gateway, isApplianceWebHost: !gateway, cancellationToken: cancellationToken)
            : postStartVerifier.VerifyWorkloadRulesetAsync(containerId, address,
                isApplianceGateway: gateway, isApplianceWebHost: !gateway, cancellationToken: cancellationToken);
    }

    private bool IsPreviouslyAuthorizedPackageProxy(JsonElement inspected, JsonElement labels, string containerId)
    {
        InfraAllowlistEntry? authorized;
        lock (infraAllowlistLock)
        {
            authorized = packageProxyAllowlistEntry;
        }

        if (authorized is null
            || !string.Equals(authorized.Id, containerId, StringComparison.OrdinalIgnoreCase)
            || authorized.Role != "pkgs-proxy"
            || !authorized.ExpectedIpv4.Contains(PackageProxyIp, StringComparer.Ordinal))
        {
            return false;
        }

        JsonElement config = inspected.GetProperty("Config");
        JsonElement annotations = config.TryGetProperty("Annotations", out JsonElement annotationElement) ? annotationElement : default;
        string inspectedImage = NormalizeContainerImageId(GetStringProperty(inspected, "Image"));
        string imageAnnotation = GetStringProperty(annotations, "io.kautuka.image-id");
        string proxyRole = GetStringProperty(annotations, "io.kautuka.role");
        string networkIp = string.Empty;
        if (inspected.TryGetProperty("NetworkSettings", out JsonElement networkSettings)
            && networkSettings.TryGetProperty("Networks", out JsonElement networks)
            && networks.TryGetProperty(WorkloadNetworkName, out JsonElement workloadNetwork))
        {
            networkIp = GetStringProperty(workloadNetwork, "IPAddress");
        }

        bool running = inspected.TryGetProperty("State", out JsonElement state)
            && state.TryGetProperty("Running", out JsonElement runningValue)
            && runningValue.ValueKind == JsonValueKind.True;
        bool healthy = running && state.TryGetProperty("Health", out JsonElement health)
            && string.Equals(GetStringProperty(health, "Status"), "healthy", StringComparison.OrdinalIgnoreCase);
        return running
            && healthy
            && string.Equals(networkIp, PackageProxyIp, StringComparison.Ordinal)
            && string.Equals(inspectedImage, authorized.ImageDigest, StringComparison.Ordinal)
            && string.Equals(imageAnnotation, authorized.ImageDigest, StringComparison.Ordinal)
            && string.Equals(proxyRole, "package-proxy", StringComparison.Ordinal)
            && string.Equals(GetStringProperty(labels, "io.kautuka.agent"), "package-proxy", StringComparison.Ordinal)
            && string.Equals(GetStringProperty(labels, "io.kautuka.user"), "system", StringComparison.Ordinal)
            && string.Equals(GetStringProperty(labels, "io.kautuka.kind"), "agent-sandbox", StringComparison.Ordinal);
    }

    private static string NormalizeContainerImageId(string imageId)
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

    private async Task<bool> IsPreviouslyAuthorizedPackageProxyAsync(string containerId, CancellationToken cancellationToken)
    {
        PodmanCommandResult result = await podmanRunner.RunAsync(["inspect", containerId], 10, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) return false;

        using JsonDocument document = JsonDocument.Parse(result.StdOut);
        JsonElement inspected = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement[0] : document.RootElement;
        JsonElement config = inspected.GetProperty("Config");
        JsonElement labels = config.TryGetProperty("Labels", out JsonElement labelElement) ? labelElement : default;
        return IsPreviouslyAuthorizedPackageProxy(inspected, labels, containerId);
    }

    public async Task AuthorizePackageProxyContainerAsync(
        string containerId,
        string imageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageId);
        if (!imageId.StartsWith("sha256:", StringComparison.Ordinal)
            || imageId.Length != 71)
        {
            throw new ArgumentException("Package proxy image ID must be a full sha256 image ID.", nameof(imageId));
        }

        lock (infraAllowlistLock)
        {
            packageProxyAllowlistEntry = new InfraAllowlistEntry(containerId, "pkgs-proxy", [PackageProxyIp], imageId);
            PersistInfraAllowlist();
        }

        await SyncWindowsPodmanInfraAllowlistAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RevokePackageProxyContainerAsync(
        string containerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        lock (infraAllowlistLock)
        {
            if (packageProxyAllowlistEntry is not null
                && string.Equals(packageProxyAllowlistEntry.Id, containerId, StringComparison.OrdinalIgnoreCase))
            {
                packageProxyAllowlistEntry = null;
                PersistInfraAllowlist();
                packageProxyStartupReady = false;
            }
        }

        if (registry.TryGetByIp(PackageProxyIp, out RegistryEntry? registered)
            && registered is not null
            && string.Equals(registered.ContainerId, containerId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(registered.AgentId, "package-proxy", StringComparison.Ordinal))
        {
            packageProxyStartupReady = false;
            registry.Deregister(PackageProxyIp, containerId);
        }

        // Always sync, including retries after the host-side entry has already
        // been removed. A previous machine copy may have failed while the local
        // state was successfully updated.
        await SyncWindowsPodmanInfraAllowlistAsync(cancellationToken).ConfigureAwait(false);
    }

    public void RegisterVerifiedPackageProxyContainer(string containerId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(containerId);
        if (registry.TryGetByIp(PackageProxyIp, out RegistryEntry? current)
            && current is not null
            && !string.Equals(current.AgentId, "package-proxy", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The reserved package proxy address is already owned by another workload.");
        }

        registry.Register(new RegistryEntry(
            PackageProxyIp, "system", "package-proxy", containerId, "agent-sandbox", null,
            "agent-sandbox", "normal", "user-agent", null, DateTimeOffset.UtcNow,
            Purpose: "package-proxy"));
    }

    public bool IsPackageProxyRegistered
        => registry.TryGetByIp(PackageProxyIp, out RegistryEntry? entry)
            && entry is not null
            && string.Equals(entry.Kind, "agent-sandbox", StringComparison.Ordinal)
            && string.Equals(entry.UserId, "system", StringComparison.Ordinal)
            && string.Equals(entry.AgentId, "package-proxy", StringComparison.Ordinal)
            && string.Equals(entry.Egress, "normal", StringComparison.Ordinal)
            && string.Equals(entry.Attribution, "user-agent", StringComparison.Ordinal);

    public void SetPackageProxyReady(bool ready)
        => packageProxyStartupReady = ready && IsPackageProxyRegistered;

    public bool IsPackageProxyReady => packageProxyStartupReady && IsPackageProxyRegistered;

    public async Task<bool> VerifyPackageProxyRulesetAsync(string containerId, CancellationToken cancellationToken = default)
    {
        try
        {
            await postStartVerifier.VerifyWorkloadRulesetAsync(
                containerId, PackageProxyIp, isPackageProxy: true, cancellationToken: cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Package proxy {ContainerId} failed special workload ruleset verification.", containerId);
            return false;
        }
    }

    private void WriteInfraAllowlistDocument(InfraAllowlistDocument doc)
    {
        string directory = Path.GetDirectoryName(allowlistFilePath)!;
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        string tempPath = Path.Combine(directory, ".infra-allowlist-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                if (OperatingSystem.IsLinux())
                {
                    File.SetUnixFileMode(tempPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                JsonSerializer.Serialize(stream, doc, InfraAllowlistJsonContext.Default.InfraAllowlistDocument);
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, allowlistFilePath, overwrite: true);
            if (OperatingSystem.IsLinux())
            {
                File.SetUnixFileMode(allowlistFilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private async Task EnsureEgressPodAsync(
        bool isWindowsHostNativeDev,
        string? decisionEndpointIp,
        bool includeLanGrants,
        string? envoyCertificateDirectory,
        CancellationToken cancellationToken)
    {
        // Render boundary and configs
        bool includeGatewayConnect = gatewayConnectEnvoyDirectory is not null;
        string boundaryNft = BoundaryRulesetRenderer.RenderBoundaryNft(includeLanGrants, decisionEndpointIp, [OutsideGatewayIp], includeGatewayConnect);
        string envoyConfig = BoundaryRulesetRenderer.RenderEnvoyConfig(
            adminSocketPath: isWindowsHostNativeDev ? "/tmp/kautuka-admin.sock" : "/run/kautuka-admin/admin.sock",
            isWindowsMtls: isWindowsHostNativeDev,
            includeGatewayConnect: includeGatewayConnect);
        string corefile = BoundaryRulesetRenderer.RenderCorefile(EgressPodIp, [OutsideGatewayIp]);

        string nacDir = Path.Combine(stateDirectory, "nac");
        string boundaryConfigDir = Path.Combine(nacDir, "boundary");
        string boundaryPath = Path.Combine(boundaryConfigDir, "boundary.nft");
        string envoyConfigDir = Path.Combine(nacDir, "envoy");
        string dnsConfigDir = Path.Combine(nacDir, "dns");
        string envoyPath = Path.Combine(envoyConfigDir, "envoy.yaml");
        string corefilePath = Path.Combine(dnsConfigDir, "Corefile");

        expectedBoundaryNftSourceHash = ComputeReconcileConfigurationHash(boundaryNft, envoyConfig, corefile);
        await EnsureExistingEgressPodUsesRenderedConfigAsync(
            boundaryNft, envoyConfig, corefile, isWindowsHostNativeDev, cancellationToken).ConfigureAwait(false);

        await WriteConfigAtomicallyAsync(boundaryPath, boundaryNft, cancellationToken).ConfigureAwait(false);
        await WriteConfigAtomicallyAsync(envoyPath, envoyConfig, cancellationToken).ConfigureAwait(false);
        await WriteConfigAtomicallyAsync(corefilePath, corefile, cancellationToken).ConfigureAwait(false);

        // Windows Podman needs the host decision endpoint configured at pod creation;
        // container-level --add-host is rejected for containers that join a pod.
        // Recreate only this dedicated infrastructure pod so the setting and rotated
        // development certificates stay in sync on every Manager start.
        if (isWindowsHostNativeDev)
        {
            await RemoveEgressPodIfPresentAsync(cancellationToken).ConfigureAwait(false);
        }

        // Check if pod exists:
        PodmanCommandResult inspectPod = await podmanRunner
            .RunAsync(["pod", "inspect", EgressPodName], 10, cancellationToken)
            .ConfigureAwait(false);

        if (inspectPod.ExitCode != 0)
        {
            logger.LogInformation("Creating egress pod {PodName}...", EgressPodName);
            List<string> createPodArgs = BuildEgressPodCreateArgs(isWindowsHostNativeDev, decisionEndpointIp);
            PodmanCommandResult podResult = await podmanRunner
                .RunAsync(createPodArgs, 30, cancellationToken)
                .ConfigureAwait(false);

            if (podResult.ExitCode != 0)
            {
                logger.LogError("Failed to create egress pod: {StdErr}", podResult.StdErr);
                throw new InvalidOperationException($"Failed to create egress pod: {podResult.StdErr}");
            }
        }
        else if (!HasRequiredEgressPodConfiguration(inspectPod.StdOut))
        {
            throw new InvalidOperationException($"Existing egress pod '{EgressPodName}' does not have the required workload/outside network addresses and forwarding sysctls.");
        }

        // Get Pod infra container ID and populate allowlist
        PodmanCommandResult podInspect = await podmanRunner
            .RunAsync(["pod", "inspect", EgressPodName], 10, cancellationToken)
            .ConfigureAwait(false);

        var allowlistEntries = new List<InfraAllowlistEntry>();
        string? infraContainerId = null;
        if (podInspect.ExitCode == 0 && !string.IsNullOrWhiteSpace(podInspect.StdOut))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(podInspect.StdOut);
                JsonElement podElem = doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0
                    ? doc.RootElement[0]
                    : doc.RootElement;
                string infraId = podElem.TryGetProperty("InfraContainerID", out JsonElement infElem) ? infElem.GetString() ?? string.Empty : string.Empty;
                if (!string.IsNullOrWhiteSpace(infraId))
                {
                    infraContainerId = infraId;
                    knownInfraContainerIds.Add(infraId);
                    allowlistEntries.Add(new(infraId, "egress-pod", [EgressPodIp, OutsidePodIp], "pinned"));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to parse InfraContainerID from pod inspect output.");
            }
        }

        // Write infra allowlist BEFORE starting containers
        WriteInfraAllowlist(allowlistEntries);

        await EnsureBoundaryImageAsync(cancellationToken).ConfigureAwait(false);

        // 1. Create all pod containers first so every container ID is allowlisted before podman pod start.
        await EnsureBoundaryContainerMountCurrentAsync(boundaryConfigDir, isWindowsHostNativeDev, cancellationToken).ConfigureAwait(false);
        await EnsureInfraContainerCreatedAsync(
            "kautuka-egress-boundary",
            "egress-boundary",
            BoundaryInitImage,
            BuildBoundaryCreateArgs(boundaryConfigDir, isWindowsHostNativeDev),
            allowlistEntries,
            cancellationToken).ConfigureAwait(false);

        // 2. CoreDNS container
        await EnsureConfigContainerMountCurrentAsync(
            "kautuka-egress-dns", Path.GetDirectoryName(corefilePath)!, "/etc/coredns", isWindowsHostNativeDev, cancellationToken).ConfigureAwait(false);
        await EnsureInfraContainerCreatedAsync(
            "kautuka-egress-dns",
            "egress-dns",
            CoreDnsPinnedImage,
            BuildCoreDnsCreateArgs(corefilePath, isWindowsHostNativeDev),
            allowlistEntries,
            cancellationToken).ConfigureAwait(false);

        // 3. Envoy container (runs natively as uid 65532 mapped via pod keep-id to host user)
        Directory.CreateDirectory(envoySocketHostDir);
        string adminSocketDir = Path.Combine(stateDirectory, "admin-socket");
        if (OperatingSystem.IsLinux())
        {
            const UnixFileMode adminSocketDirMode =
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupWrite | UnixFileMode.GroupExecute;
            Directory.CreateDirectory(adminSocketDir, adminSocketDirMode);
            File.SetUnixFileMode(adminSocketDir, adminSocketDirMode);
        }
        else
        {
            Directory.CreateDirectory(adminSocketDir);
        }
        if (isWindowsHostNativeDev)
        {
            if (string.IsNullOrWhiteSpace(decisionEndpointIp)
                || string.IsNullOrWhiteSpace(envoyCertificateDirectory))
            {
                throw new InvalidOperationException(
                    "Windows Podman NAC requires a resolved host gateway address and an Envoy mTLS certificate directory.");
            }

        }

        await EnsureConfigContainerMountCurrentAsync(
            "kautuka-egress-envoy", Path.GetDirectoryName(envoyPath)!, "/etc/kautuka-envoy", isWindowsHostNativeDev, cancellationToken).ConfigureAwait(false);
        string? podSelinuxLevel = null;
        if (useDevelopmentSelinuxDomain && !isWindowsHostNativeDev)
        {
            if (string.IsNullOrWhiteSpace(infraContainerId))
                throw new InvalidOperationException("Cannot determine the SELinux level of the egress pod.");
            PodmanCommandResult infraInspect = await podmanRunner.RunAsync(
                ["inspect", infraContainerId], 10, cancellationToken).ConfigureAwait(false);
            if (infraInspect.ExitCode != 0)
                throw new InvalidOperationException("Cannot inspect the SELinux level of the egress pod.");
            using JsonDocument infraDocument = JsonDocument.Parse(infraInspect.StdOut);
            JsonElement infra = infraDocument.RootElement.ValueKind == JsonValueKind.Array
                ? infraDocument.RootElement[0] : infraDocument.RootElement;
            string processLabel = GetStringProperty(infra, "ProcessLabel");
            string[] labelParts = processLabel.Split(':', 4);
            if (labelParts.Length != 4 || string.IsNullOrWhiteSpace(labelParts[3]))
                throw new InvalidOperationException("The egress pod did not report a valid SELinux level.");
            podSelinuxLevel = labelParts[3];
        }
        await EnsureInfraContainerCreatedAsync(
            "kautuka-egress-envoy",
            "egress-envoy",
            EnvoyPinnedImage,
            BuildEnvoyCreateArgs(
                envoyPath,
                envoySocketHostDir,
                adminSocketDir,
                isWindowsHostNativeDev,
                decisionEndpointIp,
                envoyCertificateDirectory,
                useDevelopmentSelinuxDomain,
                podSelinuxLevel,
                gatewayConnectEnvoyDirectory),
            allowlistEntries,
            cancellationToken).ConfigureAwait(false);

        await SyncWindowsPodmanInfraAllowlistAsync(cancellationToken).ConfigureAwait(false);

        PodmanCommandResult podStart = await podmanRunner.RunAsync(["pod", "start", EgressPodName], 30, cancellationToken).ConfigureAwait(false);
        if (podStart.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to start egress pod '{EgressPodName}' (rc {podStart.ExitCode}): {podStart.StdErr}");
        }

        if (!await IsEgressPodHealthyAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Egress pod '{EgressPodName}' did not reach a healthy state.");
        }
    }

    private async Task SyncWindowsPodmanInfraAllowlistAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(windowsPodmanMachineName)
            || string.IsNullOrWhiteSpace(windowsPodmanHookStateDirectory))
        {
            return;
        }

        PodmanCommandResult copy = await podmanRunner.RunAsync(
            ["machine", "cp", allowlistFilePath,
                $"{windowsPodmanMachineName}:{windowsPodmanHookStateDirectory}/infra-allowlist.json"],
            20,
            cancellationToken).ConfigureAwait(false);
        if (copy.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to copy the NAC infra allowlist into Podman Machine: {copy.StdErr}");
        }

        PodmanCommandResult permissions = await podmanRunner.RunAsync(
            ["machine", "ssh", windowsPodmanMachineName, $"chmod 600 '{windowsPodmanHookStateDirectory}/infra-allowlist.json'"],
            10,
            cancellationToken).ConfigureAwait(false);
        if (permissions.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to restrict Podman Machine NAC allowlist permissions: {permissions.StdErr}");
        }
    }

    private async Task<bool> IsEgressPodHealthyAsync(CancellationToken cancellationToken)
    {
        PodmanCommandResult inspect = await podmanRunner
            .RunAsync(["pod", "inspect", EgressPodName, "--format", "json"], 10, cancellationToken)
            .ConfigureAwait(false);

        if (inspect.ExitCode != 0 || !IsEgressPodHealthy(inspect.StdOut))
        {
            return false;
        }

        PodmanCommandResult boundaryInspect = await podmanRunner
            .RunAsync(["inspect", "kautuka-egress-boundary", "--format", "json"], 10, cancellationToken)
            .ConfigureAwait(false);

        return boundaryInspect.ExitCode == 0 && IsSuccessfulBoundaryInit(boundaryInspect.StdOut);
    }

    internal static bool IsSuccessfulBoundaryInit(string? inspectJson)
    {
        if (string.IsNullOrWhiteSpace(inspectJson))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(inspectJson);
            JsonElement container = doc.RootElement.ValueKind == JsonValueKind.Array
                ? (doc.RootElement.GetArrayLength() > 0 ? doc.RootElement[0] : default)
                : doc.RootElement;
            return container.ValueKind == JsonValueKind.Object
                && container.TryGetProperty("State", out JsonElement state)
                && state.ValueKind == JsonValueKind.Object
                && state.TryGetProperty("Status", out JsonElement status)
                && string.Equals(status.GetString(), "exited", StringComparison.OrdinalIgnoreCase)
                && state.TryGetProperty("ExitCode", out JsonElement exitCode)
                && exitCode.TryGetInt32(out int code)
                && code == 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static string BuildOutsideLanSourceAddressSetupCommand() =>
        $"lan_dev=$(ip -o -4 addr show | awk '$4 ~ /^10\\.248\\.0\\.2\\// {{ print $2; exit }}'); test -n \"$lan_dev\" && ip address replace {OutsideLanSourceIp}/24 dev \"$lan_dev\"";

    internal static string ComputeBoundaryRulesetHash(string ipv4RulesJson, string ipv6RulesJson)
    {
        string ipv4Hash = BoundaryRulesetRenderer.ComputeCanonicalNftJsonHash(
            ipv4RulesJson,
            ["private_reject", "wl_net_reject"]);
        string ipv6Hash = BoundaryRulesetRenderer.ComputeCanonicalNftJsonHash(
            ipv6RulesJson,
            ["ipv6_drop_pod"]);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ipv4Hash + ":" + ipv6Hash)));
    }

    internal static string ComputeBoundaryNftSourceHash(string renderedBoundaryNft)
    {
        ArgumentNullException.ThrowIfNull(renderedBoundaryNft);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(renderedBoundaryNft)));
    }

    internal static string ComputeReconcileConfigurationHash(string boundaryNft, string envoyConfig, string corefile)
    {
        ArgumentNullException.ThrowIfNull(boundaryNft);
        ArgumentNullException.ThrowIfNull(envoyConfig);
        ArgumentNullException.ThrowIfNull(corefile);
        return ComputeBoundaryNftSourceHash($"boundary.nft\0{boundaryNft}\0envoy.yaml\0{envoyConfig}\0Corefile\0{corefile}");
    }

    internal static void RequireMatchingBoundaryRulesetHash(string renderedNativeHash, string liveHash)
    {
        if (string.IsNullOrWhiteSpace(renderedNativeHash)
            || string.IsNullOrWhiteSpace(liveHash)
            || !string.Equals(renderedNativeHash, liveHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Live boundary nft ruleset hash '{liveHash}' does not match the native rendering '{renderedNativeHash}'.");
        }
    }

    internal async Task<string> ReadRenderedBoundaryRulesetHashAsync(CancellationToken cancellationToken)
    {
        string boundaryPath = Path.Combine(stateDirectory, "nac", "boundary", "boundary.nft");
        const string ipv6Marker = "__KUTUKA_IPV6_BOUNDARY__";
        string script =
            $"rules=$(cat \"$1\") && printf '%s\\n' \"$rules\" | nft -f - && nft -j list table ip kautuka_egress && printf '\\n{ipv6Marker}\\n' && nft -j list table ip6 kautuka_egress";
        PodmanCommandResult rendered;
        if (!string.IsNullOrWhiteSpace(windowsPodmanMachineName) && !string.IsNullOrWhiteSpace(windowsPodmanHookStateDirectory))
        {
            string remotePath = $"{windowsPodmanHookStateDirectory}/boundary.nft";
            PodmanCommandResult copy = await podmanRunner.RunAsync(
                ["machine", "cp", boundaryPath, $"{windowsPodmanMachineName}:{remotePath}"], 20, cancellationToken).ConfigureAwait(false);
            if (copy.ExitCode != 0)
            {
                throw new InvalidOperationException($"Failed to copy boundary ruleset into Podman Machine: {copy.StdErr}");
            }
            rendered = await podmanRunner.RunAsync(
                ["machine", "ssh", windowsPodmanMachineName, $"podman unshare unshare -n sh -c {QuoteShellArgument(script)} sh {QuoteShellArgument(remotePath)}"],
                30, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            rendered = await podmanRunner.RunAsync(
                ["unshare", "unshare", "-n", "sh", "-c", script, "sh", boundaryPath],
                30, cancellationToken).ConfigureAwait(false);
        }

        if (rendered.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Native nft rendering of the generated boundary ruleset failed (rc {rendered.ExitCode}): {rendered.StdErr}");
        }

        string[] tables = rendered.StdOut.Split($"\n{ipv6Marker}\n", StringSplitOptions.TrimEntries);
        if (tables.Length != 2 || string.IsNullOrWhiteSpace(tables[0]) || string.IsNullOrWhiteSpace(tables[1]))
        {
            throw new InvalidOperationException("Native nft rendering did not return both IPv4 and IPv6 boundary tables.");
        }

        return ComputeBoundaryRulesetHash(tables[0], tables[1]);
    }

    private static string QuoteShellArgument(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    internal async Task<string> ReadBoundaryRulesetHashAsync(CancellationToken cancellationToken)
    {
        PodmanCommandResult podInspect = await podmanRunner
            .RunAsync(["pod", "inspect", EgressPodName, "--format", "json"], 10, cancellationToken)
            .ConfigureAwait(false);
        string infraId = ReadPodInfraContainerId(podInspect);
        PodmanCommandResult infraInspect = await podmanRunner
            .RunAsync(["inspect", infraId, "--format", "json"], 10, cancellationToken)
            .ConfigureAwait(false);
        int infraPid = ReadContainerPid(infraInspect);
        string ipv4Rules = await ReadBoundaryNftTableAsync(infraPid, "ip", cancellationToken).ConfigureAwait(false);
        string ipv6Rules = await ReadBoundaryNftTableAsync(infraPid, "ip6", cancellationToken).ConfigureAwait(false);
        return ComputeBoundaryRulesetHash(ipv4Rules, ipv6Rules);
    }

    private static string ReadPodInfraContainerId(PodmanCommandResult inspect)
    {
        if (inspect.ExitCode != 0 || string.IsNullOrWhiteSpace(inspect.StdOut))
        {
            throw new InvalidOperationException($"Failed to inspect egress pod before boundary ruleset verification: {inspect.StdErr}");
        }

        using JsonDocument doc = JsonDocument.Parse(inspect.StdOut);
        JsonElement pod = doc.RootElement.ValueKind == JsonValueKind.Array
            ? (doc.RootElement.GetArrayLength() > 0 ? doc.RootElement[0] : default)
            : doc.RootElement;
        if (pod.ValueKind != JsonValueKind.Object
            || !pod.TryGetProperty("InfraContainerID", out JsonElement id)
            || string.IsNullOrWhiteSpace(id.GetString()))
        {
            throw new InvalidOperationException("Egress pod inspect did not contain an infra container ID for boundary ruleset verification.");
        }

        return id.GetString()!;
    }

    private static int ReadContainerPid(PodmanCommandResult inspect)
    {
        if (inspect.ExitCode != 0 || string.IsNullOrWhiteSpace(inspect.StdOut))
        {
            throw new InvalidOperationException($"Failed to inspect egress pod infra container: {inspect.StdErr}");
        }

        using JsonDocument doc = JsonDocument.Parse(inspect.StdOut);
        JsonElement container = doc.RootElement.ValueKind == JsonValueKind.Array
            ? (doc.RootElement.GetArrayLength() > 0 ? doc.RootElement[0] : default)
            : doc.RootElement;
        if (container.ValueKind != JsonValueKind.Object
            || !container.TryGetProperty("State", out JsonElement state)
            || !state.TryGetProperty("Pid", out JsonElement pid)
            || !pid.TryGetInt32(out int processId)
            || processId <= 0)
        {
            throw new InvalidOperationException("Egress pod infra inspect did not contain a running PID for boundary ruleset verification.");
        }

        return processId;
    }

    private async Task<string> ReadBoundaryNftTableAsync(int infraPid, string family, CancellationToken cancellationToken)
    {
        string pid = infraPid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        IReadOnlyList<string> args = ["unshare", "nsenter", "-t", pid, "-n", "nft", "-j", "list", "table", family, "kautuka_egress"];
        string output;
        if (!string.IsNullOrWhiteSpace(windowsPodmanMachineName) && !string.IsNullOrWhiteSpace(windowsPodmanHookStateDirectory))
        {
            PodmanCommandResult nft = await podmanRunner.RunAsync(
                ["machine", "ssh", windowsPodmanMachineName, $"podman unshare nsenter -t {pid} -n nft -j list table {family} kautuka_egress"],
                10, cancellationToken).ConfigureAwait(false);
            if (nft.ExitCode != 0)
            {
                throw new InvalidOperationException($"Podman Machine boundary nft verification failed for {family} (rc {nft.ExitCode}): {nft.StdErr}");
            }
            output = nft.StdOut;
        }
        else if (!Environment.IsPrivilegedProcess)
        {
            PodmanCommandResult nft = await podmanRunner.RunAsync(args, 10, cancellationToken).ConfigureAwait(false);
            if (nft.ExitCode != 0)
            {
                throw new InvalidOperationException($"Rootless boundary nft verification failed for {family} (rc {nft.ExitCode}): {nft.StdErr}");
            }

            output = nft.StdOut;
        }
        else
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "nsenter",
                    Arguments = $"-t {pid} -n nft -j list table {family} kautuka_egress",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };
            process.Start();
            output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            string error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"Boundary nft verification failed for {family} (rc {process.ExitCode}): {error}");
            }
        }

        return output;
    }

    internal static bool HasRequiredEgressPodConfiguration(string? inspectJson)
    {
        if (string.IsNullOrWhiteSpace(inspectJson))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(inspectJson);
            JsonElement pod = doc.RootElement.ValueKind == JsonValueKind.Array
                ? (doc.RootElement.GetArrayLength() > 0 ? doc.RootElement[0] : default)
                : doc.RootElement;
            if (pod.ValueKind != JsonValueKind.Object
                || !pod.TryGetProperty("CreateCommand", out JsonElement createCommand)
                || createCommand.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            string[] args = createCommand.EnumerateArray()
                .Where(static token => token.ValueKind == JsonValueKind.String)
                .Select(static token => token.GetString() ?? string.Empty)
                .ToArray();

            static bool HasPair(IReadOnlyList<string> values, string flag, string expected) =>
                Enumerable.Range(0, Math.Max(0, values.Count - 1))
                    .Any(index => string.Equals(values[index], flag, StringComparison.Ordinal)
                        && string.Equals(values[index + 1], expected, StringComparison.Ordinal));

            return HasPair(args, "--name", EgressPodName)
                && HasPair(args, "--network", $"{WorkloadNetworkName}:ip={EgressPodIp}")
                && HasPair(args, "--network", $"{OutsideNetworkName}:ip={OutsidePodIp}")
                && HasPair(args, "--sysctl", "net.ipv4.ip_forward=0")
                && HasPair(args, "--sysctl", "net.ipv6.conf.all.disable_ipv6=1");
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool IsEgressPodHealthy(string? inspectJson)
    {
        if (string.IsNullOrWhiteSpace(inspectJson))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(inspectJson);
            JsonElement pod = doc.RootElement.ValueKind == JsonValueKind.Array
                ? (doc.RootElement.GetArrayLength() > 0 ? doc.RootElement[0] : default)
                : doc.RootElement;
            if (pod.ValueKind != JsonValueKind.Object
                || !pod.TryGetProperty("State", out JsonElement state)
                || !string.Equals(state.GetString(), "Running", StringComparison.OrdinalIgnoreCase)
                || !pod.TryGetProperty("Containers", out JsonElement containers)
                || containers.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var states = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement container in containers.EnumerateArray())
            {
                string name = GetStringProperty(container, "Name");
                string containerState = GetStringProperty(container, "State");
                if (name.Length > 0)
                {
                    states[name] = containerState;
                }
            }

            bool hasInfra = states.Any(pair => pair.Key.EndsWith("-infra", StringComparison.OrdinalIgnoreCase)
                && string.Equals(pair.Value, "running", StringComparison.OrdinalIgnoreCase));
            return hasInfra
                && states.TryGetValue("kautuka-egress-boundary", out string? boundaryState)
                && string.Equals(boundaryState, "exited", StringComparison.OrdinalIgnoreCase)
                && states.TryGetValue("kautuka-egress-dns", out string? dnsState)
                && string.Equals(dnsState, "running", StringComparison.OrdinalIgnoreCase)
                && states.TryGetValue("kautuka-egress-envoy", out string? envoyState)
                && string.Equals(envoyState, "running", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static IReadOnlyList<string> BuildCoreDnsCreateArgs(string corefilePath, bool isWindowsHostNativeDev = false) =>
    [
        "create",
        "--pod", EgressPodName,
        "--name", "kautuka-egress-dns",
        "--cap-drop", "ALL",
        "--cap-add", "NET_BIND_SERVICE",
        "--security-opt", "no-new-privileges",
        "--read-only",
        "-v", $"{Path.GetFullPath(Path.GetDirectoryName(corefilePath)!)}:/etc/coredns:{GetConfigMountOptions(isWindowsHostNativeDev)}",
        CoreDnsPinnedImage,
        "-conf", "/etc/coredns/Corefile"
    ];

    internal static IReadOnlyList<string> BuildEnvoyCreateArgs(
        string envoyPath,
        string envoySocketHostDir,
        string adminSocketDir,
        bool isWindowsHostNativeDev = false,
        string? decisionEndpointIp = null,
        string? envoyCertificateDirectory = null,
        bool useDevelopmentSelinuxDomain = false,
        string? podSelinuxLevel = null,
        string? gatewayConnectEnvoyDirectory = null)
    {
        List<string> args =
        [
            "create",
            "--pod", EgressPodName,
            "--name", "kautuka-egress-envoy",
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--read-only",
            "-v", $"{Path.GetFullPath(Path.GetDirectoryName(envoyPath)!)}:/etc/kautuka-envoy:{GetConfigMountOptions(isWindowsHostNativeDev)}"
        ];

        if (!isWindowsHostNativeDev)
        {
            if (useDevelopmentSelinuxDomain)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(podSelinuxLevel);
                args.AddRange(["--security-opt", "label=type:kautuka_envoy.process"]);
                args.AddRange(["--security-opt", $"label=level:{podSelinuxLevel}"]);
            }
            args.Insert(5, "--group-add");
            args.Insert(6, "keep-groups");
            args.AddRange(["-v", $"{envoySocketHostDir}:/run/kautuka-nac:z"]);
            args.AddRange(["-v", $"{adminSocketDir}:/run/kautuka-admin:z"]);
            if (gatewayConnectEnvoyDirectory is not null)
            {
                foreach (string file in new[] { "server.crt", "server.key", "gateway-ca.crt" })
                    args.AddRange(["-v", $"{Path.Combine(gatewayConnectEnvoyDirectory, file)}:/etc/kautuka-connect/{file}:ro,Z"]);
            }
        }
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(decisionEndpointIp);
            ArgumentException.ThrowIfNullOrWhiteSpace(envoyCertificateDirectory);
            args.AddRange([
                "-v", $"{envoyCertificateDirectory}:/etc/envoy/certs:ro"
            ]);
        }

        args.Add(EnvoyPinnedImage);
        args.AddRange(["-c", "/etc/kautuka-envoy/envoy.yaml"]);

        return args;
    }

    private static string GetConfigMountOptions(bool isWindowsHostNativeDev) => isWindowsHostNativeDev ? "ro" : "ro,z";

    internal static IReadOnlyList<string> BuildBoundaryCreateArgs(string boundaryConfigDirectory, bool isWindowsHostNativeDev)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(boundaryConfigDirectory);
        string mountOptions = isWindowsHostNativeDev ? "ro" : "ro,z";
        return
        [
            "create",
            "--pod", EgressPodName,
            "--name", "kautuka-egress-boundary",
            "--init-ctr=always",
            "--cap-add", "NET_ADMIN",
            "--user", "0:0",
            "--entrypoint", "/bin/sh",
            "-v", $"{boundaryConfigDirectory}:/etc/kautuka-nac:{mountOptions}",
            BoundaryInitImage,
            "-c", $"{BuildOutsideLanSourceAddressSetupCommand()} && exec /usr/sbin/nft -f /etc/kautuka-nac/boundary.nft"
        ];
    }

    internal static bool HasRequiredBoundaryContainerMount(string? inspectJson, string boundaryConfigDirectory, bool isWindowsHostNativeDev)
    {
        if (string.IsNullOrWhiteSpace(inspectJson) || string.IsNullOrWhiteSpace(boundaryConfigDirectory))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(inspectJson);
            JsonElement container = doc.RootElement.ValueKind == JsonValueKind.Array
                ? (doc.RootElement.GetArrayLength() > 0 ? doc.RootElement[0] : default)
                : doc.RootElement;
            if (!TryGetCreateCommand(container, out JsonElement command))
            {
                return false;
            }

            string expected = $"{Path.GetFullPath(boundaryConfigDirectory)}:/etc/kautuka-nac:{(isWindowsHostNativeDev ? "ro" : "ro,z")}";
            StringComparison pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return command.EnumerateArray().Any(token => token.ValueKind == JsonValueKind.String
                && string.Equals(token.GetString(), expected, pathComparison));
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static bool HasRequiredConfigContainerMount(string? inspectJson, string configDirectory, string containerConfigDirectory, bool isWindowsHostNativeDev)
    {
        if (string.IsNullOrWhiteSpace(inspectJson) || string.IsNullOrWhiteSpace(configDirectory))
        {
            return false;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(inspectJson);
            JsonElement container = doc.RootElement.ValueKind == JsonValueKind.Array
                ? (doc.RootElement.GetArrayLength() > 0 ? doc.RootElement[0] : default)
                : doc.RootElement;
            if (!TryGetCreateCommand(container, out JsonElement command))
            {
                return false;
            }

            string options = isWindowsHostNativeDev ? "ro" : "ro,z";
            string expected = $"{Path.GetFullPath(configDirectory)}:{containerConfigDirectory}:{options}";
            StringComparison pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return command.EnumerateArray().Any(token => token.ValueKind == JsonValueKind.String
                && string.Equals(token.GetString(), expected, pathComparison));
        }
        catch (JsonException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    internal static bool HasRequiredInfraContainerImageAndArgs(string? inspectJson, string expectedImage, IReadOnlyList<string> expectedCreateArgs)
    {
        if (string.IsNullOrWhiteSpace(inspectJson) || string.IsNullOrWhiteSpace(expectedImage)) return false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(inspectJson);
            JsonElement container = doc.RootElement.ValueKind == JsonValueKind.Array
                ? (doc.RootElement.GetArrayLength() > 0 ? doc.RootElement[0] : default) : doc.RootElement;
            if (!TryGetCreateCommand(container, out JsonElement command)) return false;
            string[] actual = command.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()!).ToArray();
            string[] expected = expectedCreateArgs.ToArray();
            if (actual.Length > 0 && string.Equals(Path.GetFileName(actual[0]), "podman", StringComparison.OrdinalIgnoreCase)) actual = actual.Skip(1).ToArray();
            if (actual.Length > 0 && string.Equals(Path.GetFileName(actual[0]), "create", StringComparison.OrdinalIgnoreCase)) actual = actual.Skip(1).ToArray();
            if (expected.Length > 0 && string.Equals(Path.GetFileName(expected[0]), "create", StringComparison.OrdinalIgnoreCase)) expected = expected.Skip(1).ToArray();
            return actual.SequenceEqual(expected, StringComparer.Ordinal) && actual.Any(arg => string.Equals(arg, expectedImage, StringComparison.Ordinal));
        }
        catch (JsonException) { return false; }
    }

    private static bool TryGetCreateCommand(JsonElement container, out JsonElement command)
    {
        command = default;
        if (container.ValueKind != JsonValueKind.Object) return false;
        if (container.TryGetProperty("Config", out JsonElement config) && config.ValueKind == JsonValueKind.Object
            && config.TryGetProperty("CreateCommand", out command) && command.ValueKind == JsonValueKind.Array) return true;
        return container.TryGetProperty("CreateCommand", out command) && command.ValueKind == JsonValueKind.Array;
    }


    private async Task EnsureConfigContainerMountCurrentAsync(string containerName, string configDirectory, string containerConfigDirectory, bool isWindowsHostNativeDev, CancellationToken cancellationToken)
    {
        PodmanCommandResult inspect = await podmanRunner.RunAsync(["inspect", containerName], 10, cancellationToken).ConfigureAwait(false);
        if (inspect.ExitCode != 0)
        {
            PodmanCommandResult exists = await podmanRunner.RunAsync(["container", "exists", containerName], 10, cancellationToken).ConfigureAwait(false);
            if (exists.ExitCode == 1)
            {
                return;
            }

            if (exists.ExitCode != 0)
            {
                throw new InvalidOperationException($"Cannot determine whether infrastructure container '{containerName}' exists after inspection failed: {exists.StdErr}");
            }

            throw new InvalidOperationException($"Failed to inspect existing infrastructure container '{containerName}'; refusing to assume its config mount is current: {inspect.StdErr}");
        }

        if (HasRequiredConfigContainerMount(inspect.StdOut, configDirectory, containerConfigDirectory, isWindowsHostNativeDev))
        {
            return;
        }

        PodmanCommandResult podInspect = await podmanRunner.RunAsync(["pod", "inspect", EgressPodName], 10, cancellationToken).ConfigureAwait(false);
        string? podState = podInspect.ExitCode == 0 ? GetPodState(podInspect.StdOut) : null;
        if (podState is null)
        {
            throw new InvalidOperationException($"Cannot safely replace infrastructure container '{containerName}' because egress pod inspection failed or returned no state.");
        }

        if (IsPodStopped(podState))
        {
            if (!HasOnlyInactiveEgressPodMembers(podInspect.StdOut))
            {
                throw new InvalidOperationException($"Egress pod reports stopped but one or more members are active, missing, or unknown; refusing to replace '{containerName}'.");
            }
        }
        else if (string.Equals(podState, "Running", StringComparison.OrdinalIgnoreCase)
            || string.Equals(podState, "Degraded", StringComparison.OrdinalIgnoreCase))
        {
            PodmanCommandResult stopped = await podmanRunner.RunAsync(["pod", "stop", EgressPodName], 30, cancellationToken).ConfigureAwait(false);
            if (stopped.ExitCode != 0)
            {
                throw new InvalidOperationException($"Failed to stop egress pod before replacing stale config mount for '{containerName}': {stopped.StdErr}");
            }

            PodmanCommandResult stoppedInspect = await podmanRunner.RunAsync(["pod", "inspect", EgressPodName], 10, cancellationToken).ConfigureAwait(false);
            if (stoppedInspect.ExitCode != 0
                || !IsPodStopped(GetPodState(stoppedInspect.StdOut))
                || !HasOnlyInactiveEgressPodMembers(stoppedInspect.StdOut))
            {
                throw new InvalidOperationException($"Egress pod and every member must reach a known inactive state before replacing '{containerName}'.");
            }
        }
        else
        {
            throw new InvalidOperationException($"Cannot safely replace '{containerName}' because egress pod state '{podState}' is not a supported stoppable state.");
        }

        PodmanCommandResult removed = await podmanRunner.RunAsync(["rm", "-f", containerName], 20, cancellationToken).ConfigureAwait(false);
        if (removed.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to remove infrastructure container '{containerName}' with a stale config mount: {removed.StdErr}");
        }
    }

    private async Task EnsureBoundaryContainerMountCurrentAsync(string boundaryConfigDirectory, bool isWindowsHostNativeDev, CancellationToken cancellationToken)
    {
        const string containerName = "kautuka-egress-boundary";
        PodmanCommandResult inspect = await podmanRunner.RunAsync(["inspect", containerName], 10, cancellationToken).ConfigureAwait(false);
        if (inspect.ExitCode != 0 || HasRequiredBoundaryContainerMount(inspect.StdOut, boundaryConfigDirectory, isWindowsHostNativeDev))
        {
            return;
        }

        PodmanCommandResult podInspect = await podmanRunner.RunAsync(["pod", "inspect", EgressPodName], 10, cancellationToken).ConfigureAwait(false);
        string? podState = podInspect.ExitCode == 0 ? GetPodState(podInspect.StdOut) : null;
        if (podState is null)
        {
            throw new InvalidOperationException("Cannot safely replace the egress boundary container because egress pod inspection failed or returned no state.");
        }

        if (IsPodStopped(podState))
        {
            if (!HasOnlyInactiveEgressPodMembers(podInspect.StdOut))
            {
                throw new InvalidOperationException("Egress pod reports a stopped state but one or more pod members are active, missing, or unknown; refusing boundary replacement.");
            }
        }
        else if (string.Equals(podState, "Running", StringComparison.OrdinalIgnoreCase)
            || string.Equals(podState, "Degraded", StringComparison.OrdinalIgnoreCase))
        {
            PodmanCommandResult stopped = await podmanRunner.RunAsync(["pod", "stop", EgressPodName], 30, cancellationToken).ConfigureAwait(false);
            if (stopped.ExitCode != 0)
            {
                throw new InvalidOperationException($"Failed to stop egress pod before replacing its stale boundary config mount: {stopped.StdErr}");
            }

            PodmanCommandResult stoppedInspect = await podmanRunner.RunAsync(["pod", "inspect", EgressPodName], 10, cancellationToken).ConfigureAwait(false);
            if (stoppedInspect.ExitCode != 0
                || !IsPodStopped(GetPodState(stoppedInspect.StdOut))
                || !HasOnlyInactiveEgressPodMembers(stoppedInspect.StdOut))
            {
                throw new InvalidOperationException("Egress pod and every member must reach a known inactive state before replacing its boundary container.");
            }
        }
        else
        {
            throw new InvalidOperationException($"Cannot safely replace the egress boundary container because egress pod state '{podState}' is not a supported stoppable state.");
        }

        PodmanCommandResult removed = await podmanRunner.RunAsync(["rm", "-f", containerName], 20, cancellationToken).ConfigureAwait(false);
        if (removed.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to remove egress boundary container with a stale config mount: {removed.StdErr}");
        }
    }

    private static string? GetPodState(string? inspectJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(inspectJson ?? string.Empty);
            JsonElement pod = doc.RootElement.ValueKind == JsonValueKind.Array
                ? (doc.RootElement.GetArrayLength() > 0 ? doc.RootElement[0] : default)
                : doc.RootElement;
            return pod.ValueKind == JsonValueKind.Object
                && pod.TryGetProperty("State", out JsonElement state)
                ? state.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsPodStopped(string? state) =>
        string.Equals(state, "Exited", StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, "Stopped", StringComparison.OrdinalIgnoreCase)
        || string.Equals(state, "Created", StringComparison.OrdinalIgnoreCase);

    private static bool HasOnlyInactiveEgressPodMembers(string? inspectJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(inspectJson ?? string.Empty);
            JsonElement pod = doc.RootElement.ValueKind == JsonValueKind.Array
                ? (doc.RootElement.GetArrayLength() > 0 ? doc.RootElement[0] : default)
                : doc.RootElement;
            if (pod.ValueKind != JsonValueKind.Object
                || !pod.TryGetProperty("Containers", out JsonElement containers)
                || containers.ValueKind != JsonValueKind.Array
                || containers.GetArrayLength() == 0)
            {
                return false;
            }

            string infraContainerId = GetStringProperty(pod, "InfraContainerID");
            if (infraContainerId.Length == 0)
            {
                return false;
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool hasMatchingInfra = false;
            foreach (JsonElement member in containers.EnumerateArray())
            {
                string name = GetStringProperty(member, "Name");
                string id = GetStringProperty(member, "Id");
                string state = GetStringProperty(member, "State");
                if (name.Length == 0
                    || id.Length == 0
                    || state.Length == 0
                    || !names.Add(name)
                    || !ids.Add(id)
                    || !(string.Equals(state, "exited", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(state, "stopped", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(state, "created", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(state, "configured", StringComparison.OrdinalIgnoreCase)))
                {
                    return false;
                }

                hasMatchingInfra |= string.Equals(id, infraContainerId, StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith("-infra", StringComparison.OrdinalIgnoreCase);
            }

            return hasMatchingInfra
                && (names.Contains("kautuka-egress-boundary") || names.Contains("kautuka-egress-dns") || names.Contains("kautuka-egress-envoy"));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static List<string> BuildEgressPodCreateArgs(bool isWindowsHostNativeDev, string? decisionEndpointIp)
    {
        List<string> args =
        [
            "pod", "create",
            "--name", EgressPodName,
            "--network", $"{WorkloadNetworkName}:ip={EgressPodIp}",
            "--network", $"{OutsideNetworkName}:ip={OutsidePodIp}",
            "--sysctl", "net.ipv4.ip_forward=0",
            "--sysctl", "net.ipv6.conf.all.disable_ipv6=1"
        ];

        if (isWindowsHostNativeDev)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(decisionEndpointIp);
            args.AddRange(["--add-host", $"{WindowsPodmanDevEgressOptions.DecisionHostName}:{decisionEndpointIp}"]);
        }

        return args;
    }

    private async Task EnsureInfraContainerCreatedAsync(
        string containerName,
        string role,
        string imageDigest,
        IReadOnlyList<string> createArgs,
        List<InfraAllowlistEntry> allowlistEntries,
        CancellationToken cancellationToken)
    {
        PodmanCommandResult inspect = await podmanRunner
            .RunAsync(["inspect", containerName], 10, cancellationToken)
            .ConfigureAwait(false);

        if (inspect.ExitCode != 0)
        {
            PodmanCommandResult createRes = await podmanRunner
                .RunAsync(createArgs, 30, cancellationToken)
                .ConfigureAwait(false);
            if (createRes.ExitCode != 0)
            {
                throw new InvalidOperationException($"Failed to create infrastructure container '{containerName}': {createRes.StdErr}");
            }

            inspect = await podmanRunner
                .RunAsync(["inspect", containerName], 10, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (createArgs.Count > 0 && !await IsInfraContainerCurrentAsync(inspect.StdOut, imageDigest, createArgs, cancellationToken).ConfigureAwait(false))
        {
            await ReplaceStaleInfraContainerAsync(containerName, cancellationToken).ConfigureAwait(false);
            PodmanCommandResult createRes = await podmanRunner.RunAsync(createArgs, 30, cancellationToken).ConfigureAwait(false);
            if (createRes.ExitCode != 0)
                throw new InvalidOperationException($"Failed to recreate infrastructure container '{containerName}': {createRes.StdErr}");
            inspect = await podmanRunner.RunAsync(["inspect", containerName], 10, cancellationToken).ConfigureAwait(false);
            if (inspect.ExitCode != 0 || (createArgs.Count > 0 && !await IsInfraContainerCurrentAsync(inspect.StdOut, imageDigest, createArgs, cancellationToken).ConfigureAwait(false)))
                throw new InvalidOperationException($"Recreated infrastructure container '{containerName}' does not match its requested image and create arguments.");
        }

        string? containerId = null;
        if (inspect.ExitCode == 0 && !string.IsNullOrWhiteSpace(inspect.StdOut))
        {
            using JsonDocument doc = JsonDocument.Parse(inspect.StdOut);
            JsonElement elem = doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0
                ? doc.RootElement[0]
                : doc.RootElement;
            if (elem.TryGetProperty("Id", out JsonElement idElem))
            {
                containerId = idElem.GetString();
            }
        }

        if (!string.IsNullOrWhiteSpace(containerId))
        {
            knownInfraContainerIds.Add(containerId);
            string[] expectedIps = GetExpectedInfraIpv4(role);
            int existingEntryIndex = allowlistEntries.FindIndex(e => string.Equals(e.Id, containerId, StringComparison.OrdinalIgnoreCase));
            IReadOnlyList<IReadOnlyList<string>>? expectedIpv4Alternates = string.Equals(role, "egress-boundary", StringComparison.Ordinal)
                ? [[EgressPodIp, OutsidePodIp, OutsideLanSourceIp]]
                : null;
            var entry = new InfraAllowlistEntry(containerId, role, expectedIps, imageDigest, expectedIpv4Alternates);
            if (existingEntryIndex < 0)
            {
                allowlistEntries.Add(entry);
            }
            else
            {
                allowlistEntries[existingEntryIndex] = entry;
            }
            WriteInfraAllowlist(allowlistEntries);
        }
    }

    private async Task<bool> IsInfraContainerCurrentAsync(string? containerInspectJson, string imageReference, IReadOnlyList<string> createArgs, CancellationToken cancellationToken)
    {
        if (!HasRequiredInfraContainerImageAndArgs(containerInspectJson, imageReference, createArgs)) return false;
        try
        {
            using JsonDocument containerDoc = JsonDocument.Parse(containerInspectJson!);
            JsonElement container = containerDoc.RootElement.ValueKind == JsonValueKind.Array ? containerDoc.RootElement[0] : containerDoc.RootElement;
            string actualId = GetStringProperty(container, "Image");
            if (actualId.Length == 0) return false;
            PodmanCommandResult imageInspect = await podmanRunner.RunAsync(["image", "inspect", imageReference], 10, cancellationToken).ConfigureAwait(false);
            if (imageInspect.ExitCode != 0 || string.IsNullOrWhiteSpace(imageInspect.StdOut)) return false;
            using JsonDocument imageDoc = JsonDocument.Parse(imageInspect.StdOut);
            JsonElement image = imageDoc.RootElement.ValueKind == JsonValueKind.Array ? imageDoc.RootElement[0] : imageDoc.RootElement;
            return string.Equals(actualId, GetStringProperty(image, "Id"), StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException) { return false; }
    }

    private async Task ReplaceStaleInfraContainerAsync(string containerName, CancellationToken cancellationToken)
    {
        PodmanCommandResult podInspect = await podmanRunner.RunAsync(["pod", "inspect", EgressPodName], 10, cancellationToken).ConfigureAwait(false);
        string? state = podInspect.ExitCode == 0 ? GetPodState(podInspect.StdOut) : null;
        if (state is null) throw new InvalidOperationException($"Cannot safely replace stale infrastructure container '{containerName}' because egress pod inspection failed.");
        if (!IsPodStopped(state))
        {
            if (!string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase) && !string.Equals(state, "Degraded", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Cannot safely replace '{containerName}' because egress pod state '{state}' is unsupported.");
            PodmanCommandResult stopped = await podmanRunner.RunAsync(["pod", "stop", EgressPodName], 30, cancellationToken).ConfigureAwait(false);
            if (stopped.ExitCode != 0) throw new InvalidOperationException($"Failed to stop egress pod before replacing '{containerName}': {stopped.StdErr}");
            podInspect = await podmanRunner.RunAsync(["pod", "inspect", EgressPodName], 10, cancellationToken).ConfigureAwait(false);
            if (podInspect.ExitCode != 0 || !IsPodStopped(GetPodState(podInspect.StdOut)) || !HasOnlyInactiveEgressPodMembers(podInspect.StdOut))
                throw new InvalidOperationException($"Egress pod members must be confirmed inactive before replacing '{containerName}'.");
        }
        else if (!HasOnlyInactiveEgressPodMembers(podInspect.StdOut))
            throw new InvalidOperationException($"Egress pod members must be confirmed inactive before replacing '{containerName}'.");
        PodmanCommandResult removed = await podmanRunner.RunAsync(["rm", "-f", containerName], 20, cancellationToken).ConfigureAwait(false);
        if (removed.ExitCode != 0) throw new InvalidOperationException($"Failed to remove stale infrastructure container '{containerName}': {removed.StdErr}");
    }

    internal static string[] GetExpectedInfraIpv4(string role) =>
        string.Equals(role, "egress-envoy", StringComparison.Ordinal)
        || string.Equals(role, "egress-dns", StringComparison.Ordinal)
            ? [EgressPodIp, OutsidePodIp, OutsideLanSourceIp]
            : [EgressPodIp, OutsidePodIp];

    internal static async Task WriteConfigAtomicallyAsync(
        string path,
        string content,
        CancellationToken cancellationToken,
        Func<Stream, string, CancellationToken, Task>? writer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        string fullPath = Path.GetFullPath(path);
        string directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        string tempPath = Path.Combine(directory, ".nac-config-" + Guid.NewGuid().ToString("N") + ".tmp");

        try
        {
            var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using (stream.ConfigureAwait(false))
            {
                writer ??= static async (target, text, token) =>
                    await target.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text), token).ConfigureAwait(false);

                await writer(stream, content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private async Task EnsureBoundaryImageAsync(CancellationToken cancellationToken)
    {
        PodmanCommandResult inspect = await podmanRunner
            .RunAsync(["image", "exists", BoundaryInitImage], 10, cancellationToken)
            .ConfigureAwait(false);

        if (inspect.ExitCode == 0)
        {
            return;
        }

        PodmanCommandResult pull = await podmanRunner
            .RunAsync(["pull", BoundaryInitImage], 300, cancellationToken)
            .ConfigureAwait(false);
        if (pull.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to pull pinned boundary image '{BoundaryInitImage}' (rc {pull.ExitCode}): {pull.StdErr}");
        }
    }

    private void StartReconcileLoop(IWorkloadRegistry registry)
    {
        if (reconcileTask is { IsCompleted: false })
        {
            return;
        }

        reconcileCts = new CancellationTokenSource();
        CancellationToken token = reconcileCts.Token;

        reconcileTask = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await timer.WaitForNextTickAsync(token).ConfigureAwait(false);
                    await ReconcileOnceAsync(registry, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error in 30s egress topology reconcile loop.");
                }
            }
        }, token);
    }

    internal async Task ReconcileOnceAsync(IWorkloadRegistry registry, CancellationToken cancellationToken)
    {
        if (policyStore is not null)
        {
            BrakeState brake = policyStore.GetBrakeSnapshot();
            if (brake.System)
                await EngageBrakeAsync(null, registry, cancellationToken).ConfigureAwait(false);
            else
                foreach (UserBrakeEntry user in brake.Users)
                    await EngageBrakeAsync(user.UserId, registry, cancellationToken).ConfigureAwait(false);
        }

        if (topologyIncidentLatched)
        {
            CloseLaunchAdmission();
            await ContainWorkloadNetworkMembersAsync(registry, "previous topology incident", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (startupInitializationRetryPending)
        {
            try
            {
                await InitializeCoreAsync(registry, OperatingSystem.IsWindows(), windowsDecisionEndpointIp, true, true, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogWarning(ex, "NAC startup retry failed; admission remains closed."); }
            return;
        }

        if (startupCanaryRetryPending)
        {
            if (IsCanaryIdentityBraked())
            {
                return;
            }

            string currentStartupConfigurationHash;
            try
            {
                currentStartupConfigurationHash = await ReadRenderedReconcileConfigurationHashAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Rendered egress configuration could not be read before startup retry; recovering with the launch gate closed.");
                lock (gateLock)
                {
                    transientRecoveryPending = true;
                }
                await RecoverEgressTopologyAsync(registry, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (string.IsNullOrWhiteSpace(expectedBoundaryNftSourceHash)
                || string.IsNullOrWhiteSpace(currentStartupConfigurationHash))
            {
                logger.LogWarning("Rendered egress configuration hash is unavailable before startup retry; recovering with the launch gate closed.");
                lock (gateLock)
                {
                    transientRecoveryPending = true;
                }
                await RecoverEgressTopologyAsync(registry, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!string.Equals(currentStartupConfigurationHash, expectedBoundaryNftSourceHash, StringComparison.Ordinal))
            {
                await LatchTopologyIncidentAndContainAsync(registry, "rendered egress configuration drift during startup retry", cancellationToken).ConfigureAwait(false);
                return;
            }

            try
            {
                await InitializeAsync(
                    registry,
                    startupIsWindowsHostNativeDev,
                    startupDecisionEndpointIp,
                    startupIncludeLanGrants,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                CloseLaunchAdmission();
                logger.LogWarning(ex, "Startup revalidation after persisted brake release did not complete; the startup gate remains closed.");
            }
            return;
        }

        if (!IsStartupGateOpen && transientRecoveryPending)
        {
            if (workloadSnapshotRetryPending)
            {
                await RetryWorkloadSnapshotAsync(registry, cancellationToken).ConfigureAwait(false);
                return;
            }
            await RecoverEgressTopologyAsync(registry, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!IsStartupGateOpen)
        {
            await ReconcileWorkloadRegistryAsync(registry, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (IsStartupGateOpen)
        {
            string currentSourceHash;
            try
            {
                currentSourceHash = await ReadRenderedReconcileConfigurationHashAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Rendered egress configuration could not be read during reconcile; recreating the egress pod with the startup gate closed.");
                await RecoverEgressTopologyAsync(registry, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (string.IsNullOrWhiteSpace(expectedBoundaryNftSourceHash) || string.IsNullOrWhiteSpace(currentSourceHash))
            {
                logger.LogWarning("Rendered egress configuration hash is unavailable; recreating the egress pod with the startup gate closed.");
                await RecoverEgressTopologyAsync(registry, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!string.Equals(currentSourceHash, expectedBoundaryNftSourceHash, StringComparison.Ordinal))
            {
                logger.LogError("Rendered egress configuration changed during reconcile; closing the NAC startup gate.");
                await LatchTopologyIncidentAndContainAsync(registry, "rendered egress configuration drift", cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        bool egressPodHealthy;
        try
        {
            egressPodHealthy = await IsEgressPodHealthyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Egress pod health check failed during reconcile; recreating the egress pod with the startup gate closed.");
            await RecoverEgressTopologyAsync(registry, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!egressPodHealthy)
        {
            logger.LogWarning("Egress pod health check failed during reconcile; recreating the egress pod with the startup gate closed.");
            await RecoverEgressTopologyAsync(registry, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (OperatingSystem.IsLinux() || !string.IsNullOrWhiteSpace(windowsPodmanMachineName))
        {
            try
            {
                string liveBoundaryHash = await ReadBoundaryRulesetHashAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(expectedBoundaryRulesetHash) || string.IsNullOrWhiteSpace(liveBoundaryHash))
                {
                    logger.LogWarning("Boundary nft ruleset verification returned an empty hash; recreating the egress pod with the startup gate closed.");
                    await RecoverEgressTopologyAsync(registry, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (!string.Equals(liveBoundaryHash, expectedBoundaryRulesetHash, StringComparison.Ordinal))
                {
                    logger.LogError(
                        "Boundary nft ruleset hash changed during reconcile (expected {ExpectedHash}, observed {ObservedHash}); closing the NAC startup gate.",
                        expectedBoundaryRulesetHash,
                        liveBoundaryHash);
                    await LatchTopologyIncidentAndContainAsync(registry, "live boundary ruleset drift", cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Boundary nft ruleset could not be verified during reconcile; recreating the egress pod with the startup gate closed.");
                await RecoverEgressTopologyAsync(registry, cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        await ReconcileWorkloadRegistryAsync(registry, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> ReconcileWorkloadRegistryAsync(IWorkloadRegistry registry, CancellationToken cancellationToken)
    {
        bool RejectInvalidSnapshot()
        {
            lock (gateLock)
            {
                if (isStartupGateOpen) workloadSnapshotRetryPending = true;
            }
            CloseForQuarantineCleanupRetry();
            return false;
        }

        // Only consider quarantines recorded before the snapshot. A workload may
        // be created and quarantined while Podman is producing the listing.
        IReadOnlyList<string> quarantineBeforeSnapshot = desiredStateStore?.QuarantinedWorkloadIds ?? [];
        // Compare running containers to registry using full immutable IDs.
        PodmanCommandResult psResult;
        try
        {
            psResult = await podmanRunner
                .RunAsync(["ps", "-a", "--no-trunc", "--format", "json"], 30, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not list workload containers; closing the gate for retry.");
            return RejectInvalidSnapshot();
        }

        if (psResult.ExitCode != 0)
        {
            return RejectInvalidSnapshot();
        }

        bool cleanupComplete = true;

        var liveCids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var runningCids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cidByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(psResult.StdOut))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(psResult.StdOut);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement container in doc.RootElement.EnumerateArray())
                    {
                        string cid = container.TryGetProperty("Id", out JsonElement idElem) ? idElem.GetString() ?? string.Empty : string.Empty;
                        if (!WorkloadPostStartVerifier.IsCanonicalContainerId(cid))
                        {
                            return RejectInvalidSnapshot();
                        }

                        liveCids.Add(cid);
                        if (container.TryGetProperty("State", out JsonElement stateElem)
                            && string.Equals(stateElem.GetString(), "running", StringComparison.OrdinalIgnoreCase)) runningCids.Add(cid);

                        if (container.TryGetProperty("Names", out JsonElement namesElem))
                        {
                            if (namesElem.ValueKind == JsonValueKind.Array)
                            {
                                foreach (JsonElement nameItem in namesElem.EnumerateArray())
                                {
                                    string? name = nameItem.GetString();
                                    if (!string.IsNullOrWhiteSpace(name))
                                    {
                                        cidByName[name.TrimStart('/')] = cid;
                                    }
                                }
                            }
                            else if (namesElem.ValueKind == JsonValueKind.String)
                            {
                                string? name = namesElem.GetString();
                                if (!string.IsNullOrWhiteSpace(name))
                                {
                                    cidByName[name.TrimStart('/')] = cid;
                                }
                            }
                        }
                    }
                }
                else return RejectInvalidSnapshot();
            }
            catch
            {
                return RejectInvalidSnapshot();
            }
        }
        else return RejectInvalidSnapshot();

        if (desiredStateStore is not null)
        {
            try
            {
                desiredStateStore.ForgetQuarantinedWorkloads(quarantineBeforeSnapshot
                    .Where(id => WorkloadPostStartVerifier.IsCanonicalContainerId(id) && !liveCids.Contains(id)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not remove stale workload quarantine entries; they remain eligible for retry.");
            }
            HashSet<string> registeredContainerIds = registry.GetAll().Select(static entry => entry.ContainerId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string quarantinedId in desiredStateStore.QuarantinedWorkloadIds)
            {
                if (!WorkloadPostStartVerifier.IsCanonicalContainerId(quarantinedId)
                    || !liveCids.Contains(quarantinedId)
                    || registeredContainerIds.Contains(quarantinedId)
                    || cidByName.Any(pair => string.Equals(pair.Value, quarantinedId, StringComparison.OrdinalIgnoreCase)
                        && registeredContainerIds.Contains(pair.Key)))
                {
                    continue;
                }

                try { desiredStateStore.QuarantineWorkload(quarantinedId); }
                catch (Exception persistenceException) when (persistenceException is not OperationCanceledException)
                {
                    logger.LogCritical(persistenceException, "Could not retry quarantine persistence for unregistered surviving workload {ContainerId}.", quarantinedId);
                    cleanupComplete = false;
                    CloseForQuarantineCleanupRetry();
                }
                try
                {
                    PodmanCommandResult removal = await podmanRunner.RunAsync(["rm", "-f", "--time", "1", quarantinedId], 5, cancellationToken).ConfigureAwait(false);
                    if (removal.ExitCode != 0)
                    {
                        logger.LogCritical("Persistently quarantined workload {ContainerId} survived removal (rc {ExitCode}); closing launch admission for retry.", quarantinedId, removal.ExitCode);
                        cleanupComplete = false;
                        CloseForQuarantineCleanupRetry();
                    }
                }
                catch (Exception cleanupException) when (cleanupException is not OperationCanceledException)
                {
                    logger.LogCritical(cleanupException, "Persistently quarantined workload {ContainerId} removal threw; closing launch admission for retry.", quarantinedId);
                    cleanupComplete = false;
                    CloseForQuarantineCleanupRetry();
                }
            }
        }

        // Deregister dead containers (matching both canonical hex container IDs and pre-start registered names).
        foreach (RegistryEntry entry in registry.GetAll())
        {

            bool hasDirectId = liveCids.Contains(entry.ContainerId);
            bool hasNameMatch = cidByName.TryGetValue(entry.ContainerId, out string? matchedCid);
            string? liveCid = hasDirectId ? entry.ContainerId : hasNameMatch ? matchedCid : null;
            bool isActive = liveCid is not null && runningCids.Contains(liveCid);
            bool isPackageProxy = string.Equals(entry.AgentId, "package-proxy", StringComparison.Ordinal);
            if (isPackageProxy && !isActive)
            {
                packageProxyStartupReady = false;
            }
            bool isQuarantined = desiredStateStore?.IsWorkloadQuarantined(entry.ContainerId) == true
                || (liveCid is not null && desiredStateStore?.IsWorkloadQuarantined(liveCid) == true);
            if (liveCid is not null && isQuarantined)
            {
                string quarantineId = WorkloadPostStartVerifier.IsCanonicalContainerId(liveCid) ? liveCid : entry.ContainerId;
                registry.TryQuarantine(entry.Ip, entry.ContainerId, liveCid);
                try { desiredStateStore?.QuarantineWorkload(quarantineId); }
                catch (Exception persistenceException) when (persistenceException is not OperationCanceledException)
                {
                    logger.LogCritical(persistenceException, "Could not retry quarantine persistence for surviving workload {ContainerId}.", quarantineId);
                    cleanupComplete = false;
                    CloseForQuarantineCleanupRetry();
                }
                if (WorkloadPostStartVerifier.IsCanonicalContainerId(liveCid))
                {
                    try
                    {
                        PodmanCommandResult removal = await podmanRunner.RunAsync(["rm", "-f", "--time", "1", liveCid], 5, cancellationToken).ConfigureAwait(false);
                        if (removal.ExitCode == 0)
                        {
                            registry.Deregister(entry.Ip, entry.ContainerId);
                            continue;
                        }
                        logger.LogCritical("Quarantined workload {ContainerId} survived removal (rc {ExitCode}); closing launch admission for retry.", liveCid, removal.ExitCode);
                    }
                    catch (Exception cleanupException) when (cleanupException is not OperationCanceledException)
                    {
                        logger.LogCritical(cleanupException, "Quarantined workload {ContainerId} removal threw; closing launch admission for retry.", liveCid);
                    }
                }
                cleanupComplete = false;
                CloseForQuarantineCleanupRetry();
                continue;
            }
            if (isActive)
            {
                try
                {
                    if (isPackageProxy && !await IsPreviouslyAuthorizedPackageProxyAsync(liveCid!, cancellationToken).ConfigureAwait(false))
                    {
                        packageProxyStartupReady = false;
                        continue;
                    }

                    string canonicalId = await postStartVerifier.VerifyWorkloadRulesetAsync(liveCid!, entry.Ip,
                        isPackageProxy: isPackageProxy,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(canonicalId, entry.ContainerId, StringComparison.OrdinalIgnoreCase)) registry.UpdateContainerId(entry.Ip, entry.ContainerId, canonicalId);
                    if (transientWorkloadDenials.TryGetValue(entry.Ip, out RegistryEntry? prior)
                        && string.Equals(prior.ContainerId, entry.ContainerId, StringComparison.OrdinalIgnoreCase)
                        && registry.FindByIp(entry.Ip) is RegistryEntry current
                        && registry.TryPromoteVerified(current, prior with { ContainerId = canonicalId }))
                    {
                        transientWorkloadDenials.TryRemove(entry.Ip, out _);
                    }
                }
                catch (WorkloadRulesetVerificationException ex)
                {
                    transientWorkloadDenials.TryRemove(entry.Ip, out _);
                    if (isPackageProxy) packageProxyStartupReady = false;
                    string? canonicalId = ex is WorkloadRulesetVerificationException drift ? drift.CanonicalContainerId : null;
                    canonicalId ??= WorkloadPostStartVerifier.IsCanonicalContainerId(liveCid) ? liveCid : null;
                    if (canonicalId is null)
                    {
                        logger.LogCritical(ex, "Could not establish immutable identity for active workload {ContainerId}; closing launch admission.", entry.ContainerId);
                        registry.TryQuarantine(entry.Ip, entry.ContainerId);
                        cleanupComplete = false;
                        CloseForQuarantineCleanupRetry();
                        continue;
                    }
                    logger.LogWarning(ex, "Workload rules drift detected for {ContainerId}; quarantining and removing it.", canonicalId);
                    registry.TryQuarantine(entry.Ip, entry.ContainerId, canonicalId);
                    try
                    {
                        desiredStateStore?.QuarantineWorkload(canonicalId);
                    }
                    catch (Exception persistenceException) when (persistenceException is not OperationCanceledException)
                    {
                        logger.LogError(persistenceException, "Could not persist quarantine for workload {ContainerId}; continuing fail-closed cleanup.", canonicalId);
                    }
                    PodmanCommandResult? removal = null;
                    try { removal = await podmanRunner.RunAsync(["rm", "-f", "--time", "1", canonicalId], 5, cancellationToken).ConfigureAwait(false); }
                    catch (Exception cleanupException) when (cleanupException is not OperationCanceledException)
                    {
                        logger.LogError(cleanupException, "Could not remove quarantined workload {ContainerId}.", canonicalId);
                    }
                    if (removal?.ExitCode == 0)
                    {
                        registry.Deregister(entry.Ip, entry.ContainerId);
                    }
                    else
                    {
                        try { desiredStateStore?.QuarantineWorkload(canonicalId); }
                        catch (Exception persistenceException) when (persistenceException is not OperationCanceledException) { logger.LogCritical(persistenceException, "Quarantine persistence retry failed for surviving workload {ContainerId}.", canonicalId); }
                        logger.LogCritical("Quarantined workload {ContainerId} survived removal; closing launch admission for retry.", canonicalId);
                        cleanupComplete = false;
                        CloseForQuarantineCleanupRetry();
                    }
                    if (removal?.ExitCode == 0) registry.Deregister(entry.Ip, entry.ContainerId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (isPackageProxy) packageProxyStartupReady = false;
                    if (registry.IsVerified(entry.Ip, entry.ContainerId))
                        transientWorkloadDenials[entry.Ip] = entry;
                    registry.TryQuarantine(entry.Ip, entry.ContainerId, liveCid);
                    logger.LogWarning(ex, "Could not verify workload rules for {ContainerId}; denying traffic and retrying.", entry.ContainerId);
                }
                continue;
            }
            if (liveCid is not null)
            {
                if (!string.Equals(liveCid, entry.ContainerId, StringComparison.OrdinalIgnoreCase)) registry.UpdateContainerId(entry.Ip, entry.ContainerId, liveCid);
                continue;
            }

            if (cidByName.TryGetValue(entry.ContainerId, out string? actualCid))
            {
                registry.UpdateContainerId(entry.Ip, entry.ContainerId, actualCid);
                continue;
            }

            logger.LogInformation("Reconcile: Deregistering terminated container '{ContainerId}' (IP: {Ip}).", entry.ContainerId, entry.Ip);
            transientWorkloadDenials.TryRemove(entry.Ip, out _);
            registry.Deregister(entry.Ip, entry.ContainerId);
        }
        return cleanupComplete;
    }

    private async Task RetryWorkloadSnapshotAsync(IWorkloadRegistry registry, CancellationToken cancellationToken)
    {
        try
        {
            if (!await ReconcileWorkloadRegistryAsync(registry, cancellationToken).ConfigureAwait(false)) return;
            string sourceHash = await ReadRenderedReconcileConfigurationHashAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(sourceHash) || string.IsNullOrWhiteSpace(expectedBoundaryNftSourceHash)) return;
            if (!string.Equals(sourceHash, expectedBoundaryNftSourceHash, StringComparison.Ordinal))
            {
                await LatchTopologyIncidentAndContainAsync(registry, "rendered egress configuration drift during snapshot retry", cancellationToken).ConfigureAwait(false);
                return;
            }
            if (!await IsEgressPodHealthyAsync(cancellationToken).ConfigureAwait(false))
            {
                workloadSnapshotRetryPending = false;
                await RecoverEgressTopologyAsync(registry, cancellationToken).ConfigureAwait(false);
                return;
            }
            if (OperatingSystem.IsLinux() || !string.IsNullOrWhiteSpace(windowsPodmanMachineName))
            {
                string liveHash = await ReadBoundaryRulesetHashAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(liveHash) || string.IsNullOrWhiteSpace(expectedBoundaryRulesetHash)) return;
                if (!string.Equals(liveHash, expectedBoundaryRulesetHash, StringComparison.Ordinal))
                {
                    await LatchTopologyIncidentAndContainAsync(registry, "live boundary ruleset drift during snapshot retry", cancellationToken).ConfigureAwait(false);
                    return;
                }
            }
            TryCompleteTransientRecovery();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Workload snapshot retry could not verify the egress topology; launch admission remains closed.");
        }
    }

    private async Task RecoverEgressTopologyAsync(IWorkloadRegistry registry, CancellationToken cancellationToken)
    {
        StartupGateStateChange? gateChange = null;
        lock (gateLock)
        {
            if (!IsStartupGateOpen && !transientRecoveryPending)
            {
                return;
            }

            if (isStartupGateOpen)
            {
                transientRecoveryPending = true;
                gateChange = UpdateStartupGateStateUnderLock(isOpen: false);
            }
        }
        if (gateChange is StartupGateStateChange change)
        {
            CompleteStartupGateStateChange(change);
        }

        try
        {
            if (!await ReconcileWorkloadRegistryAsync(registry, cancellationToken).ConfigureAwait(false))
            {
                logger.LogWarning("Quarantine cleanup is incomplete; keeping launch admission closed for retry.");
                return;
            }
            await RemoveEgressPodIfPresentAsync(cancellationToken).ConfigureAwait(false);

            bool isWindowsHostNativeDev = OperatingSystem.IsWindows();
            await EnsureEgressPodAsync(
                isWindowsHostNativeDev,
                decisionEndpointIp: windowsDecisionEndpointIp,
                includeLanGrants: true,
                envoyCertificateDirectory: windowsEnvoyCertificateDirectory,
                cancellationToken).ConfigureAwait(false);

            if (OperatingSystem.IsLinux() || isWindowsHostNativeDev || !string.IsNullOrWhiteSpace(windowsPodmanMachineName))
            {
                string renderedHash = await ReadRenderedBoundaryRulesetHashAsync(cancellationToken).ConfigureAwait(false);
                string liveHash = await ReadBoundaryRulesetHashAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(renderedHash)
                    && !string.IsNullOrWhiteSpace(liveHash)
                    && !string.Equals(renderedHash, liveHash, StringComparison.Ordinal))
                {
                    await LatchTopologyIncidentAndContainAsync(registry, "live boundary ruleset drift during recovery", cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (string.IsNullOrWhiteSpace(renderedHash) || string.IsNullOrWhiteSpace(liveHash))
                {
                    throw new InvalidOperationException("Boundary nft ruleset verification returned an empty hash during recovery.");
                }

                expectedBoundaryRulesetHash = renderedHash;
            }

            if (startupCanaryRetryPending)
            {
                StartupGateStateChange retryGateChange;
                lock (gateLock)
                {
                    transientRecoveryPending = false;
                    retryGateChange = UpdateStartupGateStateUnderLock(isOpen: false);
                }
                CompleteStartupGateStateChange(retryGateChange);
                logger.LogInformation("Egress topology was recovered while startup canary retry is pending; the launch gate remains closed for full startup revalidation.");
                return;
            }

            if (TryCompleteTransientRecovery())
            {
                logger.LogInformation("Egress pod recovery completed; startup gate reopened.");
            }
            else
            {
                logger.LogInformation("Egress pod recovery completed, but launch admission was explicitly closed while recovery was in progress; the gate remains closed.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            StartupGateStateChange failedGateChange;
            lock (gateLock)
            {
                failedGateChange = UpdateStartupGateStateUnderLock(isOpen: false);
            }
            CompleteStartupGateStateChange(failedGateChange);

            logger.LogError(ex, "Egress pod recovery failed; startup gate remains closed and reconcile will retry.");
        }
    }

    private bool TryCompleteTransientRecovery()
    {
        StartupGateStateChange gateChange;
        lock (gateLock)
        {
            if (!transientRecoveryPending
                || topologyIncidentLatched)
            {
                return false;
            }

            transientRecoveryPending = false;
            workloadSnapshotRetryPending = false;
            gateChange = UpdateStartupGateStateUnderLock(isOpen: true);
        }

        CompleteStartupGateStateChange(gateChange);
        return true;
    }

    private async Task RemoveEgressPodIfPresentAsync(CancellationToken cancellationToken)
    {
        PodmanCommandResult exists = await podmanRunner
            .RunAsync(["pod", "exists", EgressPodName], 10, cancellationToken)
            .ConfigureAwait(false);
        if (exists.ExitCode == 1)
        {
            return;
        }

        if (exists.ExitCode != 0)
        {
            throw new InvalidOperationException($"Could not determine whether egress pod '{EgressPodName}' exists: {exists.StdErr}");
        }

        PodmanCommandResult removed = await podmanRunner
            .RunAsync(["pod", "rm", "-f", EgressPodName], 30, cancellationToken)
            .ConfigureAwait(false);
        if (removed.ExitCode == 0)
        {
            return;
        }

        PodmanCommandResult existsAfterRemoval = await podmanRunner
            .RunAsync(["pod", "exists", EgressPodName], 10, cancellationToken)
            .ConfigureAwait(false);
        if (existsAfterRemoval.ExitCode == 1)
        {
            return;
        }

        throw new InvalidOperationException($"Failed to remove egress pod '{EgressPodName}' before recovery (rc {removed.ExitCode}): {removed.StdErr}");
    }

    private async Task LatchTopologyIncidentAndContainAsync(
        IWorkloadRegistry registry,
        string incident,
        CancellationToken cancellationToken)
    {
        // Deny new launches and Envoy authorization before the first Podman await.
        topologyIncidentLatched = true;
        CloseLaunchAdmission();
        topologyContainmentComplete = false;
        logger.LogCritical("Topology incident '{Incident}' latched; stopping workloads on {Network}.", incident, WorkloadNetworkName);
        await ContainWorkloadNetworkMembersAsync(registry, incident, cancellationToken).ConfigureAwait(false);
    }

    private async Task ContainWorkloadNetworkMembersAsync(
        IWorkloadRegistry registry,
        string incident,
        CancellationToken cancellationToken)
    {
        int activeLaunchesAtScanStart = ActiveLaunchLeaseCount;
        HashSet<string> candidateIds = new(StringComparer.OrdinalIgnoreCase);
        PodmanCommandResult initialList = await ListWorkloadNetworkContainersAsync(cancellationToken).ConfigureAwait(false);
        bool initialListComplete = TryReadWorkloadNetworkContainers(initialList, out List<WorkloadNetworkContainer> initialContainers);
        HashSet<string> enumeratedIdentities = new(StringComparer.OrdinalIgnoreCase);

        foreach (WorkloadNetworkContainer container in initialContainers)
        {
            enumeratedIdentities.Add(container.Id);
            foreach (string name in container.Names)
            {
                enumeratedIdentities.Add(name);
            }

            if (!knownInfraContainerIds.Contains(container.Id) && !IsKnownStoppedContainerState(container.State))
            {
                candidateIds.Add(container.Id);
            }
        }

        // Registry IDs and the network scan are independent evidence sources. Union them so a
        // valid-but-empty filtered Podman response cannot hide a registered running workload.
        foreach (RegistryEntry entry in registry.GetAll())
        {
            if (knownInfraContainerIds.Contains(entry.ContainerId))
            {
                continue;
            }

            if (!enumeratedIdentities.Contains(entry.ContainerId)
                || !initialListComplete && !IsKnownStoppedContainerState(
                    initialContainers.FirstOrDefault(container =>
                        string.Equals(container.Id, entry.ContainerId, StringComparison.OrdinalIgnoreCase)
                        || container.Names.Contains(entry.ContainerId, StringComparer.OrdinalIgnoreCase))?.State ?? string.Empty))
            {
                candidateIds.Add(entry.ContainerId);
            }
        }

        if (!initialListComplete)
        {
            logger.LogError(
                "Workload network enumeration failed or was malformed while containing topology incident '{Incident}'; all known registered workloads will be included and the incident will be retried.",
                incident);
        }

        HashSet<string> confirmedInactive = new(StringComparer.OrdinalIgnoreCase);
        foreach (string containerId in candidateIds)
        {
            if (await StopKillOrInspectForTopologyIncidentAsync(containerId, incident, cancellationToken).ConfigureAwait(false))
            {
                confirmedInactive.Add(containerId);
            }
        }

        PodmanCommandResult finalList = await ListWorkloadNetworkContainersAsync(cancellationToken).ConfigureAwait(false);
        bool finalListComplete = TryReadWorkloadNetworkContainers(finalList, out List<WorkloadNetworkContainer> finalContainers);
        string[] remaining = finalContainers
            .Where(container => !knownInfraContainerIds.Contains(container.Id) && !IsKnownStoppedContainerState(container.State))
            .Select(static container => container.Id)
            .ToArray();
        foreach (string containerId in candidateIds)
        {
            if (finalContainers.Any(container =>
                (string.Equals(container.Id, containerId, StringComparison.OrdinalIgnoreCase)
                    || container.Names.Contains(containerId, StringComparer.OrdinalIgnoreCase))
                && IsKnownStoppedContainerState(container.State)))
            {
                confirmedInactive.Add(containerId);
            }
        }

        string[] unconfirmedCandidates = candidateIds
            .Where(containerId => !confirmedInactive.Contains(containerId))
            .ToArray();

        int activeLaunches = ActiveLaunchLeaseCount;
        if (!initialListComplete || !finalListComplete || remaining.Length > 0 || unconfirmedCandidates.Length > 0 || activeLaunchesAtScanStart > 0 || activeLaunches > 0)
        {
            topologyContainmentComplete = false;
            logger.LogCritical(
                "Topology incident '{Incident}' remains unresolved; enumeration complete={EnumerationComplete}, remaining workload containers={Remaining}, unconfirmed candidates={Unconfirmed}, active launch leases at scan start={ActiveLaunchLeasesAtScanStart}, active launch leases at scan end={ActiveLaunchLeasesAtScanEnd}. The launch/auth gate remains closed and reconcile will retry.",
                incident,
                initialListComplete && finalListComplete,
                remaining.Length == 0 ? "none observed" : string.Join(",", remaining),
                unconfirmedCandidates.Length == 0 ? "none" : string.Join(",", unconfirmedCandidates),
                activeLaunchesAtScanStart,
                activeLaunches);
            return;
        }

        topologyContainmentComplete = true;
        logger.LogCritical(
            "Topology incident '{Incident}' remains latched. A complete workload-network scan found no running workload containers and every candidate was confirmed inactive; the gate remains closed and future reconcile ticks will rescan.",
            incident);
    }

    private async Task<bool> StopKillOrInspectForTopologyIncidentAsync(
        string containerId,
        string incident,
        CancellationToken cancellationToken)
    {
        bool stopped = false;
        try
        {
            PodmanCommandResult stop = await podmanRunner
                .RunAsync(["stop", "-t", "2", containerId], 10, cancellationToken)
                .ConfigureAwait(false);
            stopped = stop.ExitCode == 0;
            if (!stopped)
            {
                logger.LogWarning(
                    "Topology incident '{Incident}': stop failed for workload '{ContainerId}' (rc {ExitCode}); trying kill.",
                    incident,
                    containerId,
                    stop.ExitCode);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Topology incident '{Incident}': stop failed for workload '{ContainerId}'; trying kill.", incident, containerId);
        }

        if (stopped)
        {
            return true;
        }

        try
        {
            PodmanCommandResult kill = await podmanRunner
                .RunAsync(["kill", containerId], 10, cancellationToken)
                .ConfigureAwait(false);
            if (kill.ExitCode == 0)
            {
                return true;
            }

            logger.LogCritical(
                "Topology incident '{Incident}': stop and kill failed for workload '{ContainerId}' (kill rc {ExitCode}); registry identity is retained and reconcile will retry.",
                incident,
                containerId,
                kill.ExitCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogCritical(ex,
                "Topology incident '{Incident}': stop and kill failed for workload '{ContainerId}'; registry identity is retained and reconcile will retry.",
                incident,
                containerId);
        }

        PodmanCommandResult inspect;
        try
        {
            inspect = await podmanRunner
                .RunAsync(["inspect", containerId, "--format", "json"], 10, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogCritical(ex,
                "Topology incident '{Incident}': cannot inspect workload '{ContainerId}' after stop and kill failures; inactive state is unconfirmed.",
                incident,
                containerId);
            return false;
        }

        if (TryReadConfirmedInactiveInspect(inspect))
        {
            logger.LogWarning(
                "Topology incident '{Incident}': workload '{ContainerId}' did not stop on command, but Podman inspect confirms it is inactive.",
                incident,
                containerId);
            return true;
        }

        logger.LogCritical(
            "Topology incident '{Incident}': workload '{ContainerId}' remains unconfirmed after stop, kill, and inspect; reconcile will retry.",
            incident,
            containerId);
        return false;
    }

    private static bool TryReadConfirmedInactiveInspect(PodmanCommandResult result)
    {
        if (result.ExitCode != 0)
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.StdOut);
            JsonElement inspected = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.GetArrayLength() == 1 ? document.RootElement[0] : default
                : document.RootElement;
            if (inspected.ValueKind != JsonValueKind.Object
                || !inspected.TryGetProperty("State", out JsonElement state)
                || state.ValueKind != JsonValueKind.Object
                || !state.TryGetProperty("Status", out JsonElement status)
                || status.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            return IsKnownStoppedContainerState(status.GetString() ?? string.Empty);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private bool TryReadWorkloadNetworkContainers(PodmanCommandResult result, out List<WorkloadNetworkContainer> containers)
    {
        containers = [];
        if (result.ExitCode != 0)
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(result.StdOut);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            bool complete = true;
            foreach (JsonElement item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    complete = false;
                    continue;
                }

                string id = item.TryGetProperty("Id", out JsonElement idElement) && idElement.ValueKind == JsonValueKind.String
                    ? idElement.GetString() ?? string.Empty
                    : string.Empty;
                string state = item.TryGetProperty("State", out JsonElement stateElement) && stateElement.ValueKind == JsonValueKind.String
                    ? stateElement.GetString() ?? string.Empty
                    : string.Empty;
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(state))
                {
                    complete = false;
                }

                if (!string.IsNullOrWhiteSpace(id))
                {
                    List<string> names = [];
                    if (item.TryGetProperty("Names", out JsonElement namesElement))
                    {
                        if (namesElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (JsonElement nameElement in namesElement.EnumerateArray())
                            {
                                if (nameElement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(nameElement.GetString()))
                                {
                                    complete = false;
                                    continue;
                                }

                                names.Add(nameElement.GetString()!.TrimStart('/'));
                            }
                        }
                        else if (namesElement.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(namesElement.GetString()))
                        {
                            names.Add(namesElement.GetString()!.TrimStart('/'));
                        }
                        else
                        {
                            complete = false;
                        }
                    }

                    containers.Add(new WorkloadNetworkContainer(id, state, names));
                }
            }

            return complete;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<PodmanCommandResult> ListWorkloadNetworkContainersAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await podmanRunner
                .RunAsync(["ps", "-a", "--filter", $"network={WorkloadNetworkName}", "--format", "json"], 30, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to enumerate Podman containers on workload network '{Network}'.", WorkloadNetworkName);
            return new PodmanCommandResult(1, string.Empty, ex.Message, ProcessTerminationReason.NonZeroExit);
        }
    }

    private static bool IsKnownStoppedContainerState(string state)
        => state.Equals("exited", StringComparison.OrdinalIgnoreCase)
            || state.Equals("stopped", StringComparison.OrdinalIgnoreCase)
            || state.Equals("created", StringComparison.OrdinalIgnoreCase)
            || state.Equals("configured", StringComparison.OrdinalIgnoreCase)
            || state.Equals("initialized", StringComparison.OrdinalIgnoreCase);

    private sealed record WorkloadNetworkContainer(string Id, string State, IReadOnlyList<string> Names);

    private static string GetStringProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out JsonElement prop))
        {
            return prop.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    private async Task<string> ReadRenderedReconcileConfigurationHashAsync(CancellationToken cancellationToken)
    {
        string boundaryPath = Path.Combine(stateDirectory, "nac", "boundary", "boundary.nft");
        string envoyPath = Path.Combine(stateDirectory, "nac", "envoy", "envoy.yaml");
        string corefilePath = Path.Combine(stateDirectory, "nac", "dns", "Corefile");
        string boundaryNft = await File.ReadAllTextAsync(boundaryPath, cancellationToken).ConfigureAwait(false);
        string envoyConfig = await File.ReadAllTextAsync(envoyPath, cancellationToken).ConfigureAwait(false);
        string corefile = await File.ReadAllTextAsync(corefilePath, cancellationToken).ConfigureAwait(false);
        return ComputeReconcileConfigurationHash(boundaryNft, envoyConfig, corefile);
    }

    private async Task<bool> ExistingReconcileConfigurationMatchesAsync(
        string boundaryNft,
        string envoyConfig,
        string corefile,
        CancellationToken cancellationToken)
    {
        string nacDirectory = Path.Combine(stateDirectory, "nac");
        string boundaryPath = Path.Combine(nacDirectory, "boundary", "boundary.nft");
        string envoyPath = Path.Combine(nacDirectory, "envoy", "envoy.yaml");
        string corefilePath = Path.Combine(nacDirectory, "dns", "Corefile");
        if (!File.Exists(boundaryPath) || !File.Exists(envoyPath) || !File.Exists(corefilePath))
        {
            return false;
        }

        string existingBoundary = await File.ReadAllTextAsync(boundaryPath, cancellationToken).ConfigureAwait(false);
        string existingEnvoy = await File.ReadAllTextAsync(envoyPath, cancellationToken).ConfigureAwait(false);
        string existingCorefile = await File.ReadAllTextAsync(corefilePath, cancellationToken).ConfigureAwait(false);
        return string.Equals(
            ComputeReconcileConfigurationHash(existingBoundary, existingEnvoy, existingCorefile),
            ComputeReconcileConfigurationHash(boundaryNft, envoyConfig, corefile),
            StringComparison.Ordinal);
    }

    private async Task EnsureExistingEgressPodUsesRenderedConfigAsync(
        string boundaryNft,
        string envoyConfig,
        string corefile,
        bool isWindowsHostNativeDev,
        CancellationToken cancellationToken)
    {
        if (isWindowsHostNativeDev
            || await ExistingReconcileConfigurationMatchesAsync(boundaryNft, envoyConfig, corefile, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        // Existing Envoy/CoreDNS processes keep their loaded configuration in memory.
        // Recreate the pod before replacing the bind-mounted files so startup loads the
        // same bytes represented by the expected reconcile hash.
        await RemoveEgressPodIfPresentAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource<object?>? completion = null;
        Task task;
        lock (disposeLock)
        {
            if (disposeTask is null)
            {
                completion = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                disposeTask = completion.Task;
            }

            task = disposeTask;
        }

        if (completion is not null)
        {
            _ = CompleteDisposalAsync(completion);
        }

        return new ValueTask(task);
    }

    private async Task CompleteDisposalAsync(TaskCompletionSource<object?> completion)
    {
        try
        {
            await DisposeCoreAsync().ConfigureAwait(false);
            completion.TrySetResult(null);
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
    }

    private async Task DisposeCoreAsync()
    {
        CloseLaunchAdmission();

        // The hosted-service lifecycle and the DI container both dispose this singleton.
        // Claim the cancellation source once so repeated or concurrent disposal is safe.
        CancellationTokenSource? cts = reconcileCts;
        reconcileCts = null;
        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
            Task? task = reconcileTask;
            reconcileTask = null;
            if (task is not null)
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch
                {
                    // Ignore task cancellation
                }
            }

            cts.Dispose();
        }
    }
}
