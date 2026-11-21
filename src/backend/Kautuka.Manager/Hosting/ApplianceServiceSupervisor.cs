using System.Text.Json;

using Kautuka.Hosting.Hosting;
using Kautuka.Manager.Protos;
using Kautuka.Manager.RuntimeContainer.Hosting;
using Kautuka.Telemetry.Security;

namespace Kautuka.Manager.Hosting;

/// <summary>Supervises the application containers described by the selected signed appliance release.</summary>
internal sealed partial class ApplianceServiceSupervisor(
    PodmanCommandRunner podman,
    VerifiedApplianceRelease release,
    string versionDirectory,
    string stateRoot,
    string socketRoot,
    ILogger<ApplianceServiceSupervisor> logger) : BackgroundService
{
    private const string ManagedLabel = "io.kautuka.appliance.service";
    private const string ReleaseLabel = "io.kautuka.appliance.release";
    private const string CredentialsRole = "credentialshost";
    private const string ChannelRole = "channelhost";
    private static readonly TimeSpan reconcileDelay = TimeSpan.FromSeconds(5);
    private readonly Service credentials = new(CredentialsRole, "credentialsHost");
    private readonly Service channel = new(ChannelRole, "channelHost");

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
                    await StopActiveAsync(channel, stoppingToken).ConfigureAwait(false);
                    await StopActiveAsync(credentials, stoppingToken).ConfigureAwait(false);
                }
                else
                {
                    await TryReconcileAsync(credentials, stoppingToken).ConfigureAwait(false);
                    await TryReconcileAsync(channel, stoppingToken).ConfigureAwait(false);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await StopActiveAsync(channel, CancellationToken.None).ConfigureAwait(false);
            await StopActiveAsync(credentials, CancellationToken.None).ConfigureAwait(false);
        }
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
        string id = await EnsureRunningAsync(service, token).ConfigureAwait(false);
        string socket = Path.Combine(socketRoot, service.Role, service.Role + ".sock");
        string transport = Path.Combine(stateRoot, service.StateName, "transport");
        string security = Path.Combine(stateRoot, "security");
        bool isChannel = service.Role == ChannelRole;
        bool healthy = await ApplianceServiceReadiness.CheckAsync(
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
            LogReconcileFailed(logger, service.Role, "Consecutive authenticated health checks failed");
            await RequireSuccessAsync(service, ["stop", "--time", "10", id], 20, token).ConfigureAwait(false);
            service.ResetReadiness();
        }
    }

    internal Task<string> EnsureRunningAsync(string role, CancellationToken token)
        => EnsureRunningAsync(role switch
        {
            CredentialsRole => credentials,
            ChannelRole => channel,
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        }, token);

    private async Task<string> EnsureRunningAsync(Service service, CancellationToken token)
    {
        string image = release.Deployment.Images[service.Role];
        PodmanCommandResult exists = await podman.RunAsync(["container", "exists", service.ContainerName], 10, token)
            .ConfigureAwait(false);
        if (exists.ExitCode == 1)
        {
            return await CreateAndStartAsync(service, image, token).ConfigureAwait(false);
        }
        if (exists.ExitCode != 0)
            throw new InvalidOperationException(PodmanFailureFormatter.FormatFailure($"{service.Role} lookup", exists));

        PodmanCommandResult inspection = await RequireSuccessAsync(service, ["container", "inspect", service.ContainerName], 15, token)
            .ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(inspection.StdOut);
        JsonElement container = document.RootElement.EnumerateArray().Single();
        string id = RequireContainerId(container.GetProperty("Id").GetString());
        JsonElement labels = container.GetProperty("Config").GetProperty("Labels");
        if (labels.ValueKind != JsonValueKind.Object || !HasLabel(labels, ManagedLabel, service.Role))
            throw new InvalidOperationException($"The {service.Role} container name is in use outside appliance supervision.");

        JsonElement host = container.GetProperty("HostConfig");
        if (!HasLabel(labels, ReleaseLabel, release.ReleaseId)
            || container.GetProperty("ImageName").GetString() != image
            || host.GetProperty("NetworkMode").GetString() != "none"
            || !host.GetProperty("ReadonlyRootfs").GetBoolean())
        {
            await RequireSuccessAsync(service, ["rm", "--force", "--time", "10", id], 25, token).ConfigureAwait(false);
            return await CreateAndStartAsync(service, image, token).ConfigureAwait(false);
        }

        if (service.ActiveContainer != id)
        {
            service.ActiveContainer = id;
            service.ResetReadiness();
        }
        if (!container.GetProperty("State").GetProperty("Running").GetBoolean())
        {
            service.ResetReadiness();
            await RequireSuccessAsync(service, ["start", id], 30, token).ConfigureAwait(false);
        }
        return id;
    }

    private async Task<string> CreateAndStartAsync(Service service, string image, CancellationToken token)
    {
        string stateDirectory = RequireMountSource(Path.Combine(stateRoot, service.StateName), directory: true);
        string socketDirectory = RequireMountSource(Path.Combine(socketRoot, service.Role), directory: true);
        List<string> args =
        [
            "create", "--name", service.ContainerName,
            "--label", $"{ManagedLabel}={service.Role}", "--label", $"{ReleaseLabel}={release.ReleaseId}",
            "--pull=never", "--network=none", "--read-only", "--cap-drop=ALL",
            "--security-opt=no-new-privileges", "--userns=keep-id:uid=65532,gid=65532", "--user=65532:65532",
            "--env", "Kautuka__Mode=prod", "--env", "Kautuka__ContainerStateRoot=/data", "--env", "Kautuka__Transport=unix",
            "--volume", $"{stateDirectory}:/data/{service.StateName}:Z",
            "--volume", $"{socketDirectory}:/run/kautuka/{service.Role}:z",
            "--volume", $"{KautukaDatabaseWriterLease.LockPath}:{KautukaDatabaseWriterLease.LockPath}:ro,z",
        ];
        if (service.Role == ChannelRole)
        {
            string privateRoot = RequireMountSource(Path.Combine(stateRoot, "security", "channel-private"), directory: true);
            string trustRoot = RequireMountSource(Path.Combine(stateRoot, "security", "channel-trust"), directory: true);
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
        PodmanCommandResult created = await RequireSuccessAsync(service, args, 30, token).ConfigureAwait(false);
        string id = RequireContainerId(created.StdOut.Trim());
        service.ActiveContainer = id;
        service.ResetReadiness();
        await RequireSuccessAsync(service, ["start", id], 30, token).ConfigureAwait(false);
        return id;
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

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Authenticated {Role} readiness for appliance release {ReleaseId}")]
    private static partial void LogServiceReady(ILogger logger, string role, string releaseId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Appliance {Role} reconciliation failed: {Reason}")]
    private static partial void LogReconcileFailed(ILogger logger, string role, string reason);
}
