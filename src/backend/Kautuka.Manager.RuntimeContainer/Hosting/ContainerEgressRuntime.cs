using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Sandbox.Models;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

/// <summary>Manager-owned native networks, egress pod and serialized file-policy publication.</summary>
public sealed partial class ContainerEgressRuntime(
    PodmanCommandRunner runner, ContainerEgressOptions options, RuntimeImageAllowlist allowlist, string stateRoot,
    PodmanImageArchiveStore? archives = null) : IDisposable
{
    private const string podName = "kautuka-egress";
    private const string outsideName = "kautuka-outside";
    private const string coreName = "kautuka-core";
    private const string ownerLabel = "kautuka.egress";
    private static readonly string[] packageUpstreams = ["registry.npmjs.org", "pypi.org", "files.pythonhosted.org"];
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, ScopeNetwork> networks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Source> sources = new(StringComparer.Ordinal);
    private readonly HashSet<string> ownedMaterializers = new(StringComparer.Ordinal);
    private readonly HashSet<string> retiring = new(StringComparer.Ordinal);
    private readonly HashSet<string> pendingHelpers = new(StringComparer.Ordinal);
    private readonly string configurationRoot = Path.Combine(stateRoot, "egress", "config");
    private readonly string artifactSocketRoot = Path.Combine(stateRoot, "egress", "host");
    public string ArtifactSocketPath => Path.Combine(artifactSocketRoot, "connect.sock");
    private EnvoyEgressConfiguration? active;
    private readonly PodmanImageArchiveStore imageStore = archives ?? new PodmanImageArchiveStore(runner, Path.Combine(stateRoot, "oci-images"),
        allowlist, Microsoft.Extensions.Logging.Abstractions.NullLogger<PodmanImageArchiveStore>.Instance);
    private ResolvedRuntimeImage? helperImage;
    private string? infraId;
    private string? podId;
    private string? outsideDefaultGateway;
    private string? outsideSourceAddress;
    private bool recoveryRequiresProxyStop;
    private bool ready;
    private int activePrivateNetworks;

    public bool Enabled => options.Enabled;
    public bool TelemetryEnabled => Enabled && options.TelemetryEnabled;

    public async Task<string?> GetCoreTelemetryEndpointAsync(CancellationToken cancellationToken)
    {
        if (!TelemetryEnabled) return null;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureReadyCoreAsync(cancellationToken).ConfigureAwait(false);
            return "http://" + networks[coreName].Gateway + ":5080/api/default";
        }
        finally { gate.Release(); }
    }

    private static bool IsTelemetrySource(SourceIdentity identity)
        => identity.Role == "core" && identity.Scope.Kind == "core" && identity.ComponentId == "kautuka-openobserve";

    internal async Task<bool> ProbeTelemetryAsync(PreparedPodmanContainer container, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!sources.TryGetValue(container.Id, out Source? source) || !IsTelemetrySource(source.Identity))
                throw new InvalidOperationException("Telemetry health requires the registered collector.");
            PodmanCommandResult result = await HelperAsync("/usr/bin/curl",
                ["--silent", "--show-error", "--fail", "--max-time", "2", "--retry", "8", "--retry-connrefused", "--retry-delay", "1", "--retry-max-time", "8", "http://127.0.0.1:5080/healthz"],
                cancellationToken, networkNamespace: container.SandboxKey).ConfigureAwait(false);
            return result.ExitCode == 0;
        }
        finally { gate.Release(); }
    }
    internal string MaterializationRoot { get; } = Path.Combine(stateRoot, "egress", "builds");

    public void Dispose()
    {
        gate.Dispose();
        imageAcquisitionGate.Dispose();
        GC.SuppressFinalize(this);
    }

    internal sealed record Scope(string UserId, string AgentId, string Kind = "agent")
    {
        internal string Key => Kind == "core" ? "core" : Hash(UserId + "\0" + Kind + "\0" + AgentId);
        internal string NetworkName => Kind == "core" ? coreName : "kautuka-" + Key[..32];
    }

    private sealed record ScopeNetwork(string Name, string Subnet, string Gateway, string? NativeGateway, bool Prepared = false);
    internal sealed record SourceIdentity(Scope Scope, string Role, string ComponentId, bool PackageEligible = false);
    private sealed record Source(PreparedPodmanContainer Container, string Name, SourceIdentity Identity, bool Authorized);

    internal static Scope SandboxScope(ContainerContext workspace)
    {
        if (workspace.ScopeType != "agent" || string.IsNullOrWhiteSpace(workspace.UserId))
            throw new InvalidOperationException("A sandbox requires its accepted canonical user-agent identity.");
        return new Scope(workspace.UserId, workspace.ScopeId);
    }

    internal static Scope ConnectorScope(string connectorId, ContainerContext? workspace)
    {
        int separator = connectorId.IndexOf("::", StringComparison.Ordinal);
        string owner = separator > 0 ? connectorId[..separator] : "system";
        if (workspace is not null && (!string.Equals(workspace.UserId ?? "system", owner, StringComparison.Ordinal)
            || !string.Equals(workspace.ScopeType, "agent", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Connector egress scope does not match the accepted agent owner.");
        }

        return new Scope(owner, workspace?.ScopeId ?? "management", workspace is null ? "management" : "agent");
    }

    public async Task EnsureReadyAsync(CancellationToken cancellationToken)
    {
        if (!Enabled) return;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await EnsureReadyCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    internal Task<PreparedPodmanContainer> PrepareAndAuthorizeAsync(
        WorkspaceMcpContainerConnectorDefinition connector, string containerName, Scope scope,
        Func<string, string, List<string>> buildCreate, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null)
        => PrepareAndAuthorizeAsync(new SourceIdentity(scope, "connector", connector.ConnectorId,
            PackageEligible: !string.IsNullOrWhiteSpace(connector.ServerType) || McpbImageMaterializer.InferServerType(connector.Command) is not null), containerName, buildCreate, cancellationToken, environment);

    internal async Task<PreparedPodmanContainer> PrepareAndAuthorizeAsync(
        SourceIdentity identity, string containerName, Func<string, string, List<string>> buildCreate, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        Scope scope = identity.Scope;
        if (identity.Role is not ("connector" or "agent" or "package" or "materializer" or "core")) throw new ArgumentException("Unsupported source role.", nameof(identity));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureReadyCoreAsync(cancellationToken).ConfigureAwait(false);
            ScopeNetwork network = await EnsureScopeCoreAsync(scope.NetworkName, scope.Key, cancellationToken).ConfigureAwait(false);
            List<string> create = buildCreate(network.Name, network.Gateway);
            create.InsertRange(1, ["--label", ownerLabel + "=true", "--label", "kautuka.egress.user=" + scope.UserId,
                "--label", "kautuka.egress.agent=" + scope.AgentId, "--label", "kautuka.egress.scope-kind=" + scope.Kind,
                "--label", "kautuka.egress.source=true", "--label", "kautuka.egress.source-role=" + identity.Role,
                "--label", "kautuka.egress.component=" + identity.ComponentId, "--label", "kautuka.egress.packages=" + (identity.PackageEligible ? "true" : "false")]);
            if (identity.Role == "connector") create.InsertRange(1, ["--label", "kautuka.egress.connector=" + identity.ComponentId]);
            if (identity.PackageEligible) create.InsertRange(1, ["--add-host", "kautuka-pkgs-proxy:" + network.Gateway]);
            PreparedPodmanContainer prepared;
            try
            {
                prepared = await new PodmanContainerPreparation(runner)
                    .PrepareAsync(create, network.Name, network.Gateway, helperImage!, cancellationToken, environment).ConfigureAwait(false);
            }
            catch
            {
                await RetireEmptyScopesAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            // Native stop/exit may release an IP. Only the newly observed allocation may own its grants.
            bool packageBoundaryChanged = identity.Role == "package" || IsTelemetrySource(identity);
            foreach (string stale in sources.Where(pair => pair.Value.Container.NetworkName == prepared.NetworkName
                && pair.Value.Container.Address == prepared.Address).Select(static pair => pair.Key).ToArray())
            {
                packageBoundaryChanged |= sources[stale].Identity.Role == "package" || IsTelemetrySource(sources[stale].Identity);
                sources.Remove(stale);
            }
            sources.Add(prepared.Id, new Source(prepared, containerName, identity, Authorized: true));
            try
            {
                if (packageBoundaryChanged) await ApplyBoundaryAsync(cancellationToken).ConfigureAwait(false);
                await PublishAsync(CompileCurrent(), cancellationToken).ConfigureAwait(false);
                if (identity.Role == "materializer") ownedMaterializers.Add(prepared.Id);
                return prepared;
            }
            catch
            {
                // The application has not run. Keep its allocation if retirement cannot confirm revocation.
                await RetireCoreAsync(prepared.Id, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally { gate.Release(); }
    }

    internal async Task RetireAsync(string id, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ownedMaterializers.Remove(id);
            retiring.Add(id);
            await EnsureReadyCoreAsync(cancellationToken).ConfigureAwait(false);
            await RetireCoreAsync(id, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task<ManagedContainerReconcileResult> RetireOrphansAsync(string role, Func<IReadOnlyCollection<string>> desiredNames, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desiredNames);
        if (role is not ("sidecar" or "agent")) throw new ArgumentException("Unsupported orphan owner.", nameof(role));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureReadyCoreAsync(cancellationToken).ConfigureAwait(false);
            PodmanCommandResult listed = await RunAsync(["ps", "--all", "--filter", "label=kautuka.managed=manager", "--filter", "label=kautuka.role=" + role,
                "--no-trunc", "--format", "{{.ID}}"], cancellationToken).ConfigureAwait(false);
            int removed = 0;
            foreach (string id in listed.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!IsId(id)) throw new InvalidOperationException("Invalid managed orphan identity.");
                using JsonDocument inspected = JsonDocument.Parse((await RunAsync(["inspect", id], cancellationToken).ConfigureAwait(false)).StdOut);
                string name = inspected.RootElement[0].GetProperty("Name").GetString()!.TrimStart('/');
                if (desiredNames().Contains(name, StringComparer.Ordinal)) continue;
                await RetireCoreAsync(id, cancellationToken).ConfigureAwait(false);
                removed++;
            }
            return new ManagedContainerReconcileResult(true, removed, null);
        }
        finally { gate.Release(); }
    }

    internal async Task<PreparedPodmanContainer?> FindSourceAsync(string name, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureReadyCoreAsync(cancellationToken).ConfigureAwait(false);
            return sources.Values.SingleOrDefault(source => source.Name == name && source.Authorized)?.Container;
        }
        finally { gate.Release(); }
    }

    internal async Task<T> StartWithAdmissionAsync<T>(PreparedPodmanContainer container,
        Func<CancellationToken, Task<T>> start, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureReadyCoreAsync(cancellationToken).ConfigureAwait(false);
            if (!sources.TryGetValue(container.Id, out Source? source) || !source.Authorized || source.Container != container)
                throw new InvalidOperationException("The exact container no longer has an admitted source allocation.");
            using JsonDocument inspected = JsonDocument.Parse((await RunAsync(["inspect", container.Id], cancellationToken).ConfigureAwait(false)).StdOut);
            JsonElement current = inspected.RootElement[0];
            JsonElement attached = current.GetProperty("NetworkSettings").GetProperty("Networks");
            string? state = current.GetProperty("State").GetProperty("Status").GetString();
            if (state is not ("initialized" or "running") || current.GetProperty("Id").GetString() != container.Id || attached.EnumerateObject().Count() != 1
                || !attached.TryGetProperty(container.NetworkName, out JsonElement network)
                || network.GetProperty("IPAddress").GetString() != container.Address
                || current.GetProperty("NetworkSettings").GetProperty("SandboxKey").GetString() != container.SandboxKey)
                throw new InvalidOperationException("Container allocation changed after policy admission; startup is denied.");
            return await start(cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task RetireCoreAsync(string id, CancellationToken cancellationToken)
    {
        if (!IsId(id)) throw new InvalidOperationException("Egress retirement requires an immutable container ID.");
        retiring.Add(id);
        if (sources.TryGetValue(id, out Source? source))
        {
            sources[id] = source with { Authorized = false };
            try
            {
                await PublishAsync(CompileCurrent(), cancellationToken).ConfigureAwait(false);
                if (source.Identity.Role == "package" || IsTelemetrySource(source.Identity)) await ApplyBoundaryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Ordinary stop may release the IP. First blackhole egress and invalidate readiness.
                ready = false;
                recoveryRequiresProxyStop = true;
                await StopProxyAsync(CancellationToken.None).ConfigureAwait(false);
                _ = await runner.RunAsync(["stop", "--time", "1", id], 15, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }

        _ = await runner.RunAsync(["rm", "--force", id], 15, cancellationToken).ConfigureAwait(false);
        PodmanCommandResult exists = await runner.RunAsync(["container", "exists", id], 10, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (exists.ExitCode != 1 || exists.TerminationReason != ProcessTerminationReason.NonZeroExit)
        {
            ready = false;
            throw new InvalidOperationException("Egress retirement could not confirm address allocation removal.");
        }

        sources.Remove(id);
        ownedMaterializers.Remove(id);
        retiring.Remove(id);
        await RetireEmptyScopesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureReadyCoreAsync(CancellationToken cancellationToken)
    {
        if (ready)
        {
            PodmanCommandResult inspected = await runner.RunAsync(["inspect", "--format", "{{.State.Running}}", "kautuka-egress-envoy", "kautuka-egress-dns"], 10, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            string[] states = inspected.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (inspected.ExitCode == 0 && states.Length == 2 && states.All(static state => state == "true")) return;
            if (inspected.TerminationReason is ProcessTerminationReason.TimedOut or ProcessTerminationReason.CallerCancelled)
                throw new ContainerInspectionUnavailableException("Cannot confirm the egress services' running state; retry the operation.");
            ready = false;
            recoveryRequiresProxyStop = true;
        }
        if (recoveryRequiresProxyStop) await StopProxyAsync(cancellationToken).ConfigureAwait(false);
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Container egress requires the Linux appliance.");
        if (options.MaxPrivateNetworks < 2) throw new InvalidOperationException("Egress needs capacity for core and a workload scope.");
        NetworkAddressPool configuredPool = NetworkAddressPool.Parse(options.NetworkPool);
        if (options.MaxPrivateNetworks >= configuredPool.SlotCount)
            throw new InvalidOperationException("The configured egress pool does not have enough /24 subnets for the private-network limit and outside network.");
        _ = await ValidatePoolInventoryAsync(configuredPool, cancellationToken).ConfigureAwait(false);
        networks.Clear();
        Volatile.Write(ref activePrivateNetworks, 0);
        if (!options.FollowHostDns) NetworkDestinationPolicy.ValidateDnsUpstreams(options.DnsUpstreams);

        helperImage = await RequireCachedImageAsync("egress-setup", cancellationToken).ConfigureAwait(false);
        await RecoverHelpersAsync(cancellationToken).ConfigureAwait(false);
        ResolvedRuntimeImage envoy = await RequireCachedImageAsync("egress-envoy", cancellationToken).ConfigureAwait(false);
        ResolvedRuntimeImage dns = await RequireCachedImageAsync("egress-dns", cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(configurationRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        await PrepareDnsStartupAsync(cancellationToken).ConfigureAwait(false);
        if ((options.ArtifactHosts.Count > 0 || TelemetryEnabled)) await PrepareArtifactSocketDirectoryAsync(cancellationToken).ConfigureAwait(false);
        ScopeNetwork outside = await EnsureNetworkAsync(outsideName, "outside", isPrivate: false, cancellationToken).ConfigureAwait(false);
        outsideDefaultGateway = outside.NativeGateway ?? throw new InvalidOperationException("The outside network has no native gateway.");
        await LoadPrivateTopologyAsync(cancellationToken).ConfigureAwait(false);
        bool podExists = await ExistsAsync(["pod", "exists", podName], cancellationToken).ConfigureAwait(false);
        if (!podExists)
        {
            _ = await RunAsync(["pod", "create", "--name", podName, "--label", "kautuka.managed=manager",
                "--label", ownerLabel + "=true", "--dns", "127.0.0.1", "--sysctl", "net.ipv4.ip_unprivileged_port_start=0",
                "--sysctl", "net.ipv4.ip_forward=0",
                "--exit-policy", "continue",
                "--security-opt", "no-new-privileges", "--infra-image", helperImage.LocalImageId,
                "--infra-command", "/usr/bin/sleep infinity", "--network", outsideName], cancellationToken).ConfigureAwait(false);
        }

        using (JsonDocument inspected = JsonDocument.Parse((await RunAsync(["pod", "inspect", podName], cancellationToken).ConfigureAwait(false)).StdOut))
        {
            JsonElement pod = inspected.RootElement[0];
            if (pod.GetProperty("Labels").GetProperty(ownerLabel).GetString() != "true")
                throw new InvalidOperationException("The egress pod is not Manager-owned.");
            podId = pod.GetProperty("Id").GetString();
            if (podId is null || !IsId(podId)) throw new InvalidOperationException("Invalid managed pod identity.");
            infraId = pod.GetProperty("InfraContainerID").GetString();
            if (infraId is null || !IsId(infraId)) throw new InvalidOperationException("The egress pod has no immutable namespace owner.");
        }

        // Starting the whole pod would revive Envoy with stale files before ownership recovery.
        _ = await RunAsync(["start", infraId!], cancellationToken).ConfigureAwait(false);
        // Install a dropping boundary before attaching any private network to a new namespace.
        Volatile.Write(ref authorization, null);
        sources.Clear(); // Do not restore package or collector exceptions from stale allocations.
        try
        {
            await ApplyBoundaryAsync(cancellationToken).ConfigureAwait(false);
            await PrepareRecoveredScopesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ready = false;
            recoveryRequiresProxyStop = true;
            await StopProxyAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        _ = await EnsureScopeCoreAsync(coreName, "core", cancellationToken).ConfigureAwait(false);
        List<string> inactive = await RestoreSourcesAsync(cancellationToken).ConfigureAwait(false);
        await ApplyBoundaryAsync(cancellationToken).ConfigureAwait(false);
        EnvoyEgressConfiguration initial = CompileCurrent();
        bool proxyRunning = false;
        if (await ExistsAsync(["container", "exists", "kautuka-egress-envoy"], cancellationToken).ConfigureAwait(false))
        {
            using JsonDocument inspected = JsonDocument.Parse((await RunAsync(["inspect", "kautuka-egress-envoy"], cancellationToken).ConfigureAwait(false)).StdOut);
            proxyRunning = inspected.RootElement[0].GetProperty("State").GetProperty("Running").GetBoolean();
        }
        if (!proxyRunning)
        {
            // A stopped proxy must never revive from stale source ownership.
            await WriteAsync("cds.json", initial.ClusterDocument, cancellationToken).ConfigureAwait(false);
            await WriteAsync("lds.json", initial.ListenerDocument, cancellationToken).ConfigureAwait(false);
            active = null;
            Volatile.Write(ref authorization, null);
        }
        string desiredDns = options.FollowHostDns ? dnsStartup!.Corefile
            : ".:53 {\n forward . " + string.Join(' ', options.DnsUpstreams) + "\n log\n errors\n}\n";
        if (!File.Exists(Path.Combine(configurationRoot, "bootstrap.json")))
        {
            await WriteAsync("cds.json", initial.ClusterDocument, cancellationToken).ConfigureAwait(false);
            await WriteAsync("lds.json", initial.ListenerDocument, cancellationToken).ConfigureAwait(false);
            await WriteAsync("bootstrap.json", """
                {"node":{"id":"kautuka-egress","cluster":"kautuka"},"admin":{"address":{"socket_address":{"address":"127.0.0.1","port_value":9901}}},
                "dynamic_resources":{"cds_config":{"resource_api_version":"V3","path_config_source":{"path":"/egress/cds.json"}},
                "lds_config":{"resource_api_version":"V3","path_config_source":{"path":"/egress/lds.json"}}}}
                """, cancellationToken).ConfigureAwait(false);
            await WriteAsync("Corefile", desiredDns, cancellationToken).ConfigureAwait(false);
        }

        if (options.FollowHostDns) await WriteAsync("Corefile", desiredDns, cancellationToken).ConfigureAwait(false);
        if (!File.Exists(Path.Combine(configurationRoot, "Corefile"))
            || await File.ReadAllTextAsync(Path.Combine(configurationRoot, "Corefile"), cancellationToken).ConfigureAwait(false) != desiredDns)
            throw new InvalidOperationException("Persisted DNS configuration differs from the requested upstreams; controlled DNS replacement is required.");

        await EnsureServiceAsync("kautuka-egress-dns", dns, isDns: true, cancellationToken).ConfigureAwait(false);
        await ConfirmDnsStartupAsync(cancellationToken).ConfigureAwait(false);
        if (options.FollowHostDns) await TryRefreshDnsAtStartupAsync(cancellationToken).ConfigureAwait(false);
        await EnsureServiceAsync("kautuka-egress-envoy", envoy, isDns: false, cancellationToken, requireRunning: proxyRunning).ConfigureAwait(false);
        if (proxyRunning) active = await ReadActiveConfigurationAsync(cancellationToken).ConfigureAwait(false);
        await PublishAsync(initial, cancellationToken).ConfigureAwait(false);
        foreach (string id in inactive)
        {
            _ = await RunAsync(["rm", "--force", id], cancellationToken).ConfigureAwait(false);
            retiring.Remove(id);
        }
        await RetireEmptyScopesAsync(cancellationToken).ConfigureAwait(false);
        ready = true;
        recoveryRequiresProxyStop = false;
    }

    private async Task<ResolvedRuntimeImage> RequireCachedImageAsync(string id, CancellationToken cancellationToken)
    {
        RuntimeImageEntry entry = allowlist.Images.SingleOrDefault(image => image.Id == id)
            ?? throw new InvalidOperationException("The verified runtime image inventory is missing " + id + ".");
        ContainerImageProvenancePolicy.EnsureTrusted(entry.Image, id);
        return await imageStore.ResolveCachedAsync(entry.Image, cancellationToken).ConfigureAwait(false);
    }

    private async Task PrepareArtifactSocketDirectoryAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Artifact socket permissions require Linux.");
        Directory.CreateDirectory(artifactSocketRoot);
        // Only this owned directory: never recursively change runtime storage ownership.
        await RunAsync(["unshare", "chown", "0:0", artifactSocketRoot], ct).ConfigureAwait(false);
        File.SetUnixFileMode(artifactSocketRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.SetGroup);
        string ownership = (await RunAsync(["unshare", "stat", "--format=%u:%g", artifactSocketRoot], ct).ConfigureAwait(false)).StdOut.Trim();
        if (ownership != "0:0") throw new InvalidOperationException("Cannot confirm host artifact socket directory mapping.");
    }

    private async Task EnsureServiceAsync(string name, ResolvedRuntimeImage resolved, bool isDns, CancellationToken cancellationToken, bool requireRunning = false)
    {
        string image = resolved.LocalImageId;
        string serviceUser = isDns ? "65534:65534" : "65532:65532";
        if (await ExistsAsync(["container", "exists", name], cancellationToken).ConfigureAwait(false))
        {
            using JsonDocument inspected = JsonDocument.Parse((await RunAsync(["inspect", name], cancellationToken).ConfigureAwait(false)).StdOut);
            JsonElement container = inspected.RootElement[0];
            using JsonDocument expectedImage = JsonDocument.Parse((await RunAsync(["image", "inspect", image], cancellationToken).ConfigureAwait(false)).StdOut);
            string? imageId = expectedImage.RootElement[0].GetProperty("Id").GetString();
            JsonElement expectedEntrypoint = expectedImage.RootElement[0].GetProperty("Config").GetProperty("Entrypoint");
            string[] expectedArguments = isDns ? ["-conf", "/egress/Corefile"] : ["-c", "/egress/bootstrap.json", "--concurrency", "1"];
            JsonElement host = container.GetProperty("HostConfig");
            JsonElement config = container.GetProperty("Config");
            bool legacyDnsUser = isDns && config.GetProperty("User").GetString() == "65532:65532";
            JsonElement binds = host.GetProperty("Binds");
            bool artifactMount = !isDns && (options.ArtifactHosts.Count > 0 || TelemetryEnabled);
            string[] expectedBinds = isDns ? [configurationRoot + ":/egress:ro"] : artifactMount ? [configurationRoot + ":/egress:ro", artifactSocketRoot + ":/host-artifacts:rw", Path.GetDirectoryName(AuthorizationSocketPath)! + ":/manager-authorization:ro"] : [configurationRoot + ":/egress:ro", Path.GetDirectoryName(AuthorizationSocketPath)! + ":/manager-authorization:ro"];
            JsonElement mounts = container.GetProperty("Mounts");
            bool mountsMatch = mounts.ValueKind == JsonValueKind.Array && mounts.GetArrayLength() == expectedBinds.Length
                && expectedBinds.All(expected =>
                {
                    string[] parts = expected.Split(':');
                    return mounts.EnumerateArray().Any(mount => mount.GetProperty("Type").GetString() == "bind"
                        && mount.GetProperty("Source").GetString() == parts[0] && mount.GetProperty("Destination").GetString() == parts[1]
                        && mount.GetProperty("RW").GetBoolean() == (parts[2] == "rw"));
                });
            string[] groups = host.TryGetProperty("GroupAdd", out JsonElement groupAdd) && groupAdd.ValueKind == JsonValueKind.Array
                ? [.. groupAdd.EnumerateArray().Select(item => item.GetString()!)] : [];

            if (container.GetProperty("Config").GetProperty("Labels").GetProperty(ownerLabel).GetString() != "true"
                || container.GetProperty("Pod").GetString() != podId || container.GetProperty("Image").GetString() != imageId
                || !JsonElement.DeepEquals(config.GetProperty("Entrypoint"), expectedEntrypoint)
                || !config.GetProperty("Cmd").EnumerateArray().Select(static argument => argument.GetString()).SequenceEqual(expectedArguments)
                || (!legacyDnsUser && config.GetProperty("User").GetString() != serviceUser) || !host.GetProperty("ReadonlyRootfs").GetBoolean()
                || host.GetProperty("Privileged").GetBoolean()
                || host.GetProperty("RestartPolicy").GetProperty("Name").GetString() is not ("no" or "")
                || !host.GetProperty("SecurityOpt").EnumerateArray().Any(static item => item.GetString() == "no-new-privileges")
                || !CapabilitiesMatch(container.GetProperty("BoundingCaps"), isDns)
                || !CapabilitiesMatch(container.GetProperty("EffectiveCaps"), isDns)
                || binds.ValueKind != JsonValueKind.Array || binds.GetArrayLength() != expectedBinds.Length
                || !expectedBinds.All(expected => binds.EnumerateArray().Any(bind => bind.GetString() == expected || (bind.GetString()?.StartsWith(expected + ",", StringComparison.Ordinal) ?? false)))
                || !mountsMatch
                || !(!isDns ? groups.SequenceEqual(new[] { "0" }) : groups.Length == 0))
                throw new InvalidOperationException("Existing egress service does not match its managed pod, image and security specification.");
            bool running = container.GetProperty("State").GetProperty("Running").GetBoolean();
            if (legacyDnsUser)
            {
                string legacyId = container.GetProperty("Id").GetString()!;
                if (running) _ = await RunAsync(["stop", "--time", "5", legacyId], cancellationToken).ConfigureAwait(false);
                _ = await RunAsync(["rm", legacyId], cancellationToken).ConfigureAwait(false);
                await EnsureServiceAsync(name, resolved, isDns, cancellationToken).ConfigureAwait(false);
                return;
            }
            if (!isDns && (!host.TryGetProperty("LogConfig", out JsonElement log)
                || log.GetProperty("Type").GetString() != "k8s-file"
                || !string.Equals(log.GetProperty("Size").GetString(), "16MB", StringComparison.OrdinalIgnoreCase)))
            {
                if (running || requireRunning)
                    throw new InvalidOperationException("The managed proxy's audit log options differ; controlled proxy replacement is required.");
                // Proper update shutdown has already closed egress. Replace
                // only the exact stopped, fully validated managed container.
                await TryFinalNetworkAuditAsync(cancellationToken).ConfigureAwait(false);
                _ = await RunAsync(["rm", container.GetProperty("Id").GetString()!], cancellationToken).ConfigureAwait(false);
                await EnsureServiceAsync(name, resolved, isDns, cancellationToken).ConfigureAwait(false);
                return;
            }
            if (requireRunning && !running) throw new InvalidOperationException("Egress changed during adoption; recovery must be retried before startup.");
            if (running) return;
            _ = await RunAsync(["start", container.GetProperty("Id").GetString()!], cancellationToken).ConfigureAwait(false);
            return;
        }
        if (requireRunning) throw new InvalidOperationException("Egress disappeared during adoption; recovery must be retried before startup.");

        var args = new List<string> { "run", "--detach", "--pull=never", "--restart=no", "--name", name, "--pod", podName,
            "--label", "kautuka.managed=manager", "--label", ownerLabel + "=true", "--read-only", "--cap-drop=ALL",
            "--security-opt=no-new-privileges", "--user", serviceUser, "--volume", configurationRoot + ":/egress:ro" };
        if (isDns) args.Add("--cap-add=NET_BIND_SERVICE");
        else
        {
            args.AddRange(["--log-driver=k8s-file", "--log-opt=max-size=16mb"]);
            args.AddRange(["--group-add=0", "--volume", Path.GetDirectoryName(AuthorizationSocketPath)! + ":/manager-authorization:ro"]);
            if ((options.ArtifactHosts.Count > 0 || TelemetryEnabled)) args.AddRange(["--volume", artifactSocketRoot + ":/host-artifacts:rw"]);
        }
        args.Add(image);
        args.AddRange(isDns ? ["-conf", "/egress/Corefile"] : ["-c", "/egress/bootstrap.json", "--concurrency", "1"]);
        _ = await RunAsync(args, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ScopeNetwork> EnsureScopeCoreAsync(string name, string scopeKey, CancellationToken cancellationToken)
    {
        if (networks.TryGetValue(name, out ScopeNetwork? known) && known.Prepared) return known;
        PodmanCommandResult inventory = await RunAsync(["network", "ls", "--filter", "label=" + ownerLabel + "=true", "--format", "{{.Name}}"], cancellationToken).ConfigureAwait(false);
        string[] existing = inventory.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!existing.Contains(name, StringComparer.Ordinal) && existing.Count(static item => item != outsideName) >= options.MaxPrivateNetworks)
            throw new InvalidOperationException($"The limit of {options.MaxPrivateNetworks} private networks is reached. Core, management, and agent scopes share this limit; stop an unused scope or increase ContainerEgress:MaxPrivateNetworks after checking host capacity.");
        ScopeNetwork network = await EnsureNetworkAsync(name, scopeKey, isPrivate: true, cancellationToken).ConfigureAwait(false);
        networks[name] = network;
        Volatile.Write(ref activePrivateNetworks, networks.Count);
        try
        {
            // Protect even a newly local/private destination before adding its route/interface.
            await ApplyBoundaryAsync(cancellationToken).ConfigureAwait(false);
            using JsonDocument inspected = JsonDocument.Parse((await RunAsync(["inspect", infraId!], cancellationToken).ConfigureAwait(false)).StdOut);
            JsonElement attachments = inspected.RootElement[0].GetProperty("NetworkSettings").GetProperty("Networks");
            if (!attachments.TryGetProperty(name, out JsonElement attached))
                _ = await RunAsync(["network", "connect", "--ip", network.Gateway, name, infraId!], cancellationToken).ConfigureAwait(false);
            else if (attached.GetProperty("IPAddress").GetString() != network.Gateway)
                throw new InvalidOperationException("Egress scope address does not match its managed attachment.");
            await ApplyBoundaryAsync(cancellationToken).ConfigureAwait(false);
            network = network with { Prepared = true };
            networks[name] = network;
            return network;
        }
        catch
        {
            ready = false;
            recoveryRequiresProxyStop = true;
            await StopProxyAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task LoadPrivateTopologyAsync(CancellationToken cancellationToken)
    {
        PodmanCommandResult inventory = await RunAsync(["network", "ls", "--filter", "label=" + ownerLabel + "=true", "--format", "{{.Name}}"], cancellationToken).ConfigureAwait(false);
        foreach (string name in inventory.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (name == outsideName) continue;
            using JsonDocument inspected = JsonDocument.Parse((await RunAsync(["network", "inspect", name], cancellationToken).ConfigureAwait(false)).StdOut);
            string? key = inspected.RootElement[0].GetProperty("labels").GetProperty("kautuka.egress.scope").GetString();
            if (!(name == coreName && key == "core") && (key is null || !IsId(key) || name != "kautuka-" + key[..32]))
                throw new InvalidOperationException("Unrecognized managed private topology.");
            networks[name] = await EnsureNetworkAsync(name, key!, isPrivate: true, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref activePrivateNetworks, networks.Count);
        }
    }

    private async Task PrepareRecoveredScopesAsync(CancellationToken cancellationToken)
    {
        if (networks.Count == 0) return;
        try
        {
            // The already-installed boundary covers every inventoried subnet before any attachment.
            var expected = networks.Values.ToDictionary(static network => network.Name, static network => network.Gateway, StringComparer.Ordinal);
            await ReconcileRecoveredAttachmentsAsync(expected,
                async token => JsonDocument.Parse((await RunAsync(["inspect", infraId!], token).ConfigureAwait(false)).StdOut),
                async (name, gateway, token) => { _ = await RunAsync(["network", "connect", "--ip", gateway, name, infraId!], token).ConfigureAwait(false); },
                cancellationToken).ConfigureAwait(false);
            // No package or collector source exception exists until RestoreSources validates allocations.
            await ApplyBoundaryAsync(cancellationToken).ConfigureAwait(false);
            foreach (string name in networks.Keys.ToArray()) networks[name] = networks[name] with { Prepared = true };
        }
        catch
        {
            ready = false;
            recoveryRequiresProxyStop = true;
            await StopProxyAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    internal static async Task ReconcileRecoveredAttachmentsAsync(IReadOnlyDictionary<string, string> expected,
        Func<CancellationToken, Task<JsonDocument>> inspect, Func<string, string, CancellationToken, Task> connect,
        CancellationToken cancellationToken)
    {
        using JsonDocument initial = await inspect(cancellationToken).ConfigureAwait(false);
        JsonElement attachments = initial.RootElement[0].GetProperty("NetworkSettings").GetProperty("Networks");
        foreach (string name in MissingRecoveredAttachments(attachments, expected, requireComplete: false))
            await connect(name, expected[name], cancellationToken).ConfigureAwait(false);
        using JsonDocument final = await inspect(cancellationToken).ConfigureAwait(false);
        JsonElement current = final.RootElement[0].GetProperty("NetworkSettings").GetProperty("Networks");
        _ = MissingRecoveredAttachments(current, expected, requireComplete: true);
    }

    internal static string[] MissingRecoveredAttachments(JsonElement attachments, IReadOnlyDictionary<string, string> expected, bool requireComplete)
    {
        var missing = new List<string>();
        foreach ((string name, string gateway) in expected.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            if (!attachments.TryGetProperty(name, out JsonElement attached))
            {
                if (requireComplete) throw new InvalidOperationException("Recovered egress scope attachment could not be verified.");
                missing.Add(name);
            }
            else if (attached.GetProperty("IPAddress").GetString() != gateway)
                throw new InvalidOperationException("Egress scope address does not match its managed attachment.");
        }
        return [.. missing];
    }

    private async Task<ScopeNetwork> EnsureNetworkAsync(string name, string scopeKey, bool isPrivate, CancellationToken cancellationToken)
    {
        NetworkAddressPool pool = NetworkAddressPool.Parse(options.NetworkPool);
        bool exists = await ExistsAsync(["network", "exists", name], cancellationToken).ConfigureAwait(false);
        // Startup already validated the complete inventory. Revalidate before each new
        // allocation and before adopting an existing network during steady state.
        HashSet<int> occupied = !exists || ready ? await ValidatePoolInventoryAsync(pool, cancellationToken).ConfigureAwait(false) : [];
        if (!exists)
        {
            var args = new List<string> { "network", "create", "--disable-dns", "--label", ownerLabel + "=true", "--label", "kautuka.egress.scope=" + scopeKey };
            if (isPrivate) args.Add("--internal");
            int slot = name == outsideName ? 0 : name == coreName ? 1 : Enumerable.Range(2, pool.SlotCount - 2).FirstOrDefault(candidate => !occupied.Contains(candidate), -1);
            if (slot < 0 || occupied.Contains(slot))
                throw new InvalidOperationException("The configured container egress network pool has no free /24 subnet for this scope. Choose a larger non-overlapping pool before creating a new topology.");
            args.AddRange(["--subnet", pool.Subnet(slot), "--gateway", pool.Gateway(slot)]);
            args.Add(name);
            _ = await RunAsync(args, cancellationToken).ConfigureAwait(false);
        }

        using JsonDocument inspected = JsonDocument.Parse((await RunAsync(["network", "inspect", name], cancellationToken).ConfigureAwait(false)).StdOut);
        JsonElement network = inspected.RootElement[0];
        JsonElement labels = network.GetProperty("labels");
        if (network.GetProperty("driver").GetString() != "bridge" || network.GetProperty("internal").GetBoolean() != isPrivate
            || network.GetProperty("dns_enabled").GetBoolean() || network.GetProperty("ipv6_enabled").GetBoolean()
            || labels.GetProperty(ownerLabel).GetString() != "true" || labels.GetProperty("kautuka.egress.scope").GetString() != scopeKey)
            throw new InvalidOperationException("Existing network does not match its managed private-scope configuration.");
        JsonElement subnets = network.GetProperty("subnets");
        if (subnets.GetArrayLength() != 1) throw new InvalidOperationException("Expected one native IPv4 subnet per managed network.");
        string subnet = subnets[0].GetProperty("subnet").GetString()!;
        if (!pool.TryGetSlot(subnet, out int actualSlot)
            || name == outsideName && actualSlot != 0 || name == coreName && actualSlot != 1
            || name is not (outsideName or coreName) && actualSlot < 2
            || !subnets[0].TryGetProperty("gateway", out JsonElement actualGateway) || actualGateway.GetString() != pool.Gateway(actualSlot))
            throw new InvalidOperationException("Existing managed network does not match the configured pool. Configure the original pool; automatic migration is not supported.");
        string[] parts = subnet.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out IPAddress? address) || address.AddressFamily != AddressFamily.InterNetwork
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int prefix) || prefix is < 8 or > 29)
            throw new InvalidOperationException("Native IPAM did not provide a usable IPv4 subnet.");
        byte[] bytes = address.GetAddressBytes();
        bytes[3] += 2;
        return new ScopeNetwork(name, subnet, new IPAddress(bytes).ToString(),
            subnets[0].TryGetProperty("gateway", out JsonElement gateway) ? gateway.GetString() : null);
    }

    private async Task ApplyBoundaryAsync(CancellationToken cancellationToken)
    {
        NetworkAddressPool pool = NetworkAddressPool.Parse(options.NetworkPool);
        pool.ValidateResolvers(dnsBoundaryUpstreams);
        await EnsurePrivateSourceAsync(cancellationToken).ConfigureAwait(false);
        string subnets = string.Join(", ", networks.Values.Select(static network => network.Subnet));
        string addresses = string.Join(", ", networks.Values.Select(static network => network.Gateway));
        PodmanCommandResult tables = await HelperAsync("/usr/sbin/nft", ["-j", "list", "tables"], cancellationToken, networkAdmin: true).ConfigureAwait(false);
        using JsonDocument parsed = JsonDocument.Parse(tables.StdOut);
        var text = new StringBuilder();
        foreach (JsonElement item in parsed.RootElement.GetProperty("nftables").EnumerateArray())
        {
            if (item.TryGetProperty("table", out JsonElement table) && table.GetProperty("name").GetString() == "kautuka_egress")
            {
                string? family = table.GetProperty("family").GetString();
                if (family is "ip" or "ip6") text.Append("delete table ").Append(family).AppendLine(" kautuka_egress");
            }
        }

        Source? package = sources.Values.SingleOrDefault(static source => source.Authorized && source.Identity.Role == "package");
        Source? telemetry = TelemetryEnabled ? sources.Values.SingleOrDefault(static source => source.Authorized && IsTelemetrySource(source.Identity)) : null;
        text.Append(NetworkDestinationPolicy.RenderBoundary(subnets, addresses, networks.GetValueOrDefault(coreName)?.Gateway,
            outsideSourceAddress, dnsBoundaryUpstreams, package?.Container.Address, telemetry?.Container.Address));
        await WriteAsync("boundary.nft", text.ToString() + "\n", cancellationToken).ConfigureAwait(false);
        _ = await HelperAsync("/usr/sbin/nft", ["-f", "/egress/boundary.nft"], cancellationToken, networkAdmin: true).ConfigureAwait(false);
        PodmanCommandResult routes = await HelperAsync("/usr/sbin/ip", ["-j", "-4", "route", "show", "default"], cancellationToken).ConfigureAwait(false);
        using JsonDocument defaults = JsonDocument.Parse(routes.StdOut);
        if (defaults.RootElement.GetArrayLength() != 1 || defaults.RootElement[0].GetProperty("gateway").GetString() != outsideDefaultGateway)
            throw new InvalidOperationException("The egress namespace lost its sole outside default route.");
        using (JsonDocument inspected = JsonDocument.Parse((await RunAsync(["inspect", infraId!], cancellationToken).ConfigureAwait(false)).StdOut))
        {
            JsonElement attachments = inspected.RootElement[0].GetProperty("NetworkSettings").GetProperty("Networks");
            if (!attachments.TryGetProperty(outsideName, out JsonElement outside)) throw new InvalidOperationException("The egress pod lost its outside attachment.");
            outsideSourceAddress = outside.GetProperty("IPAddress").GetString();
            if (!IPAddress.TryParse(outsideSourceAddress, out IPAddress? outsideAddress) || outsideAddress.AddressFamily != AddressFamily.InterNetwork)
                throw new InvalidOperationException("The outside attachment has no assigned IPv4 source address.");
            foreach (JsonProperty attachment in attachments.EnumerateObject())
            {
                if (attachment.Name == outsideName) continue;
                if (!networks.TryGetValue(attachment.Name, out ScopeNetwork? expected)
                    || attachment.Value.GetProperty("IPAddress").GetString() != expected.Gateway)
                    throw new InvalidOperationException("The egress pod has an unrecognized private attachment.");
            }
        }
        PodmanCommandResult forwarding = await HelperAsync("/usr/bin/cat", ["/proc/sys/net/ipv4/ip_forward"], cancellationToken).ConfigureAwait(false);
        if (forwarding.StdOut.Trim() != "0") throw new InvalidOperationException("The egress pod must not route packets directly.");
    }

    private async Task<List<string>> RestoreSourcesAsync(CancellationToken cancellationToken)
    {
        sources.Clear();
        var inactive = new List<string>();
        PodmanCommandResult listed = await RunAsync(["ps", "--all", "--filter", "label=" + ownerLabel + "=true", "--filter", "label=kautuka.egress.source=true", "--no-trunc", "--format", "{{.ID}}"], cancellationToken).ConfigureAwait(false);
        foreach (string id in listed.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!IsId(id)) throw new InvalidOperationException("Invalid managed source inventory ID.");
            using JsonDocument inspected = JsonDocument.Parse((await RunAsync(["inspect", id], cancellationToken).ConfigureAwait(false)).StdOut);
            JsonElement container = inspected.RootElement[0];
            JsonElement labels = container.GetProperty("Config").GetProperty("Labels");
            string? state = container.GetProperty("State").GetProperty("Status").GetString();
            string? role = labels.GetProperty("kautuka.egress.source-role").GetString();
            if (role is not ("connector" or "agent" or "package" or "materializer" or "core")) throw new InvalidOperationException("Unknown managed source role.");
            if (retiring.Contains(id) || role == "materializer" && !ownedMaterializers.Contains(id)) { inactive.Add(id); continue; }
            if (state is not ("running" or "initialized" or "paused"))
            {
                // A live request may still need its exited installer's filesystem for commit.
                if (role != "materializer") inactive.Add(id);
                continue;
            }
            string? kind = labels.GetProperty("kautuka.egress.scope-kind").GetString();
            if (kind is not ("agent" or "management" or "core")) throw new InvalidOperationException("Unknown managed connector scope kind.");
            var scope = new Scope(labels.GetProperty("kautuka.egress.user").GetString()!, labels.GetProperty("kautuka.egress.agent").GetString()!, kind);
            ScopeNetwork network = await EnsureScopeCoreAsync(scope.NetworkName, scope.Key, cancellationToken).ConfigureAwait(false);
            JsonElement attachments = container.GetProperty("NetworkSettings").GetProperty("Networks");
            if (attachments.EnumerateObject().Count() != 1 || !attachments.TryGetProperty(network.Name, out JsonElement attachment))
                throw new InvalidOperationException("Managed source has unexpected network attachments.");
            string? address = attachment.GetProperty("IPAddress").GetString();
            string? sandbox = container.GetProperty("NetworkSettings").GetProperty("SandboxKey").GetString();
            if (!IPAddress.TryParse(address, out IPAddress? parsed) || parsed.AddressFamily != AddressFamily.InterNetwork || string.IsNullOrWhiteSpace(sandbox))
                throw new InvalidOperationException("An allocated managed source has no valid IPv4 namespace identity.");
            if (sources.Values.Any(source => source.Container.NetworkName == network.Name && source.Container.Address == address))
                throw new InvalidOperationException("Native source ownership is ambiguous; egress recovery is deferred.");
            var prepared = new PreparedPodmanContainer(id, network.Name, address!, sandbox);
            var identity = new SourceIdentity(scope, role, labels.GetProperty("kautuka.egress.component").GetString()!,
                labels.GetProperty("kautuka.egress.packages").GetString() == "true");
            sources[id] = new Source(prepared, container.GetProperty("Name").GetString()!.TrimStart('/'), identity, Authorized: !retiring.Contains(id));
        }
        return inactive;
    }

    private async Task StopProxyAsync(CancellationToken cancellationToken)
    {
        Volatile.Write(ref authorization, null);
        if (!await ExistsAsync(["container", "exists", "kautuka-egress-envoy"], cancellationToken).ConfigureAwait(false)) return;
        _ = await runner.RunAsync(["stop", "--time", "1", "kautuka-egress-envoy"], 15, cancellationToken).ConfigureAwait(false);
        using JsonDocument inspected = JsonDocument.Parse((await RunAsync(["inspect", "kautuka-egress-envoy"], cancellationToken).ConfigureAwait(false)).StdOut);
        if (inspected.RootElement[0].GetProperty("State").GetProperty("Running").GetBoolean())
            throw new InvalidOperationException("Uncertain policy could not be blackholed; no source allocation may be released.");
    }

    // The update helper has stopped the long-lived Manager before this path.
    // Recovery must be able to close egress without loading bootstrap images,
    // publishing policy or starting any container.
    internal async Task StopForUpdateAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ready = false;
            active = null;
            Volatile.Write(ref authorization, null);
            string? json = await PodmanContainerInspector.ReadAsync(runner, "kautuka-egress-envoy", cancellationToken).ConfigureAwait(false);
            if (json is null) return;
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement container = PodmanContainerInspector.Container(document.RootElement);
            string? id = container.GetProperty("Id").GetString();
            if (id is null || !IsId(id)
                || !container.GetProperty("Config").GetProperty("Labels").TryGetProperty(ownerLabel, out JsonElement owner)
                || owner.GetString() != "true")
                throw new InvalidOperationException("The egress proxy is not a recognized managed container; update shutdown is deferred.");
            if (container.GetProperty("State").GetProperty("Running").GetBoolean())
                _ = await RunAsync(["stop", "--time", "1", id], cancellationToken).ConfigureAwait(false);
            string? stopped = await PodmanContainerInspector.ReadAsync(runner, id, cancellationToken).ConfigureAwait(false);
            if (stopped is null) return;
            using JsonDocument observed = JsonDocument.Parse(stopped);
            JsonElement actual = PodmanContainerInspector.Container(observed.RootElement);
            if (actual.GetProperty("Id").GetString() != id || actual.GetProperty("State").GetProperty("Running").GetBoolean())
                throw new InvalidOperationException("The egress proxy could not be confirmed stopped; update shutdown is deferred.");
            await TryFinalNetworkAuditAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task RetireEmptyScopesAsync(CancellationToken cancellationToken)
    {
        PodmanCommandResult inventory = await RunAsync(["network", "ls", "--filter", "label=" + ownerLabel + "=true", "--format", "{{.Name}}"], cancellationToken).ConfigureAwait(false);
        foreach (string name in inventory.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (name is coreName or outsideName || networks.ContainsKey(name)) continue;
            using JsonDocument inspected = JsonDocument.Parse((await RunAsync(["network", "inspect", name], cancellationToken).ConfigureAwait(false)).StdOut);
            string? scopeKey = inspected.RootElement[0].GetProperty("labels").GetProperty("kautuka.egress.scope").GetString();
            if (scopeKey is null || !IsId(scopeKey) || name != "kautuka-" + scopeKey[..32])
                throw new InvalidOperationException("Unexpected managed scope identity; automatic network retirement is deferred.");
            networks.Add(name, await EnsureNetworkAsync(name, scopeKey, isPrivate: true, cancellationToken).ConfigureAwait(false));
            Volatile.Write(ref activePrivateNetworks, networks.Count);
        }
        foreach (ScopeNetwork network in networks.Values.Where(static item => item.Name != coreName).ToArray())
        {
            if (sources.Values.Any(source => source.Container.NetworkName == network.Name)) continue;
            PodmanCommandResult listed = await RunAsync(["ps", "--all", "--filter", "network=" + network.Name, "--no-trunc", "--format", "{{.ID}}"], cancellationToken).ConfigureAwait(false);
            // The pod's trusted containers may appear here; never detach a network with another client.
            bool client = false;
            foreach (string id in listed.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                using JsonDocument inspected = JsonDocument.Parse((await RunAsync(["inspect", id], cancellationToken).ConfigureAwait(false)).StdOut);
                if (id != infraId && inspected.RootElement[0].GetProperty("Pod").GetString() != podId) { client = true; break; }
            }
            if (client) continue;
            using (JsonDocument inspected = JsonDocument.Parse((await RunAsync(["inspect", infraId!], cancellationToken).ConfigureAwait(false)).StdOut))
            {
                if (inspected.RootElement[0].GetProperty("NetworkSettings").GetProperty("Networks").TryGetProperty(network.Name, out _))
                    _ = await RunAsync(["network", "disconnect", network.Name, infraId!], cancellationToken).ConfigureAwait(false);
            }
            _ = await RunAsync(["network", "rm", network.Name], cancellationToken).ConfigureAwait(false);
            networks.Remove(network.Name);
            Volatile.Write(ref activePrivateNetworks, networks.Count);
            await ApplyBoundaryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private EnvoyEgressConfiguration CompileCurrent()
    {
        Source[] authorized = [.. sources.Values.Where(static source => source.Authorized)];
        Source? package = authorized.SingleOrDefault(static source => source.Identity.Role == "package");
        policySources = [.. authorized.Select(source => new NetworkAccessIdentity(source.Identity.Scope.Kind,
            source.Identity.Scope.UserId, source.Identity.Scope.AgentId, source.Identity.Role, source.Identity.ComponentId)).Distinct()];
        policyDefaults = [.. options.Grants, .. HostedProviderDefaults(), .. package is null ? Array.Empty<ContainerEgressGrant>() : packageUpstreams.Select(host => new ContainerEgressGrant
        {
            ScopeType = package.Identity.Scope.Kind, UserId = package.Identity.Scope.UserId, AgentId = package.Identity.Scope.AgentId,
            Role = package.Identity.Role, ComponentId = package.Identity.ComponentId, Destination = host, Port = 443,
        })];
        IReadOnlyList<ContainerEgressGrant> effective = networkPolicy.EffectiveGrants(policyDefaults);
        RemoteConnectorPermission[] remotePermissions = RemotePermissions(effective);
        // Agent grants authorize their own agent/container sources only. Gateway
        // routes come from explicit core policy or built-in Gateway defaults.
        IReadOnlyList<ContainerEgressGrant> grants = effective;
        EgressTcpRule[] rules = [.. authorized.SelectMany(source => grants
            .Where(grant => GrantAppliesToSource(grant, source.Identity))
            .Select(grant => EnvoyEgressConfiguration.Normalize(new EgressTcpRule(source.Container.Address, grant.Destination, grant.Port, grant.AllowPrivateNetwork)))).Distinct().OrderBy(rule => rule.SourceAddress, StringComparer.Ordinal).ThenBy(rule => rule.Destination, StringComparer.Ordinal).ThenBy(rule => rule.Port).ThenBy(rule => rule.AllowPrivateNetwork)];
        EgressPackageRoute[] routes = package is null ? [] : [.. authorized.Where(static source => source.Identity.PackageEligible)
            .Select(source => new EgressPackageRoute(source.Container.Address, networks[source.Container.NetworkName].Gateway, package.Container.Address, networks[coreName].Gateway))];
        Source? collector = TelemetryEnabled ? authorized.SingleOrDefault(static source => IsTelemetrySource(source.Identity)) : null;
        EgressTelemetryTarget? telemetry = collector is null ? null : new EgressTelemetryTarget(collector.Container.Address, networks[coreName].Gateway,
            [.. authorized.Where(static source => source.Identity.Role == "core" && source.Identity.Scope.Kind == "core" && !IsTelemetrySource(source.Identity))
                .Select(source => new EgressTelemetryRoute(source.Container.Address, networks[coreName].Gateway))]);
        string allocationVersion = networkAudit.RecordAllocations(sources.Values.Select(source => new NetworkAccessAllocation(
            source.Container.Address, source.Container.Id, new NetworkAccessIdentity(source.Identity.Scope.Kind, source.Identity.Scope.UserId,
                source.Identity.Scope.AgentId, source.Identity.Role, source.Identity.ComponentId))));
        var identities = authorized.OrderBy(source => source.Container.Address, StringComparer.Ordinal).ToDictionary(source => source.Container.Address,
            source => new NetworkAccessIdentity(source.Identity.Scope.Kind, source.Identity.Scope.UserId, source.Identity.Scope.AgentId, source.Identity.Role, source.Identity.ComponentId), StringComparer.Ordinal);
        var generationData = new JsonObject
        {
            ["allocation"] = allocationVersion,
            ["policyRevision"] = networkPolicy.Revision,
            ["remotePermissions"] = new JsonArray([.. remotePermissions.Select(item => (JsonNode)new JsonArray(item.Identity.ScopeType,
                item.Identity.UserId, item.Identity.AgentId, item.Identity.Role, item.Identity.ComponentId, item.Destination, item.Port, item.AllowPrivateNetwork))]),
            ["sources"] = new JsonArray([.. identities.Select(pair => (JsonNode)new JsonArray(pair.Key, pair.Value.ScopeType, pair.Value.UserId, pair.Value.AgentId, pair.Value.Role, pair.Value.ComponentId))]),
            ["rules"] = new JsonArray([.. rules.Select(rule => (JsonNode)new JsonArray(rule.SourceAddress, rule.Destination, rule.Port, rule.AllowPrivateNetwork))])
        };
        string generation = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(generationData.ToJsonString())));
        var snapshot = new EgressAuthorizationSnapshot(generation, identities, rules,
            authorized.ToDictionary(source => source.Container.Address, source => source.Container.Id, StringComparer.Ordinal))
        { RemotePermissions = remotePermissions, PolicyRevision = networkPolicy.Revision };
        return EnvoyEgressConfiguration.Compile(rules, outsideSourceAddress ?? throw new InvalidOperationException("Missing outside source address."), routes, options.ArtifactHosts, allocationVersion, telemetry, TelemetryEnabled, NetworkDestinationPolicy.PrivateSourceAddress, generation) with { AuthorizationSnapshot = snapshot };
    }

    private async Task<EnvoyEgressConfiguration> ReadActiveConfigurationAsync(CancellationToken cancellationToken)
    {
        PodmanCommandResult result = await HelperAsync("/usr/bin/curl", AdminProbeArguments(), cancellationToken).ConfigureAwait(false);
        JsonArray configs = JsonNode.Parse(result.StdOut)!["configs"]!.AsArray();
        JsonObject clusters = configs.OfType<JsonObject>().Single(config => config["@type"]!.GetValue<string>().EndsWith(".ClustersConfigDump", StringComparison.Ordinal));
        JsonObject listeners = configs.OfType<JsonObject>().Single(config => config["@type"]!.GetValue<string>().EndsWith(".ListenersConfigDump", StringComparison.Ordinal));
        var activeClusters = new JsonArray();
        foreach (JsonNode? item in clusters["dynamic_active_clusters"]?.AsArray() ?? [])
            activeClusters.Add((JsonNode)item!["cluster"]!.DeepClone());
        var activeListeners = new JsonArray();
        foreach (JsonNode? item in listeners["dynamic_listeners"]?.AsArray() ?? [])
        {
            string name = item!["name"]!.GetValue<string>();
            if (name is not (EnvoyEgressConfiguration.ListenerName or EnvoyEgressConfiguration.ArtifactListenerName))
                throw new InvalidOperationException("The managed proxy has an unrecognized listener.");
            activeListeners.Add(item["active_state"]!["listener"]!.DeepClone());
        }
        JsonNode listener = activeListeners.Single(item => item!["name"]!.GetValue<string>() == EnvoyEgressConfiguration.ListenerName)!;
        string version = listener["metadata"]!["filter_metadata"]!["kautuka.egress"]!["policy_version"]!.GetValue<string>();
        if (!IsId(version)) throw new InvalidOperationException("The active proxy has no recognized policy marker.");
        string clusterDocument = new JsonObject { ["version_info"] = version, ["resources"] = activeClusters }.ToJsonString();
        string listenerDocument = new JsonObject { ["version_info"] = version, ["resources"] = activeListeners }.ToJsonString();
        return new EnvoyEgressConfiguration(version, clusterDocument, listenerDocument,
            [.. activeClusters.Select(item => item!["name"]!.GetValue<string>())]);
    }

    private async Task PublishAsync(EnvoyEgressConfiguration next, CancellationToken cancellationToken)
    {
        EnvoyEgressConfiguration? previous = active;
        EgressAuthorizationSnapshot candidate = next.AuthorizationSnapshot
            ?? throw new InvalidOperationException("A policy publication requires its bound authorization snapshot.");
        BeginAuthorizationTransition(candidate);
        try
        {
            await ActivateAsync(next, previous, cancellationToken).ConfigureAwait(false);
            active = next;
            CompleteAuthorizationTransition(candidate);
            Interlocked.Exchange(ref activePolicyRevision, networkPolicy.Revision);
            policyActivationError = null;
        }
        catch (Exception activationError)
        {
            if (previous is not null)
            {
                try { await ActivateAsync(previous, next, CancellationToken.None).ConfigureAwait(false); }
                catch (Exception recoveryError)
                {
                    ready = false;
                    Volatile.Write(ref authorization, null);
                    recoveryRequiresProxyStop = true;
                    try { await StopProxyAsync(CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception stopError) { throw new AggregateException("Egress policy activation, recovery and proxy shutdown failed.", activationError, recoveryError, stopError); }
                    throw new AggregateException("Egress policy activation and recovery failed; the proxy was stopped.", activationError, recoveryError);
                }
            }

            RestoreAuthorizationTransition();
            throw;
        }
    }

    private async Task ActivateAsync(EnvoyEgressConfiguration next, EnvoyEgressConfiguration? previous, CancellationToken cancellationToken)
    {
        // Stage a union so the currently accepted listener never loses a referenced cluster.
        var resources = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (string document in previous is null ? new[] { next.ClusterDocument } : new[] { previous.ClusterDocument, next.ClusterDocument })
        {
            foreach (JsonNode? resource in JsonNode.Parse(document)!["resources"]!.AsArray())
                resources[resource!["name"]!.GetValue<string>()] = resource.DeepClone();
        }

        string stageVersion = next.Version + "-stage";
        string stageDocument = ClusterPublicationDocument(stageVersion, resources.Values);
        var stage = new EnvoyEgressConfiguration(stageVersion, stageDocument, next.ListenerDocument, [.. resources.Keys]);
        await WriteAsync("cds.json", stageDocument, cancellationToken).ConfigureAwait(false);
        await AwaitActiveAsync(stage, clusters: true, cancellationToken).ConfigureAwait(false);
        await WriteAsync("lds.json", next.ListenerDocument, cancellationToken).ConfigureAwait(false);
        await AwaitActiveAsync(next, clusters: false, cancellationToken).ConfigureAwait(false);
        await WriteAsync("cds.json", ClusterPublicationDocument(next.Version, JsonNode.Parse(next.ClusterDocument)!["resources"]!.AsArray().Select(static item => item!)), cancellationToken).ConfigureAwait(false);
        await AwaitActiveAsync(next, clusters: true, cancellationToken).ConfigureAwait(false);
    }

    private static string ClusterPublicationDocument(string version, IEnumerable<JsonNode> resources)
    {
        // File subscriptions can ignore version-only changes. Mark the actual resources too.
        var published = new JsonArray();
        foreach (JsonNode resource in resources)
        {
            JsonNode clone = resource.DeepClone();
            clone["metadata"]!["filter_metadata"]!["kautuka.egress"]!["publication_version"] = version;
            published.Add(clone);
        }
        return new JsonObject { ["version_info"] = version, ["resources"] = published }.ToJsonString();
    }

    private async Task AwaitActiveAsync(EnvoyEgressConfiguration expected, bool clusters, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        string observed = "";
        do
        {
            PodmanCommandResult result = await HelperAsync("/usr/bin/curl", AdminProbeArguments(), cancellationToken).ConfigureAwait(false);
            observed = result.StdOut;
            using JsonDocument dump = JsonDocument.Parse(result.StdOut);
            if (clusters ? expected.ClustersAreActive(dump.RootElement) : expected.ListenerIsActive(dump.RootElement)) return;
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        } while (DateTimeOffset.UtcNow < deadline);
        await WriteAsync("unconfirmed-admin-dump.json", observed, CancellationToken.None).ConfigureAwait(false);
        throw new InvalidOperationException("Envoy did not confirm " + (clusters ? "clusters " : "listener ") + expected.Version + "; workload startup is denied.");
    }

    // Podman detach/start confirms process creation before Envoy opens admin.
    // Retry that native connection race; command, JSON and version failures
    // remain strict. The helper's process timeout is still the outer bound.
    internal static string[] AdminProbeArguments() =>
        ["--silent", "--show-error", "--fail", "--max-time", "2", "--retry", "8", "--retry-connrefused",
            "--retry-delay", "1", "--retry-max-time", "8", "http://127.0.0.1:9901/config_dump"];

    private async Task<PodmanCommandResult> HelperAsync(string entrypoint, string[] arguments, CancellationToken cancellationToken, bool networkAdmin = false, string? networkNamespace = null, bool dnsProbe = false)
    {
        string operation = Guid.NewGuid().ToString("N");
        var args = new List<string> { "run", "--rm", "--pull=never", "--pod", podName, "--cap-drop=ALL", "--security-opt=no-new-privileges",
            "--label", "kautuka.preparation=" + operation, "--label", "kautuka.preparation-helper=true",
            "--read-only", "--user", networkAdmin ? "0:0" : "65533:65533", "--volume", configurationRoot + ":/egress:ro", "--entrypoint", entrypoint };
        if (networkNamespace is not null)
        {
            int pod = args.IndexOf("--pod"); args.RemoveRange(pod, 2);
            args.InsertRange(1, ["--network", "ns:" + networkNamespace]);
        }
        if (networkAdmin) args.Add("--cap-add=NET_ADMIN");
        if (dnsProbe) args.InsertRange(args.IndexOf("--entrypoint"), ["--volume", Path.Combine(configurationRoot, "dns-probe-resolv.conf") + ":/etc/resolv.conf:ro"]);
        args.Add(helperImage!.LocalImageId);
        args.AddRange(arguments);
        pendingHelpers.Add(operation);
        try
        {
            PodmanCommandResult result = await RunAsync(args, cancellationToken).ConfigureAwait(false);
            pendingHelpers.Remove(operation);
            return result;
        }
        catch (Exception helperError)
        {
            try
            {
                await new PodmanContainerPreparation(runner).RetireOperationAsync(operation).ConfigureAwait(false);
                pendingHelpers.Remove(operation);
            }
            catch (Exception cleanupError)
            {
                ready = false;
                throw new AggregateException("Egress helper and its cleanup failed; readiness is invalid.", helperError, cleanupError);
            }
            throw;
        }
    }

    private async Task RecoverHelpersAsync(CancellationToken cancellationToken)
    {
        PodmanCommandResult listed = await RunAsync(["ps", "--all", "--filter", "label=kautuka.preparation-helper=true", "--no-trunc", "--format", "{{.ID}}"], cancellationToken).ConfigureAwait(false);
        foreach (string id in listed.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!IsId(id)) throw new InvalidOperationException("Invalid preparation helper identity during recovery.");
            using JsonDocument inspected = JsonDocument.Parse((await RunAsync(["inspect", id], cancellationToken).ConfigureAwait(false)).StdOut);
            string? operation = inspected.RootElement[0].GetProperty("Config").GetProperty("Labels").GetProperty("kautuka.preparation").GetString();
            if (operation?.Length != 32 || !operation.All(char.IsAsciiHexDigit)) throw new InvalidOperationException("Invalid preparation ownership during recovery.");
            pendingHelpers.Add(operation);
        }
        foreach (string operation in pendingHelpers.ToArray())
        {
            await new PodmanContainerPreparation(runner).RetireOperationAsync(operation).ConfigureAwait(false);
            pendingHelpers.Remove(operation);
        }
    }

    private async Task WriteAsync(string name, string content, CancellationToken cancellationToken)
    {
        string target = Path.Combine(configurationRoot, name);
        string temporary = target + ".next";
        await File.WriteAllTextAsync(temporary, content, cancellationToken).ConfigureAwait(false);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        File.Move(temporary, target, overwrite: true);
    }

    private async Task<bool> ExistsAsync(string[] arguments, CancellationToken cancellationToken)
    {
        PodmanCommandResult result = await runner.RunAsync(arguments, 15, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return result.ExitCode switch
        {
            0 => true,
            1 when result.TerminationReason == ProcessTerminationReason.NonZeroExit => false,
            _ => throw new InvalidOperationException(PodmanFailureFormatter.FormatFailure("Inspect managed egress resource", result))
        };
    }

    private async Task<PodmanCommandResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        PodmanCommandResult result = await runner.RunAsync(arguments, 30, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.ExitCode != 0) throw new InvalidOperationException(PodmanFailureFormatter.FormatFailure("Manager egress operation", result));
        return result;
    }

    private static bool IsId(string id) => id.Length == 64 && id.All(char.IsAsciiHexDigit);
    private static bool CapabilitiesMatch(JsonElement capabilities, bool dns)
    {
        if (capabilities.ValueKind == JsonValueKind.Null) return !dns;
        if (capabilities.ValueKind != JsonValueKind.Array) return false;
        return dns ? capabilities.GetArrayLength() == 1 && capabilities[0].GetString() == "CAP_NET_BIND_SERVICE"
            : capabilities.GetArrayLength() == 0;
    }
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
