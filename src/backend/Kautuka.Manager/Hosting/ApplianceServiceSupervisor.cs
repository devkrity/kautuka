using System.Security.Cryptography;
using System.Text.Json;

using Kautuka.Hosting.Hosting;
using Kautuka.Manager.Protos;
using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting.NetworkAccess;
using Kautuka.Telemetry.Security;

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
    string? gatewayConnectDirectory = null) : BackgroundService
{
    private const string ManagedLabel = "io.kautuka.appliance.service";
    private const string ReleaseLabel = "io.kautuka.appliance.release";
    private const string CredentialsRole = "credentialshost";
    private const string ChannelRole = "channelhost";
    private const string GatewayRole = "gateway";
    private static readonly TimeSpan reconcileDelay = TimeSpan.FromSeconds(5);
    private readonly Service credentials = new(CredentialsRole, "credentialsHost");
    private readonly Service channel = new(ChannelRole, "channelHost");
    private readonly Service gateway = new(GatewayRole, "gateway");
    private string? gatewayImageId;

    private sealed class Service(string role, string stateName)
    {
        internal string Role { get; } = role;
        internal string StateName { get; } = stateName;
        internal string ContainerName { get; } = $"kautuka-appliance-{role}";
        internal string? ActiveContainer { get; set; }
        internal bool Ready { get; set; }
        internal int FailedProbes { get; set; }

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
            await new ApplianceRoleImageImporter(podman).EnsureAsync(release.Deployment, versionDirectory, stoppingToken)
                .ConfigureAwait(false);
            using var timer = new PeriodicTimer(reconcileDelay);
            do
            {
                if (KautukaMaintenanceMode.IsActive)
                {
                    await StopCohortAsync(stoppingToken).ConfigureAwait(false);
                }
                else
                {
                    await TryReconcileAsync(credentials, stoppingToken).ConfigureAwait(false);
                    await TryReconcileAsync(channel, stoppingToken).ConfigureAwait(false);
                    if (topology?.IsStartupGateOpen == true && credentials.Ready && channel.Ready)
                        await TryReconcileAsync(gateway, stoppingToken).ConfigureAwait(false);
                    else
                        await StopActiveAsync(gateway, stoppingToken).ConfigureAwait(false);
                }
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

    private async Task StopCohortAsync(CancellationToken token)
    {
        await StopActiveAsync(gateway, token).ConfigureAwait(false);
        await StopActiveAsync(channel, token).ConfigureAwait(false);
        await StopActiveAsync(credentials, token).ConfigureAwait(false);
    }

    private async Task StopActiveAsync(Service service, CancellationToken token)
    {
        if (service.ActiveContainer is not { } id)
            return;
        PodmanCommandResult result = await podman.RunAsync(["stop", "--ignore", "--time", "10", id], 20, token)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            LogReconcileFailed(logger, service.Role, PodmanFailureFormatter.FormatFailure("Container stop", result));
            return;
        }
        if (service.Role == GatewayRole) topology?.RevokeApplianceGateway(id);
        service.ActiveContainer = null;
        service.ResetReadiness();
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
            LogReconcileFailed(logger, service.Role, ex.Message);
        }
    }

    private async Task ReconcileAsync(Service service, CancellationToken token)
    {
        await EnsureRunningAsync(service, token).ConfigureAwait(false);
        string socket = Path.Combine(socketRoot, service.Role, service.Role + ".sock");
        string transport = Path.Combine(stateRoot, service.StateName, "transport");
        string security = Path.Combine(stateRoot, "security", "channel");
        bool isChannel = service.Role == ChannelRole;
        bool healthy = service.Role == GatewayRole
            ? await ApplianceServiceReadiness.CheckGatewayAsync(socket, token).ConfigureAwait(false)
            : await ApplianceServiceReadiness.CheckAsync(
            socket,
            isChannel ? Path.Combine(security, "channel-trust", ChannelSecurityMaterialProvisioner.CaCertificate) : Path.Combine(transport, ".ca.cer"),
            isChannel ? Path.Combine(security, "gateway-private", ChannelSecurityMaterialProvisioner.GatewayClientPfx) : Path.Combine(transport, ".credentials-client.pfx"),
            token)
            .ConfigureAwait(false);
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
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        }, token);

    private async Task<string> EnsureRunningAsync(Service service, CancellationToken token)
    {
        string image = release.Deployment.Images[service.Role];
        if (service.Role == GatewayRole && topology?.IsStartupGateOpen != true)
            throw new InvalidOperationException("Gateway cannot start before the appliance egress boundary is ready.");
        string? gatewayImage = service.Role == GatewayRole
            ? gatewayImageId ??= await ResolveImageIdAsync(service, image, token).ConfigureAwait(false)
            : null;
        PodmanCommandResult inspection = await podman.RunAsync(["container", "inspect", service.ContainerName], 15, token)
            .ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (inspection.ExitCode != 0)
        {
            PodmanCommandResult exists = await podman.RunAsync(["container", "exists", service.ContainerName], 10, token)
                .ConfigureAwait(false);
            if (exists.ExitCode == 1)
                return await CreateAndStartAsync(service, image, gatewayImage, token).ConfigureAwait(false);
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
            || (gatewayImage is null ? host.GetProperty("NetworkMode").GetString() != "none"
                : !MatchesImageId(container.GetProperty("Image").GetString(), gatewayImage)
                    || !HasGatewayProfile(container, BuildCreateArgs(service, image, gatewayImage)))
            || !host.GetProperty("ReadonlyRootfs").GetBoolean())
        {
            await RequireSuccessAsync(service, ["rm", "--force", "--time", "10", id], 25, token).ConfigureAwait(false);
            if (gatewayImage is not null) topology!.RevokeApplianceGateway(id);
            return await CreateAndStartAsync(service, image, gatewayImage, token).ConfigureAwait(false);
        }

        if (service.ActiveContainer != id)
        {
            service.ActiveContainer = id;
            service.ResetReadiness();
        }
        bool running = container.GetProperty("State").GetProperty("Running").GetBoolean();
        if (gatewayImage is not null)
        {
            if (!running) service.ResetReadiness();
            await VerifyGatewayAsync(service, id, gatewayImage, running, token).ConfigureAwait(false);
        }
        else if (!running)
        {
            service.ResetReadiness();
            await RequireSuccessAsync(service, ["start", id], 30, token).ConfigureAwait(false);
        }
        return id;
    }

    private async Task<string> CreateAndStartAsync(Service service, string image, string? gatewayImage, CancellationToken token)
    {
        PodmanCommandResult created = await RequireSuccessAsync(service, BuildCreateArgs(service, image, gatewayImage), 30, token).ConfigureAwait(false);
        string id = RequireContainerId(created.StdOut.Trim());
        service.ActiveContainer = id;
        service.ResetReadiness();
        if (gatewayImage is null)
            await RequireSuccessAsync(service, ["start", id], 30, token).ConfigureAwait(false);
        else
            await VerifyGatewayAsync(service, id, gatewayImage, running: false, token).ConfigureAwait(false);
        return id;
    }

    private List<string> BuildCreateArgs(Service service, string image, string? gatewayImage)
    {
        string statePath = Path.Combine(stateRoot, service.StateName);
        if (gatewayImage is not null)
        {
            if (File.Exists(Path.Combine(stateRoot, "kautuka.db")))
                throw new InvalidOperationException("Gateway data is still in the host-service layout. Appliance activation requires a completed data migration.");
            Directory.CreateDirectory(statePath);
        }
        string stateDirectory = RequireMountSource(statePath, directory: true);
        string socketDirectory = RequireMountSource(Path.Combine(socketRoot, service.Role), directory: true);
        string dataRoot = gatewayImage is null ? "/data" : Path.GetFullPath(stateRoot);
        string stateMount = gatewayImage is null ? $"{dataRoot}/{service.StateName}" : dataRoot;
        List<string> args =
        [
            "create", "--name", service.ContainerName,
            "--label", $"{ManagedLabel}={service.Role}", "--label", $"{ReleaseLabel}={release.ReleaseId}",
            "--pull=never", "--read-only", "--cap-drop=ALL",
            "--security-opt=no-new-privileges", "--userns=keep-id:uid=65532,gid=65532", "--user=65532:65532",
            "--env", "Kautuka__Mode=prod", "--env", $"Kautuka__ContainerStateRoot={dataRoot}", "--env", "Kautuka__Transport=unix",
            "--volume", $"{stateDirectory}:{stateMount}:Z",
            "--volume", $"{socketDirectory}:/run/kautuka/{service.Role}:z",
            "--volume", $"{KautukaDatabaseWriterLease.LockPath}:{KautukaDatabaseWriterLease.LockPath}:ro,z",
        ];
        if (gatewayImage is not null)
        {
            args.InsertRange(0, ["--hooks-dir", GetGatewayHookDirectory()]);
            args.AddRange([
                "--network", $"{EgressTopologyManager.WorkloadNetworkName}:ip={WorkloadRulesetRenderer.ApplianceGatewayIp}",
                "--dns", WorkloadRulesetRenderer.EgressPodIp,
                "--annotation", "io.kautuka.role=appliance-gateway",
                "--annotation", $"io.kautuka.image-id={gatewayImage}",
                "--env", "Kautuka__Appliance__Enabled=true",
                "--volume", $"{RequireMountSource(gatewayConnectDirectory!, directory: true)}:/run/kautuka-gateway-connect:ro,Z",
            ]);
            AddGatewayPeerMounts(args, dataRoot);
        }
        else args.Add("--network=none");
        if (service.Role == ChannelRole)
        {
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
        args.Add(image);
        return args;
    }

    private string GetGatewayHookDirectory()
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
                throw new InvalidOperationException("The appliance Gateway requires the installed Native AOT OCI hook.");
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
            throw new InvalidOperationException("Gateway requires the appliance's Native AOT createRuntime hook registration.");
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

        using var version = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in new[] { "manager/.ca.cer", $"manager/{ManagerClientIdentities.GatewayCertificateFileName}",
            "credentialsHost/transport/.ca.cer", "credentialsHost/transport/.credentials-client.pfx" })
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
        PodmanCommandResult result = await RequireSuccessAsync(service, ["image", "inspect", "--format", "{{.Id}}", image], 15, token).ConfigureAwait(false);
        string id = result.StdOut.Trim();
        if (id.StartsWith("sha256:", StringComparison.Ordinal)) id = id["sha256:".Length..];
        return $"sha256:{RequireContainerId(id)}";
    }

    private static bool MatchesImageId(string? actual, string expected)
        => actual == expected || string.Equals(actual, expected["sha256:".Length..], StringComparison.Ordinal);

    private static bool HasGatewayProfile(JsonElement container, List<string> expected)
    {
        JsonElement config = container.GetProperty("Config");
        if (!config.TryGetProperty("CreateCommand", out JsonElement command) || command.ValueKind != JsonValueKind.Array)
            return false;
        string?[] actual = command.EnumerateArray().Select(static entry => entry.GetString()).ToArray();
        return actual.Length == expected.Count + 1 && Path.GetFileName(actual[0]) == "podman"
            && actual.Skip(1).SequenceEqual(expected, StringComparer.Ordinal);
    }

    private async Task VerifyGatewayAsync(Service service, string id, string imageId, bool running, CancellationToken token)
    {
        EgressTopologyManager egress = topology ?? throw new InvalidOperationException("Gateway requires the appliance egress boundary.");
        IWorkloadLaunchLease lease = await egress.AcquireLaunchLeaseAsync(token).ConfigureAwait(false);
        await using var leaseLifetime = lease.ConfigureAwait(false);
        using var launch = CancellationTokenSource.CreateLinkedTokenSource(lease.CancellationToken, token);
        try
        {
            egress.AuthorizeApplianceGateway(id, imageId);
            if (!running) await RequireSuccessAsync(service, ["--hooks-dir", GetGatewayHookDirectory(), "start", id], 30, launch.Token).ConfigureAwait(false);
            await egress.VerifyApplianceGatewayAsync(id, launch.Token).ConfigureAwait(false);
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
