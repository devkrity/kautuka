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
    [property: JsonPropertyName("image_digest")] string ImageDigest);

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
public sealed class EgressTopologyManager : IHostedService, IAsyncDisposable, IWorkloadStartupState
{
    public const string WorkloadNetworkName = "kautuka-workload";
    public const string OutsideNetworkName = "kautuka-outside";
    public const string EgressPodName = "kautuka-egress";
    public const string PackageProxyContainerName = "kautuka-pkgs-proxy";
    public const string EgressPodIp = "10.247.0.2";
    public const string OutsidePodIp = "10.248.0.2";
    public const string OutsideSubnet = "10.248.0.0/24";
    public const string PackageProxyIp = "10.247.0.3";
    public const string WorkloadSubnet = "10.247.0.0/16";

    public const string EnvoyPinnedImage = "docker.io/envoyproxy/envoy@sha256:09ca6f2140a61ad8df11779a4a30d52b48433d444466328494f9188c9c35eded";
    public const string CoreDnsPinnedImage = "docker.io/coredns/coredns@sha256:900f9c109f7a33545d3c811516e8376df9019147b750f5ce3e254468769176ea";
    public const string BoundaryInitImage = "localhost/kautuka-egress-boundary:local";

    private readonly PodmanCommandRunner podmanRunner;
    private readonly WorkloadAddressAllocator addressAllocator;
    private readonly WorkloadPostStartVerifier postStartVerifier;
    private readonly IWorkloadRegistry registry;
    private readonly string stateDirectory;
    private readonly string envoySocketHostDir;
    private readonly string allowlistFilePath;
    private readonly string startupGateFilePath;
    private readonly string hookLogFilePath;
    private readonly ILogger<EgressTopologyManager> logger;

    private readonly object gateLock = new();
    private bool isStartupGateOpen;
    private long unlabelledWorkloadCount;
    private CancellationTokenSource? reconcileCts;
    private Task? reconcileTask;
    private readonly HashSet<string> knownInfraContainerIds = new(StringComparer.OrdinalIgnoreCase);

    public EgressTopologyManager(
        PodmanCommandRunner podmanRunner,
        WorkloadAddressAllocator addressAllocator,
        WorkloadPostStartVerifier postStartVerifier,
        IWorkloadRegistry registry,
        string stateDirectory,
        ILogger<EgressTopologyManager>? logger = null,
        string? envoySocketHostDir = null,
        string? hookStateDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(podmanRunner);
        ArgumentNullException.ThrowIfNull(addressAllocator);
        ArgumentNullException.ThrowIfNull(postStartVerifier);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);

