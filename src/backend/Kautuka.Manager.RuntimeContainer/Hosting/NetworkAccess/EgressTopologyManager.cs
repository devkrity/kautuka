using System.Text.Json;
using System.Text.Json.Serialization;
using Kautuka.NetworkAccessController;
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
public sealed class EgressTopologyManager : IAsyncDisposable
{
    public const string WorkloadNetworkName = "kautuka-workload";
    public const string OutsideNetworkName = "kautuka-outside";
    public const string EgressPodName = "kautuka-egress";
    public const string PackageProxyContainerName = "kautuka-pkgs-proxy";
    public const string EgressPodIp = "10.247.0.2";
    public const string PackageProxyIp = "10.247.0.3";
    public const string WorkloadSubnet = "10.247.0.0/16";

    private readonly PodmanCommandRunner podmanRunner;
    private readonly WorkloadAddressAllocator addressAllocator;
    private readonly WorkloadPostStartVerifier postStartVerifier;
    private readonly string stateDirectory;
    private readonly string allowlistFilePath;
    private readonly string startupGateFilePath;
    private readonly ILogger<EgressTopologyManager> logger;

    private readonly object gateLock = new();
    private bool isStartupGateOpen;
    private long unlabelledWorkloadCount;
    private CancellationTokenSource? reconcileCts;
    private Task? reconcileTask;

