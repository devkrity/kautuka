using System.Security.Cryptography;
using System.Text.Json;

using Kautuka.Hosting.Hosting;
using Kautuka.Manager.Protos;
using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.Telemetry.Security;
using Kautuka.Telemetry.Telemetry;

namespace Kautuka.Manager.Hosting;

/// <summary>Supervises the application containers described by the selected signed appliance release.</summary>
internal sealed partial class ApplianceServiceSupervisor(
    PodmanCommandRunner podman,
    VerifiedApplianceRelease release,
    string versionDirectory,
    string stateRoot,
    string socketRoot,
    ILogger<ApplianceServiceSupervisor> logger,
    EgressTopologyManager? topology = null,
    string? gatewayConnectDirectory = null,
    PodmanImageArchiveStore? imageArchives = null,
    bool superviseObservability = false,
    bool superviseUpdateService = false,
    Func<CancellationToken, Task<bool>>? checkWebHostReadiness = null,
    bool verificationMode = false) : BackgroundService
{
    private const string ManagedLabel = "io.kautuka.appliance.service";
    private const string ReleaseLabel = "io.kautuka.appliance.release";
    private const string CredentialsRole = "credentialshost";
    private const string ChannelRole = "channelhost";
    private const string GatewayRole = "gateway";
    private const string WebHostRole = "webhost";
    private const string ObservabilityRole = "observability";
    private const string UpdateRole = "updateservice";
    private static readonly TimeSpan reconcileDelay = TimeSpan.FromSeconds(5);
    private readonly Service credentials = new(CredentialsRole, "credentialsHost", superviseObservability);
    private readonly Service channel = new(ChannelRole, "channelHost", superviseObservability);
    private readonly Service gateway = new(GatewayRole, "gateway", networked: true);
    private readonly Service webHost = new(WebHostRole, "webhost", networked: true);
    private readonly Service observability = new(ObservabilityRole, ApplianceTelemetry.StateName, networked: true);
    private readonly Service updater = new(UpdateRole, "updateService", networked: true);
    private volatile bool cohortReady;

    internal bool IsReady => cohortReady && topology?.IsStartupGateOpen == true;

    private sealed class Service(string role, string stateName, bool networked)
    {
        internal string Role { get; } = role;
        internal string StateName { get; } = stateName;
        internal string ContainerName { get; } = $"kautuka-appliance-{role}";
        internal string? NetworkRole { get; } = networked ? $"appliance-{role}" : null;
        internal string? RuntimeImageId { get; set; }
        internal string? ActiveContainer { get; set; }
        internal bool Ready { get; set; }
        internal int FailedProbes { get; set; }
        internal bool IsHealthy => Ready && FailedProbes == 0;

        internal void ResetReadiness()
        {
            Ready = false;
            FailedProbes = 0;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var imageIds = await new ApplianceRoleImageImporter(podman)
                .EnsureAsync(release.Deployment, versionDirectory, stoppingToken, requireVerificationFence: verificationMode).ConfigureAwait(false);
            foreach (Service service in new[] { credentials, channel, gateway, webHost, updater })
                service.RuntimeImageId = imageIds[service.Role];
            using var timer = new PeriodicTimer(reconcileDelay);
            do
            {
                if ((!verificationMode && KautukaMaintenanceMode.IsActive) || (superviseObservability && topology?.IsStartupGateOpen != true))
                    await StopCohortAsync(stoppingToken).ConfigureAwait(false);
                else
                    await ReconcileServicesAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await StopCohortAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    internal async Task ReconcileServicesAsync(CancellationToken token)
    {
        if (superviseObservability)
            await TryReconcileAsync(observability, token).ConfigureAwait(false);
        await TryReconcileAsync(credentials, token).ConfigureAwait(false);
        await TryReconcileAsync(channel, token).ConfigureAwait(false);
        if (superviseUpdateService && topology?.IsStartupGateOpen == true)
            await TryReconcileAsync(updater, token).ConfigureAwait(false);
        if (topology?.IsStartupGateOpen == true && credentials.Ready && channel.Ready
            && (!superviseUpdateService || updater.Ready))
            await TryReconcileAsync(gateway, token).ConfigureAwait(false);
        else
            await StopActiveAsync(gateway, token).ConfigureAwait(false);
        if (gateway.Ready && topology?.IsStartupGateOpen == true)
            await TryReconcileAsync(webHost, token).ConfigureAwait(false);
        else
            await StopActiveAsync(webHost, token).ConfigureAwait(false);
        cohortReady = credentials.IsHealthy && channel.IsHealthy && gateway.IsHealthy && webHost.IsHealthy
            && (!superviseUpdateService || updater.IsHealthy) && (!superviseObservability || observability.Ready);
    }

    internal async Task<bool> StopCohortAsync(CancellationToken token)
    {
        cohortReady = false;
        Service[] services = [webHost, gateway, updater, channel, credentials, observability];
        var running = new Dictionary<Service, string>();
        bool stopped = true;
        try
        {
            PodmanCommandResult result = await podman.RunAsync(
                ["ps", "--no-trunc", "--filter", $"label={ManagedLabel}", "--format", "json"], 15, token)
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (result.ExitCode != 0)
                throw new InvalidOperationException(PodmanFailureFormatter.FormatFailure("List running appliance containers", result));

            using JsonDocument containers = JsonDocument.Parse(result.StdOut);
            foreach (JsonElement container in containers.RootElement.EnumerateArray())
            {
                JsonElement labels = container.GetProperty("Labels");
                JsonElement names = container.GetProperty("Names");
                Service? service = services.FirstOrDefault(service => labels.ValueKind == JsonValueKind.Object
                    && HasLabel(labels, ManagedLabel, service.Role)
                    && names.EnumerateArray().Any(name => name.GetString() == service.ContainerName));
                if (service is not null)
                    running.Add(service, RequireContainerId(container.GetProperty("Id").GetString()));
            }
        }
        catch (Exception ex) when (!token.IsCancellationRequested &&
            ex is IOException or InvalidDataException or InvalidOperationException or JsonException or KeyNotFoundException or ArgumentException)
        {
            LogReconcileFailed(logger, "cohort", ex.Message);
            stopped = false;
        }

        foreach (Service service in services)
        {
            string? trackedId = service.ActiveContainer;
            stopped &= await StopActiveAsync(service, token).ConfigureAwait(false);
            if (running.TryGetValue(service, out string? id) && id != trackedId)
                stopped &= await StopContainerAsync(service, id, token).ConfigureAwait(false);
        }
        return stopped;
    }

    private async Task<bool> StopActiveAsync(Service service, CancellationToken token)
    {
        service.Ready = false;
        if (service.ActiveContainer is not { } id)
            return true;
        if (!await StopContainerAsync(service, id, token).ConfigureAwait(false))
            return false;
        service.ActiveContainer = null;
        service.ResetReadiness();
        return true;
    }

    private async Task<bool> StopContainerAsync(Service service, string id, CancellationToken token)
    {
        PodmanCommandResult result = await podman.RunAsync(["stop", "--ignore", "--time", "10", id], 20, token)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            LogReconcileFailed(logger, service.Role, PodmanFailureFormatter.FormatFailure("Container stop", result));
            return false;
        }
        if (service.NetworkRole is { } role) topology?.RevokeApplianceService(role, id);
        return true;
    }

    private async Task TryReconcileAsync(Service service, CancellationToken token)
    {
        try
        {
            await ReconcileAsync(service, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!token.IsCancellationRequested &&
            ex is IOException or InvalidDataException or InvalidOperationException or JsonException or KeyNotFoundException)
        {
            service.ResetReadiness();
            LogReconcileFailed(logger, service.Role, ex.Message);
        }
    }

    private async Task ReconcileAsync(Service service, CancellationToken token)
    {
        await EnsureRunningAsync(service, token).ConfigureAwait(false);
        if (service.Role == ObservabilityRole)
        {
            service.Ready = true;
            return;
        }
        string socket = Path.Combine(socketRoot, service.Role, service.Role + ".sock");
        string transport = Path.Combine(stateRoot, service.StateName, "transport");
        string security = Path.Combine(stateRoot, "security", "channel");
        bool isChannel = service.Role == ChannelRole;
        bool healthy = service.Role switch
        {
            GatewayRole => await ApplianceServiceReadiness.CheckGatewayAsync(socket, token).ConfigureAwait(false),
            WebHostRole => await (checkWebHostReadiness?.Invoke(token) ?? ApplianceServiceReadiness.CheckWebHostAsync(token)).ConfigureAwait(false),
            UpdateRole => await ApplianceServiceReadiness.CheckAsync(socket,
                Path.Combine(stateRoot, service.StateName, ".ca.cer"),
                Path.Combine(stateRoot, service.StateName, ".updateservice-client.pfx"), token).ConfigureAwait(false),
            _ => await ApplianceServiceReadiness.CheckAsync(
            socket,
            isChannel ? Path.Combine(security, "channel-trust", ChannelSecurityMaterialProvisioner.CaCertificate) : Path.Combine(transport, ".ca.cer"),
            isChannel ? Path.Combine(security, "gateway-private", ChannelSecurityMaterialProvisioner.GatewayClientPfx) : Path.Combine(transport, ".credentials-client.pfx"),
            token)
            .ConfigureAwait(false),
        };
        if (healthy)
        {
            if (!service.Ready) LogServiceReady(logger, service.Role, release.ReleaseId);
            service.Ready = true;
            service.FailedProbes = 0;
        }
        else if (++service.FailedProbes >= 6)
        {
            LogReconcileFailed(logger, service.Role, "Consecutive local health checks failed");
            await StopActiveAsync(service, token).ConfigureAwait(false);
        }
    }

    internal Task<string> EnsureRunningAsync(string role, CancellationToken token)
        => EnsureRunningAsync(role switch
        {
            CredentialsRole => credentials,
            ChannelRole => channel,
            GatewayRole => gateway,
            WebHostRole => webHost,
            ObservabilityRole when superviseObservability => observability,
            UpdateRole when superviseUpdateService => updater,
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        }, token);

    private async Task<string> EnsureRunningAsync(Service service, CancellationToken token)
    {
        string image = service.Role == ObservabilityRole ? EgressTopologyManager.ObservabilityPinnedImage : release.Deployment.Images[service.Role];
        string? role = service.NetworkRole;
        if (role is not null && topology?.IsStartupGateOpen != true)
            throw new InvalidOperationException("The service cannot start before the appliance egress boundary is ready.");
        string? runtimeImage = role is not null
            ? service.RuntimeImageId ??= await ResolveImageIdAsync(service, image, token).ConfigureAwait(false)
            : null;
        PodmanCommandResult inspection = await podman.RunAsync(["container", "inspect", service.ContainerName], 15, token)
            .ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (inspection.ExitCode != 0)
        {
            PodmanCommandResult exists = await podman.RunAsync(["container", "exists", service.ContainerName], 10, token)
                .ConfigureAwait(false);
            if (exists.ExitCode == 1)
                return await CreateAndStartAsync(service, image, runtimeImage, token).ConfigureAwait(false);
            throw new InvalidOperationException(PodmanFailureFormatter.FormatFailure($"{service.Role} inspect", inspection));
        }

        using JsonDocument document = JsonDocument.Parse(inspection.StdOut);
        JsonElement container = document.RootElement.EnumerateArray().Single();
        string id = RequireContainerId(container.GetProperty("Id").GetString());
        JsonElement labels = container.GetProperty("Config").GetProperty("Labels");
        if (labels.ValueKind != JsonValueKind.Object || !HasLabel(labels, ManagedLabel, service.Role))
            throw new InvalidOperationException($"The {service.Role} container name is in use outside appliance supervision.");

        JsonElement host = container.GetProperty("HostConfig");
        if (!HasLabel(labels, ReleaseLabel, release.ReleaseId)
            || container.GetProperty("ImageName").GetString() != image
            || (runtimeImage is null ? host.GetProperty("NetworkMode").GetString() != "none"
                : !MatchesImageId(container.GetProperty("Image").GetString(), runtimeImage)
                    || !HasExpectedProfile(container, BuildCreateArgs(service, image, runtimeImage)))
            || !host.GetProperty("ReadonlyRootfs").GetBoolean())
        {
            await RequireSuccessAsync(service, ["rm", "--force", "--time", "10", id], 25, token).ConfigureAwait(false);
            if (role is not null) topology!.RevokeApplianceService(role, id);
            return await CreateAndStartAsync(service, image, runtimeImage, token).ConfigureAwait(false);
        }

        if (service.ActiveContainer != id)
        {
            service.ActiveContainer = id;
            service.ResetReadiness();
        }
        bool running = container.GetProperty("State").GetProperty("Running").GetBoolean();
        if (runtimeImage is not null)
        {
            if (!running) service.ResetReadiness();
            await VerifyNetworkedServiceAsync(service, id, runtimeImage, running ? container : null, token).ConfigureAwait(false);
        }
        else if (!running)
        {
            service.ResetReadiness();
            await RequireSuccessAsync(service, ["start", id], 30, token).ConfigureAwait(false);
        }
        return id;
    }

    private async Task<string> CreateAndStartAsync(Service service, string image, string? runtimeImage, CancellationToken token)
    {
        PodmanCommandResult created = await RequireSuccessAsync(service, BuildCreateArgs(service, image, runtimeImage), 30, token).ConfigureAwait(false);
        string id = RequireContainerId(created.StdOut.Trim());
        service.ActiveContainer = id;
        service.ResetReadiness();
        if (runtimeImage is null)
            await RequireSuccessAsync(service, ["start", id], 30, token).ConfigureAwait(false);
        else
            await VerifyNetworkedServiceAsync(service, id, runtimeImage, inspection: null, token).ConfigureAwait(false);
        return id;
    }

    private List<string> BuildCreateArgs(Service service, string image, string? runtimeImage)
    {
        List<string> args =
        [
            "create", "--name", service.ContainerName,
            "--label", $"{ManagedLabel}={service.Role}", "--label", $"{ReleaseLabel}={release.ReleaseId}",
            "--pull=never", "--read-only", "--cap-drop=ALL",
            "--security-opt=no-new-privileges", "--userns=keep-id:uid=65532,gid=65532", "--user=65532:65532",
            "--env", "Kautuka__Mode=prod", "--env", "Kautuka__Transport=unix",
        ];
        if (service.NetworkRole is { } role)
        {
            string address = WorkloadRulesetRenderer.FindApplianceAddress(role)
                ?? throw new InvalidOperationException("Unknown appliance network role.");
            args.InsertRange(0, ["--hooks-dir", GetApplianceHookDirectory()]);
            args.AddRange([
                "--network", $"{EgressTopologyManager.WorkloadNetworkName}:ip={address}",
                "--annotation", $"io.kautuka.role={role}",
                "--annotation", $"io.kautuka.image-id={runtimeImage}",
                "--env", "Kautuka__Appliance__Enabled=true",
            ]);
        }
        else args.Add("--network=none");

        // .NET certificate validation accesses the per-user store even when a
        // peer uses custom trust. Numeric container users may receive an empty
        // HOME from Podman when recreated during boot.
        if (service.Role != ObservabilityRole)
        {
            args.AddRange(["--env", "HOME=/tmp"]);
            if (verificationMode)
                args.AddRange(["--env", $"{KautukaMaintenanceMode.ApplianceVerificationEnvironmentVariable}=1"]);
        }

        if (superviseObservability && service.Role != ObservabilityRole)
        {
            string endpoint = FormattableString.Invariant($"http://{WorkloadRulesetRenderer.EgressPodIp}:{WorkloadRulesetRenderer.TelemetryIngressPort}/api/default");
            args.AddRange([
                "--env", $"Kautuka__Telemetry__Otlp__LogsEndpoint={endpoint}",
                "--env", $"Kautuka__Telemetry__Otlp__MetricsEndpoint={endpoint}",
                "--env", $"Kautuka__Telemetry__Otlp__TracesEndpoint={endpoint}",
                "--env", "Kautuka__Telemetry__Otlp__Protocol=http/protobuf",
                "--env", "Kautuka__Telemetry__Otlp__Headers=",
            ]);
        }

        if (service.Role == ObservabilityRole)
        {
            string dataDirectory = Directory.CreateDirectory(Path.Combine(stateRoot, ApplianceTelemetry.StateName)).FullName;
            args.AddRange([
                "--secret", $"{ApplianceTelemetry.SecretName},type=env,target=ZO_ROOT_USER_PASSWORD",
                "--env", $"ZO_ROOT_USER_EMAIL={KautukaTelemetryDefaults.DefaultOpenObserveRootUserEmail}",
                "--env", "ZO_DATA_DIR=/data",
                "--volume", $"{dataDirectory}:/data:Z",
                image,
            ]);
            return args;
        }

        if (service.Role == WebHostRole)
        {
            string peer = RequireMountSource(Path.Combine(socketRoot, GatewayRole), directory: true);
            string port = WorkloadRulesetRenderer.WebHostPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
            args.AddRange(["--publish", $"127.0.0.1:{port}:{port}", "--volume", $"{peer}:/run/kautuka/gateway:ro,z", image]);
            return args;
        }

        bool isGateway = service.Role == GatewayRole;
        string statePath = Path.Combine(stateRoot, service.StateName);
        if (isGateway && File.Exists(Path.Combine(stateRoot, "kautuka.db")))
            throw new InvalidOperationException("Gateway data is still in the host-service layout. Appliance activation requires a completed data migration.");
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Appliance service state requires Linux.");
        Directory.CreateDirectory(statePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string stateDirectory = RequireMountSource(statePath, directory: true);
        string socketDirectory = RequireMountSource(Path.Combine(socketRoot, service.Role), directory: true);
        string dataRoot = isGateway ? Path.GetFullPath(stateRoot) : "/data";
        string stateMount = isGateway ? dataRoot : $"{dataRoot}/{service.StateName}";
        args.AddRange([
            "--env", $"Kautuka__ContainerStateRoot={dataRoot}",
            "--volume", $"{stateDirectory}:{stateMount}:Z",
            "--volume", $"{socketDirectory}:/run/kautuka/{service.Role}:z",
            "--volume", $"{KautukaDatabaseWriterLease.LockPath}:{KautukaDatabaseWriterLease.LockPath}:ro",
        ]);
        if (isGateway)
        {
            args.AddRange([
                "--dns", WorkloadRulesetRenderer.EgressPodIp,
                "--volume", $"{RequireMountSource(gatewayConnectDirectory!, directory: true)}:/run/kautuka-gateway-connect:ro,Z",
            ]);
            AddGatewayPeerMounts(args, dataRoot);
        }
        if (service.Role == ChannelRole)
        {
            if (service.NetworkRole is not null)
                args.AddRange(["--dns", WorkloadRulesetRenderer.EgressPodIp]);
            string security = Path.Combine(stateRoot, "security", "channel");
            string privateRoot = RequireMountSource(Path.Combine(security, "channel-private"), directory: true);
            string trustRoot = RequireMountSource(Path.Combine(security, "channel-trust"), directory: true);
            string managerSocket = RequireMountSource(Path.Combine(socketRoot, "manager"), directory: true);
            string managerCa = RequireMountSource(Path.Combine(stateRoot, "manager", ".ca.cer"), directory: false);
            string managerClient = RequireMountSource(Path.Combine(stateRoot, "manager", ManagerClientIdentities.ChannelHostCertificateFileName), directory: false);
            args.AddRange([
                "--env", "Kautuka__ChannelGateway__ProductionMaterial__ChannelPrivateRoot=/run/channel-private",
                "--env", "Kautuka__ChannelGateway__ProductionMaterial__ChannelTrustRoot=/run/channel-trust",
                "--volume", $"{privateRoot}:/run/channel-private:ro,Z",
                "--volume", $"{trustRoot}:/run/channel-trust:ro,z",
                "--volume", $"{managerSocket}:/run/kautuka/manager:ro,z",
                "--volume", $"{managerCa}:/data/manager/.ca.cer:ro,z",
                "--volume", $"{managerClient}:/data/manager/{ManagerClientIdentities.ChannelHostCertificateFileName}:ro,Z",
            ]);
        }
        if (service.Role == UpdateRole)
        {
            args.AddRange([
                "--env", $"Kautuka__Appliance__ReleaseId={release.ReleaseId}",
                "--env", $"Kautuka__Appliance__ReleaseVersion={release.Version}",
                "--volume", $"{RequireMountSource(Path.Combine(socketRoot, "manager"), directory: true)}:/run/kautuka/manager:ro,z",
                "--volume", $"{RequireMountSource(Path.Combine(stateRoot, "manager", ".ca.cer"), directory: false)}:/data/manager/.ca.cer:ro,z",
                "--volume", $"{RequireMountSource(Path.Combine(stateRoot, "manager", ManagerClientIdentities.UpdateServiceCertificateFileName), directory: false)}:/data/manager/{ManagerClientIdentities.UpdateServiceCertificateFileName}:ro,z",
            ]);
        }
        args.Add(image);
        return args;
    }

    private string GetApplianceHookDirectory()
    {
        string hookPath = RequireMountSource(Path.Combine(versionDirectory, "scripts", "kautuka-oci-hook"), directory: false);
        string managerRoot = Path.GetFullPath(Path.Combine(versionDirectory, "..", ".."));
        var current = new DirectoryInfo(Path.Combine(managerRoot, "current"));
        if (current.ResolveLinkTarget(returnFinalTarget: true)?.FullName != Path.GetFullPath(versionDirectory))
            throw new InvalidOperationException("The installed OCI hook must resolve to the selected appliance release.");
        using (FileStream hook = File.OpenRead(hookPath))
        {
            Span<byte> signature = stackalloc byte[4];
            hook.ReadExactly(signature);
            if (!signature.SequenceEqual("\u007fELF"u8))
                throw new InvalidOperationException("Appliance services require the installed Native AOT OCI hook.");
        }

        string directory = RequireMountSource(Path.Combine(stateRoot, ".config", "containers", "hooks.d"), directory: true);
        using JsonDocument config = JsonDocument.Parse(File.ReadAllBytes(RequireMountSource(
            Path.Combine(directory, "kautuka_hook.json"), directory: false)));
        JsonElement root = config.RootElement;
        JsonElement command = root.GetProperty("hook");
        JsonElement stages = root.GetProperty("stages");
        if (root.GetProperty("version").GetString() != "1.0.0"
            || !root.GetProperty("when").GetProperty("always").GetBoolean()
            || stages.GetArrayLength() != 1 || stages[0].GetString() != "createRuntime"
            || command.GetProperty("path").GetString() != Path.Combine(current.FullName, "scripts", "kautuka-oci-hook")
            || command.TryGetProperty("args", out _) || command.TryGetProperty("env", out _))
            throw new InvalidOperationException("Appliance services require the Native AOT createRuntime hook registration.");
        return directory;
    }

    private void AddGatewayPeerMounts(List<string> args, string dataRoot)
    {
        string workspacePath = Path.Combine(stateRoot, "workspaces");
        Directory.CreateDirectory(workspacePath);
        string workspaceRoot = RequireMountSource(workspacePath, directory: true);
        // Gateway publishes host workspace paths to Manager when requesting workload mounts.
        args.AddRange([
            "--env", $"Kautuka__Workspace__BaseRoot={workspaceRoot}",
            "--volume", $"{workspaceRoot}:{workspaceRoot}:z",
        ]);
        string bundlePath = Path.Combine(dataRoot, "mcpb-bundles");
        Directory.CreateDirectory(bundlePath);
        string bundleRoot = RequireMountSource(bundlePath, directory: true);
        args.AddRange(["--volume", $"{bundleRoot}:{bundleRoot}:z"]);

        foreach (string peer in new[] { "manager", CredentialsRole, ChannelRole })
            args.AddRange(["--volume", $"{RequireMountSource(Path.Combine(socketRoot, peer), directory: true)}:/run/kautuka/{peer}:ro,z"]);
        if (superviseUpdateService)
            args.AddRange(["--volume", $"{RequireMountSource(Path.Combine(socketRoot, UpdateRole), directory: true)}:/run/kautuka/{UpdateRole}:ro,z"]);

        using var version = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        List<string> peerFiles = ["manager/.ca.cer", $"manager/{ManagerClientIdentities.GatewayCertificateFileName}",
            "credentialsHost/transport/.ca.cer", "credentialsHost/transport/.credentials-client.pfx"];
        if (superviseUpdateService)
            peerFiles.AddRange(["updateService/.ca.cer", "updateService/.updateservice-client.pfx"]);
        foreach (string file in peerFiles)
        {
            string source = RequireMountSource(Path.Combine(stateRoot, file), directory: false);
            args.AddRange(["--volume", $"{source}:{dataRoot}/{file}:ro,z"]);
            using FileStream credential = File.OpenRead(source);
            version.AppendData(SHA256.HashData(credential));
        }
        // Podman file binds retain the old inode when a peer atomically replaces its certificate.
        args.AddRange(["--label", $"io.kautuka.appliance.peer-credentials={Convert.ToHexString(version.GetHashAndReset())}"]);

        string security = Path.Combine(stateRoot, "security", "channel");
        args.AddRange([
            "--env", "Kautuka__ChannelGateway__ProductionMaterial__GatewayPrivateRoot=/run/gateway-private",
            "--env", "Kautuka__ChannelGateway__ProductionMaterial__GatewayTrustRoot=/run/gateway-trust",
            "--volume", $"{RequireMountSource(Path.Combine(security, "gateway-private"), directory: true)}:/run/gateway-private:ro,Z",
            "--volume", $"{RequireMountSource(Path.Combine(security, "gateway-trust"), directory: true)}:/run/gateway-trust:ro,z",
        ]);
    }

    private async Task<string> ResolveImageIdAsync(Service service, string image, CancellationToken token)
    {
        if (service.Role == ObservabilityRole && imageArchives is not null)
            await imageArchives.TryEnsureImageAsync(image, token).ConfigureAwait(false);
        PodmanCommandResult result = await RequireSuccessAsync(service, ["image", "inspect", "--format", "{{.Id}}", image], 15, token).ConfigureAwait(false);
        if (!ApplianceRoleImageImporter.TryNormalizeImageId(result.StdOut.Trim(), out string id))
            throw new InvalidDataException("Invalid appliance image ID.");
        return id;
    }

    private static bool MatchesImageId(string? actual, string expected)
        => actual == expected || string.Equals(actual, expected["sha256:".Length..], StringComparison.Ordinal);

    private static bool HasExpectedProfile(JsonElement container, List<string> expected)
    {
        JsonElement config = container.GetProperty("Config");
        if (!config.TryGetProperty("CreateCommand", out JsonElement command) || command.ValueKind != JsonValueKind.Array)
            return false;
        string?[] actual = command.EnumerateArray().Select(static entry => entry.GetString()).ToArray();
        return actual.Length == expected.Count + 1 && Path.GetFileName(actual[0]) == "podman"
            && actual.Skip(1).SequenceEqual(expected, StringComparer.Ordinal);
    }

    private async Task VerifyNetworkedServiceAsync(Service service, string id, string imageId, JsonElement? inspection, CancellationToken token)
    {
        EgressTopologyManager egress = topology ?? throw new InvalidOperationException("The service requires the appliance egress boundary.");
        string role = service.NetworkRole ?? throw new InvalidOperationException("Missing appliance network profile.");
        IWorkloadLaunchLease lease = await egress.AcquireLaunchLeaseAsync(token).ConfigureAwait(false);
        await using var leaseLifetime = lease.ConfigureAwait(false);
        using var launch = CancellationTokenSource.CreateLinkedTokenSource(lease.CancellationToken, token);
        try
        {
            egress.AuthorizeApplianceService(role, id, imageId);
            if (inspection is null) await RequireSuccessAsync(service, ["--hooks-dir", GetApplianceHookDirectory(), "start", id], 30, launch.Token).ConfigureAwait(false);
            await egress.VerifyApplianceServiceAsync(role, id, inspection, launch.Token).ConfigureAwait(false);
            launch.Token.ThrowIfCancellationRequested();
        }
        catch
        {
            await StopActiveAsync(service, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<PodmanCommandResult> RequireSuccessAsync(Service service, List<string> arguments, int timeout, CancellationToken token)
    {
        PodmanCommandResult result = await podman.RunAsync(arguments, timeout, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (result.ExitCode != 0)
            throw new InvalidOperationException(PodmanFailureFormatter.FormatFailure($"{service.Role} {arguments[0]}", result));
        return result;
    }

    private static bool HasLabel(JsonElement labels, string key, string value)
        => labels.TryGetProperty(key, out JsonElement found) && found.ValueKind == JsonValueKind.String && found.GetString() == value;

    private static string RequireContainerId(string? id)
        => id is { Length: 64 } && id.All(char.IsAsciiHexDigit) ? id : throw new InvalidDataException("Invalid appliance container ID.");

    private static string RequireMountSource(string path, bool directory)
    {
        string fullPath = Path.GetFullPath(path);
        FileSystemInfo source = directory ? new DirectoryInfo(fullPath) : new FileInfo(fullPath);
        if (!source.Exists || source.LinkTarget is not null || fullPath.Contains(':', StringComparison.Ordinal))
            throw new InvalidDataException($"Appliance service requires a provisioned mount source at {fullPath}.");
        return fullPath;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Verified local {Role} readiness for appliance release {ReleaseId}")]
    private static partial void LogServiceReady(ILogger logger, string role, string releaseId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Appliance {Role} reconciliation failed: {Reason}")]
    private static partial void LogReconcileFailed(ILogger logger, string role, string reason);
}