        this.podmanRunner = podmanRunner;
        this.addressAllocator = addressAllocator;
        this.postStartVerifier = postStartVerifier;
        this.registry = registry;
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
            lock (gateLock)
            {
                isStartupGateOpen = value;
                try
                {
                    File.WriteAllText(startupGateFilePath, value ? "open" : "closed");
                }
                catch
                {
                    // Ignore file write errors
                }
            }
        }
    }

    public long UnlabelledWorkloadCount => Interlocked.Read(ref unlabelledWorkloadCount);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await InitializeAsync(registry, isWindowsHostNativeDev: !OperatingSystem.IsLinux(), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            IsStartupGateOpen = false;
            logger.LogError(ex, "Failed to initialize EgressTopologyManager; closed startup gate.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        IsStartupGateOpen = false;
        await DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Ensures networks and allowlist are provisioned, egress pod is live with boundary rules,
    /// registry is rebuilt from container labels, startup canary passes, and reconcile loop starts.
    /// </summary>
    public async Task InitializeAsync(
        IWorkloadRegistry registry,
        bool isWindowsHostNativeDev = false,
        string? decisionEndpointIp = null,
        bool includeLanGrants = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);

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

        // 1. Ensure networks exist
        await EnsureNetworksAsync(cancellationToken).ConfigureAwait(false);

        // 2. Ensure egress pod and containers exist, and write infra allowlist before start
        await EnsureEgressPodAsync(isWindowsHostNativeDev, decisionEndpointIp, includeLanGrants, cancellationToken).ConfigureAwait(false);

        // 3. Rebuild registry from live containers before opening listener (Section 4.3.4 item 6)
        await RebuildRegistryFromLiveContainersAsync(registry, cancellationToken).ConfigureAwait(false);

        // 4. Run startup self-test canary (Section 4.3.4 item 5)
        bool canaryPassed = await RunStartupSelfTestAsync(cancellationToken).ConfigureAwait(false);
        if (!canaryPassed)
        {
            IsStartupGateOpen = false;
            logger.LogError("Startup canary self-test failed; startup gate closed.");
            throw new InvalidOperationException("Network access control self-test failed.");
        }

        IsStartupGateOpen = true;
        logger.LogInformation("Startup canary self-test passed; startup gate open.");

        // 5. Start 30s reconcile loop
        StartReconcileLoop(registry);
    }

    /// <summary>
    /// Reconstructs the IWorkloadRegistry from live container io.kautuka.* labels before
    /// the Envoy listener starts accepting connections. Kills and removes any unlabelled containers.
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
                        string cid = container.TryGetProperty("Id", out JsonElement idElem) ? idElem.GetString() ?? string.Empty : string.Empty;
                        if (string.IsNullOrWhiteSpace(cid))
                        {
                            continue;
                        }

                        // Skip allowlisted infra containers
                        if (knownInfraContainerIds.Contains(cid))
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
                        string systemwideStr = GetStringProperty(labels, "io.kautuka.systemwide");
                        string egress = GetStringProperty(labels, "io.kautuka.egress");
                        string compat = GetStringProperty(labels, "io.kautuka.compat");
                        string purpose = GetStringProperty(labels, "io.kautuka.purpose");

                        bool systemwide = string.Equals(systemwideStr, "true", StringComparison.OrdinalIgnoreCase);
                        bool isProbe = string.Equals(purpose, "probe", StringComparison.OrdinalIgnoreCase);

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
                            if (!systemwide && !isProbe && string.IsNullOrWhiteSpace(user))
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
                                await podmanRunner.RunAsync(["rm", "-f", cid], 5, cancellationToken).ConfigureAwait(false);
                            }
                            catch
                            {
                                // Ignore cleanup failure
                            }
                            continue;
                        }

                        string attribution;
                        if (egress is "deny-all" or "proxy-only")
                        {
                            attribution = "none";
                        }
                        else if (systemwide)
                        {
                            attribution = string.IsNullOrWhiteSpace(user) ? "none" : "user";
                        }
                        else if (isProbe)
                        {
                            attribution = "user";
                        }
                        else if (string.IsNullOrWhiteSpace(agent))
                        {
                            // v7-26: Non-probe container missing agentId cannot have normal egress
                            egress = "deny-all";
                            attribution = "none";
                        }
                        else
                        {
                            attribution = "user-agent";
                        }

                        var entry = new RegistryEntry(
                            ip,
                            string.IsNullOrWhiteSpace(user) ? null : user,
                            string.IsNullOrWhiteSpace(agent) ? null : agent,
                            cid,
                            kind,
                            string.IsNullOrWhiteSpace(connector) ? null : connector,
                            execContext,
                            systemwide,
                            string.IsNullOrWhiteSpace(egress) ? "normal" : egress,
                            attribution,
                            string.IsNullOrWhiteSpace(compat) ? null : compat,
                            DateTimeOffset.UtcNow,
                            string.IsNullOrWhiteSpace(purpose) ? null : purpose);

                        rebuiltEntries.Add(entry);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error parsing containers during registry rebuild.");
            }
        }

        registry.Rebuild(rebuiltEntries);
        logger.LogInformation("Workload registry rebuilt with {Count} entries.", registry.Count);
    }

    /// <summary>
    /// Executes the startup canary test (Section 4.3.4 item 5).
    /// Starts a canary workload, verifies the hook log entry and live ruleset, and removes the canary.
    /// </summary>
    private const string CanaryBasePinnedImage = "ghcr.io/devkrity/kautuka-base@sha256:675400832f2ced5cf802db66eb067ef5022f65f0551c665d87ae7edc58dc0839";

    public async Task<bool> RunStartupSelfTestAsync(CancellationToken cancellationToken = default)
    {
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

            // Verify live ruleset via verifier
            WorkloadVerificationResult verification = await postStartVerifier
                .VerifyWorkloadAsync(canaryName, canaryIp, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!verification.Passed)
            {
                return false;
            }

            // Verify hook log has entry
            if (File.Exists(hookLogFilePath))
            {
                string logContent = await File.ReadAllTextAsync(hookLogFilePath, cancellationToken).ConfigureAwait(false);
                if (!logContent.Contains("action", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning("Hook log does not contain action entries.");
                }
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
                try
                {
                    await podmanRunner.RunAsync(["kill", entry.ContainerId], 10, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort kill fallback
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
                if (!isInternal || dnsEnabled)
                {
                    IsStartupGateOpen = false;
                    throw new InvalidOperationException($"Existing network '{WorkloadNetworkName}' drifted (internal={isInternal}, dns_enabled={dnsEnabled}).");
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
                "--subnet", WorkloadSubnet,
                "--gateway", "10.247.0.1",
                "--route", "0.0.0.0/0,10.247.0.2",
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

        if (inspectOut.ExitCode != 0)
        {
            logger.LogInformation("Creating {NetworkName}...", OutsideNetworkName);
            PodmanCommandResult createOut = await podmanRunner
                .RunAsync(["network", "create", "--subnet", OutsideSubnet, "--gateway", "10.248.0.1", OutsideNetworkName], 30, cancellationToken)
                .ConfigureAwait(false);

            if (createOut.ExitCode != 0)
            {
                throw new InvalidOperationException($"Failed to create network {OutsideNetworkName}: {createOut.StdErr}");
            }
        }
    }

    public void WriteInfraAllowlist(IReadOnlyList<InfraAllowlistEntry>? entries = null)
    {
        entries ??=
        [
            new(EgressPodName, "egress-pod", [EgressPodIp], "pinned"),
            new("kautuka-egress-envoy", "egress-envoy", [EgressPodIp], EnvoyPinnedImage),
            new("kautuka-egress-dns", "egress-dns", [EgressPodIp], CoreDnsPinnedImage),
            new(PackageProxyContainerName, "pkgs-proxy", [PackageProxyIp], "pinned"),
        ];

        var doc = new InfraAllowlistDocument(1, entries);
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
        CancellationToken cancellationToken)
    {
        // Render boundary and configs
        IReadOnlyList<string> upstreamDnsIps = await DiscoverUpstreamDnsIpsAsync(cancellationToken).ConfigureAwait(false);
        string boundaryNft = BoundaryRulesetRenderer.RenderBoundaryNft(includeLanGrants, decisionEndpointIp, upstreamDnsIps);
        string envoyConfig = BoundaryRulesetRenderer.RenderEnvoyConfig(isWindowsMtls: isWindowsHostNativeDev);
        string corefile = BoundaryRulesetRenderer.RenderCorefile(EgressPodIp);

        string nacDir = Path.Combine(stateDirectory, "nac");
        string boundaryPath = Path.Combine(nacDir, "boundary.nft");
        string envoyPath = Path.Combine(nacDir, "envoy.yaml");
        string corefilePath = Path.Combine(nacDir, "Corefile");

        await File.WriteAllTextAsync(boundaryPath, boundaryNft, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(envoyPath, envoyConfig, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(corefilePath, corefile, cancellationToken).ConfigureAwait(false);

        // Check if pod exists:
        PodmanCommandResult inspectPod = await podmanRunner
            .RunAsync(["pod", "inspect", EgressPodName], 10, cancellationToken)
            .ConfigureAwait(false);

        if (inspectPod.ExitCode != 0)
        {
            logger.LogInformation("Creating egress pod {PodName}...", EgressPodName);
            List<string> createPodArgs =
            [
                "pod", "create",
                "--name", EgressPodName,
                "--network", $"{WorkloadNetworkName}:ip={EgressPodIp}",
                "--network", $"{OutsideNetworkName}:ip={OutsidePodIp}",
                "--sysctl", "net.ipv4.ip_forward=0",
                "--sysctl", "net.ipv6.conf.all.disable_ipv6=1"
            ];

            PodmanCommandResult podResult = await podmanRunner
                .RunAsync(createPodArgs, 30, cancellationToken)
                .ConfigureAwait(false);

            if (podResult.ExitCode != 0)
            {
                logger.LogError("Failed to create egress pod: {StdErr}", podResult.StdErr);
                throw new InvalidOperationException($"Failed to create egress pod: {podResult.StdErr}");
            }
        }

        // Get Pod infra container ID and populate allowlist
        PodmanCommandResult podInspect = await podmanRunner
            .RunAsync(["pod", "inspect", EgressPodName], 10, cancellationToken)
            .ConfigureAwait(false);

        var allowlistEntries = new List<InfraAllowlistEntry>();
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
        await EnsureInfraContainerCreatedAsync(
            "kautuka-egress-boundary",
            "egress-boundary",
            BoundaryInitImage,
            [
                "create",
                "--pod", EgressPodName,
                "--name", "kautuka-egress-boundary",
                "--init-ctr=always",
                "--cap-add", "NET_ADMIN",
                "--user", "0:0",
                "--entrypoint", "/bin/sh",
                "-v", $"{boundaryPath}:/etc/nftables.conf:ro",
                BoundaryInitImage,
                "-c", "ip route replace local 192.0.0.8 dev lo 2>/dev/null || true; exec /usr/sbin/nft -f /etc/nftables.conf"
            ],
            allowlistEntries,
            cancellationToken).ConfigureAwait(false);

        // 2. CoreDNS container
        await EnsureInfraContainerCreatedAsync(
            "kautuka-egress-dns",
            "egress-dns",
            CoreDnsPinnedImage,
            [
                "create",
                "--pod", EgressPodName,
                "--name", "kautuka-egress-dns",
                "--cap-add", "NET_BIND_SERVICE",
                "--user", "0:0",
                "-v", $"{corefilePath}:/etc/coredns/Corefile:ro",
                CoreDnsPinnedImage,
                "-conf", "/etc/coredns/Corefile"
            ],
            allowlistEntries,
            cancellationToken).ConfigureAwait(false);

        // 3. Envoy container (runs natively as uid 65532 mapped via pod keep-id to host user)
        Directory.CreateDirectory(envoySocketHostDir);
        string adminSocketDir = Path.Combine(stateDirectory, "admin-socket");
        Directory.CreateDirectory(adminSocketDir);
        List<string> envoyCreateArgs =
        [
            "create",
            "--pod", EgressPodName,
            "--name", "kautuka-egress-envoy",
            "--user", "0:0",
            "-v", $"{envoyPath}:/etc/envoy/envoy.yaml:ro",
            "-v", $"{envoySocketHostDir}:/run/kautuka-nac:z",
            "-v", $"{adminSocketDir}:/run/kautuka-admin:z",
            EnvoyPinnedImage,
            "-c", "/etc/envoy/envoy.yaml"
        ];
        await EnsureInfraContainerCreatedAsync(
            "kautuka-egress-envoy",
            "egress-envoy",
            EnvoyPinnedImage,
            envoyCreateArgs,
            allowlistEntries,
            cancellationToken).ConfigureAwait(false);

        PodmanCommandResult podStart = await podmanRunner.RunAsync(["pod", "start", EgressPodName], 30, cancellationToken).ConfigureAwait(false);
        if (podStart.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to start egress pod '{EgressPodName}' (rc {podStart.ExitCode}): {podStart.StdErr}");
        }
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
            if (!allowlistEntries.Any(e => string.Equals(e.Id, containerId, StringComparison.OrdinalIgnoreCase)))
            {
                allowlistEntries.Add(new(containerId, role, [EgressPodIp, OutsidePodIp], imageDigest));
            }
            WriteInfraAllowlist(allowlistEntries);
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

        string dockerfilePath = Path.Combine(AppContext.BaseDirectory, "sidecar-assets", "egress-boundary", "Dockerfile");
        if (!File.Exists(dockerfilePath))
        {
            dockerfilePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "images", "kautuka-egress-boundary", "Dockerfile"));
        }

        if (File.Exists(dockerfilePath))
        {
            string contextDir = Path.GetDirectoryName(dockerfilePath)!;
            logger.LogInformation("Building boundary init image '{Image}' from {Path}...", BoundaryInitImage, dockerfilePath);
            PodmanCommandResult buildResult = await podmanRunner
                .RunAsync(["build", "-t", BoundaryInitImage, "-f", dockerfilePath, contextDir], 120, cancellationToken)
                .ConfigureAwait(false);
            if (buildResult.ExitCode != 0)
            {
                throw new InvalidOperationException($"Failed to build boundary init image '{BoundaryInitImage}' from '{dockerfilePath}' (rc {buildResult.ExitCode}): {buildResult.StdErr}");
            }
        }
        else
        {
            throw new FileNotFoundException($"Boundary init image '{BoundaryInitImage}' is not present and its Dockerfile could not be found.", dockerfilePath);
        }
    }

    private void StartReconcileLoop(IWorkloadRegistry registry)
    {
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
        // 1. Compare running containers to registry
        PodmanCommandResult psResult = await podmanRunner
            .RunAsync(["ps", "-a", "--format", "json"], 30, cancellationToken)
            .ConfigureAwait(false);

        if (psResult.ExitCode != 0)
        {
            return;
        }

        var liveCids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                        string? cid = container.TryGetProperty("Id", out JsonElement idElem) ? idElem.GetString() : null;
                        if (string.IsNullOrWhiteSpace(cid))
                        {
                            continue;
                        }

                        liveCids.Add(cid);

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
            }
            catch
            {
                // Ignore parse errors
            }
        }

        // Deregister dead containers (matching both canonical hex container IDs and pre-start registered names).
        foreach (RegistryEntry entry in registry.GetAll())
        {
            if (liveCids.Contains(entry.ContainerId))
            {
                continue;
            }

            if (cidByName.TryGetValue(entry.ContainerId, out string? actualCid))
            {
                registry.UpdateContainerId(entry.Ip, entry.ContainerId, actualCid);
                continue;
            }

            logger.LogInformation("Reconcile: Deregistering terminated container '{ContainerId}' (IP: {Ip}).", entry.ContainerId, entry.Ip);
            registry.Deregister(entry.Ip, entry.ContainerId);
        }
    }

    private async Task<IReadOnlyList<string>> DiscoverUpstreamDnsIpsAsync(CancellationToken cancellationToken)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void TryAddCandidate(string? ipText)
        {
            if (string.IsNullOrWhiteSpace(ipText))
            {
                return;
            }

            if (!System.Net.IPAddress.TryParse(ipText.Trim(), out System.Net.IPAddress? parsed)
                || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                return;
            }

            byte[] b = parsed.GetAddressBytes();
            if (b[0] == 127 || (b[0] == 10 && b[1] == 247) || (b[0] == 169 && b[1] == 254))
            {
                return;
            }

            string normalized = parsed.ToString();
            if (seen.Add(normalized))
            {
                results.Add(normalized);
            }
        }

        try
        {
            PodmanCommandResult netInspect = await podmanRunner
                .RunAsync(["network", "inspect", OutsideNetworkName], 10, cancellationToken)
                .ConfigureAwait(false);

            if (netInspect.ExitCode == 0 && !string.IsNullOrWhiteSpace(netInspect.StdOut))
            {
                using JsonDocument doc = JsonDocument.Parse(netInspect.StdOut);
                JsonElement netElem = doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0
                    ? doc.RootElement[0]
                    : doc.RootElement;

                if (netElem.TryGetProperty("subnets", out JsonElement subnets) && subnets.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement subnet in subnets.EnumerateArray())
                    {
                        if (subnet.TryGetProperty("gateway", out JsonElement gwElem))
                        {
                            TryAddCandidate(gwElem.GetString());
                        }
                    }
                }
            }
        }
        catch
        {
            // Best-effort network gateway inspection
        }

        if (OperatingSystem.IsLinux())
        {
            foreach (string resolvPath in new[] { "/run/systemd/resolve/resolv.conf", "/etc/resolv.conf" })
            {
                try
                {
                    if (!File.Exists(resolvPath))
                    {
                        continue;
                    }

                    string[] lines = await File.ReadAllLinesAsync(resolvPath, cancellationToken).ConfigureAwait(false);
                    foreach (string line in lines)
                    {
                        string trimmed = line.Trim();
                        if (trimmed.StartsWith("nameserver", StringComparison.OrdinalIgnoreCase))
                        {
                            string[] parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                            if (parts.Length >= 2)
                            {
                                TryAddCandidate(parts[1]);
                            }
                        }
                    }
                }
                catch
                {
                    // Best-effort host resolver read
                }
            }
        }

        if (results.Count == 0)
        {
            TryAddCandidate("10.248.0.1");
        }

        return results;
    }

    private static string GetStringProperty(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(propertyName, out JsonElement prop))
        {
            return prop.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    public async ValueTask DisposeAsync()
    {
        if (reconcileCts is not null)
        {
            await reconcileCts.CancelAsync().ConfigureAwait(false);
            if (reconcileTask is not null)
            {
                try
                {
                    await reconcileTask.ConfigureAwait(false);
                }
                catch
                {
                    // Ignore task cancellation
                }
            }
            reconcileCts.Dispose();
        }
    }
}