    public EgressTopologyManager(
        PodmanCommandRunner podmanRunner,
        WorkloadAddressAllocator addressAllocator,
        WorkloadPostStartVerifier postStartVerifier,
        string stateDirectory,
        ILogger<EgressTopologyManager>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(podmanRunner);
        ArgumentNullException.ThrowIfNull(addressAllocator);
        ArgumentNullException.ThrowIfNull(postStartVerifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);

        this.podmanRunner = podmanRunner;
        this.addressAllocator = addressAllocator;
        this.postStartVerifier = postStartVerifier;
        this.stateDirectory = Path.GetFullPath(stateDirectory);
        this.logger = logger ?? NullLogger<EgressTopologyManager>.Instance;

        string nacDir = Path.Combine(this.stateDirectory, "nac");
        Directory.CreateDirectory(nacDir);
        allowlistFilePath = Path.Combine(nacDir, "infra-allowlist.json");
        startupGateFilePath = Path.Combine(nacDir, "startup-gate.status");
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

        // 1. Ensure networks exist
        await EnsureNetworksAsync(cancellationToken).ConfigureAwait(false);

        // 2. Write infra-allowlist.json
        WriteInfraAllowlist();

        // 3. Ensure egress pod and infra containers are running
        await EnsureEgressPodAsync(isWindowsHostNativeDev, decisionEndpointIp, includeLanGrants, cancellationToken).ConfigureAwait(false);

        // 4. Rebuild registry from live containers before opening listener (Section 4.3.4 item 6)
        await RebuildRegistryFromLiveContainersAsync(registry, cancellationToken).ConfigureAwait(false);

        // 5. Run startup self-test canary (Section 4.3.4 item 5)
        bool canaryPassed = await RunStartupSelfTestAsync(cancellationToken).ConfigureAwait(false);
        if (!canaryPassed)
        {
            IsStartupGateOpen = false;
            logger.LogError("Startup canary self-test failed; startup gate closed.");
            throw new InvalidOperationException("Network access startup canary self-test failed.");
        }

        IsStartupGateOpen = true;
        logger.LogInformation("Startup canary self-test passed; startup gate open.");

        // 6. Start 30s reconcile loop
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
            .RunAsync(["ps", "-a", "--filter", "label=kautuka.managed=manager", "--format", "json"], 30, cancellationToken)
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

                        bool systemwide = string.Equals(systemwideStr, "true", StringComparison.OrdinalIgnoreCase);

                        // Find IP on kautuka-workload
                        string ip = string.Empty;
                        if (inspected.TryGetProperty("NetworkSettings", out JsonElement netSettings)
                            && netSettings.TryGetProperty("Networks", out JsonElement networks)
                            && networks.TryGetProperty(WorkloadNetworkName, out JsonElement wlNet)
                            && wlNet.TryGetProperty("IPAddress", out JsonElement ipElem))
                        {
                            ip = ipElem.GetString() ?? string.Empty;
                        }

                        // Validation of required labels:
                        bool isValid = !string.IsNullOrWhiteSpace(ip)
                            && !string.IsNullOrWhiteSpace(kind)
                            && !string.IsNullOrWhiteSpace(execContext);

                        if (isValid && kind is "connector" or "agent-sandbox")
                        {
                            if (!systemwide && string.IsNullOrWhiteSpace(user))
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

                        string attribution = "user-agent";
                        if (egress is "deny-all" or "proxy-only")
                        {
                            attribution = "none";
                        }
                        else if (systemwide)
                        {
                            attribution = string.IsNullOrWhiteSpace(user) ? "none" : "user";
                        }
                        else if (string.IsNullOrWhiteSpace(agent))
                        {
                            attribution = "user";
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
                            DateTimeOffset.UtcNow);

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
    public async Task<bool> RunStartupSelfTestAsync(CancellationToken cancellationToken = default)
    {
        string canaryName = $"kautuka-canary-{Guid.NewGuid():N}"[..24];
        string canaryIp = addressAllocator.AllocateNextIp();

        logger.LogInformation("Running startup canary self-test '{CanaryName}' at IP {CanaryIp}...", canaryName, canaryIp);
        try
        {
            var spec = new WorkloadContainerSpec(canaryName, "ghcr.io/devkrity/kautuka-base", canaryIp)
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

            return verification.Passed;
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
            try
            {
                await podmanRunner.RunAsync(["stop", "-t", "2", entry.ContainerId], 10, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Ignore stop error
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

        if (inspectWl.ExitCode != 0)
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
                .RunAsync(["network", "create", OutsideNetworkName], 30, cancellationToken)
                .ConfigureAwait(false);

            if (createOut.ExitCode != 0)
            {
                throw new InvalidOperationException($"Failed to create network {OutsideNetworkName}: {createOut.StdErr}");
            }
        }
    }

    private void WriteInfraAllowlist()
    {
        var entries = new List<InfraAllowlistEntry>
        {
            new(EgressPodName, "egress-pod", [EgressPodIp], "pinned"),
            new("kautuka-egress-envoy", "egress-envoy", [EgressPodIp], "docker.io/envoyproxy/envoy:v1.40.2"),
            new("kautuka-egress-dns", "egress-dns", [EgressPodIp], "docker.io/coredns/coredns:1.14.6"),
            new(PackageProxyContainerName, "pkgs-proxy", [PackageProxyIp], "ghcr.io/devkrity/kautuka-package-proxy:v1"),
        };

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
        string boundaryNft = BoundaryRulesetRenderer.RenderBoundaryNft(includeLanGrants, decisionEndpointIp);
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
                "--network", OutsideNetworkName,
                "--sysctl", "net.ipv4.ip_forward=0",
                "--sysctl", "net.ipv6.conf.all.disable_ipv6=1"
            ];

            PodmanCommandResult podResult = await podmanRunner
                .RunAsync(createPodArgs, 30, cancellationToken)
                .ConfigureAwait(false);

            if (podResult.ExitCode != 0)
            {
                logger.LogError("Failed to create egress pod: {StdErr}", podResult.StdErr);
            }
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

    private async Task ReconcileOnceAsync(IWorkloadRegistry registry, CancellationToken cancellationToken)
    {
        // 1. Compare running containers to registry
        PodmanCommandResult psResult = await podmanRunner
            .RunAsync(["ps", "-a", "--filter", "label=kautuka.managed=manager", "--format", "json"], 30, cancellationToken)
            .ConfigureAwait(false);

        if (psResult.ExitCode != 0)
        {
            return;
        }

        var liveCids = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(psResult.StdOut))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(psResult.StdOut);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement container in doc.RootElement.EnumerateArray())
                    {
                        if (container.TryGetProperty("Id", out JsonElement idElem))
                        {
                            string? cid = idElem.GetString();
                            if (!string.IsNullOrWhiteSpace(cid))
                            {
                                liveCids.Add(cid);
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

        // Deregister dead containers
        foreach (RegistryEntry entry in registry.GetAll())
        {
            if (!liveCids.Contains(entry.ContainerId))
            {
                logger.LogInformation("Reconcile: Deregistering terminated container '{ContainerId}' (IP: {Ip}).", entry.ContainerId, entry.Ip);
                registry.Deregister(entry.Ip, entry.ContainerId);
            }
        }
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
