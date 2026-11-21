using System.Text.Json;

using Kautuka.Hosting.Hosting;
using Kautuka.Manager.RuntimeContainer.Hosting;

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
    private const string ContainerName = "kautuka-appliance-credentialshost";
    private const string ManagedLabel = "io.kautuka.appliance.service";
    private const string ReleaseLabel = "io.kautuka.appliance.release";
    private const string Role = "credentialshost";
    private const string StorageName = "credentialsHost";
    private const string ContainerStateDirectory = "/data/credentialsHost";
    private const string ContainerSocketDirectory = "/run/kautuka/credentialshost";
    private static readonly TimeSpan reconcileDelay = TimeSpan.FromSeconds(5);
    private readonly string stateDirectory = Path.GetFullPath(Path.Combine(stateRoot, StorageName));
    private readonly string socketDirectory = Path.GetFullPath(Path.Combine(socketRoot, Role));
    private readonly string image = release.Deployment.Images[Role];
    private string? activeContainer;
    private bool ready;
    private int failedProbes;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            RequireDirectory(stateDirectory);
            RequireDirectory(socketDirectory);
            await new ApplianceRoleImageImporter(podman).EnsureAsync(release.Deployment, versionDirectory, stoppingToken)
                .ConfigureAwait(false);
            using var timer = new PeriodicTimer(reconcileDelay);
            do
            {
                try
                {
                    if (KautukaMaintenanceMode.IsActive)
                        await StopActiveAsync(stoppingToken).ConfigureAwait(false);
                    else
                        await ReconcileAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested &&
                    ex is IOException or InvalidDataException or InvalidOperationException or JsonException or KeyNotFoundException)
                {
                    LogReconcileFailed(logger, ex.Message);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await StopActiveAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task StopActiveAsync(CancellationToken token)
    {
        if (activeContainer is not { } id)
            return;
        PodmanCommandResult result = await podman.RunAsync(["stop", "--ignore", "--time", "10", id], 20, token)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            LogReconcileFailed(logger, PodmanFailureFormatter.FormatFailure("CredentialsHost stop", result));
            return;
        }
        activeContainer = null;
        ready = false;
        failedProbes = 0;
    }

    private async Task ReconcileAsync(CancellationToken token)
    {
        string id = await EnsureRunningAsync(token).ConfigureAwait(false);
        if (id != activeContainer)
        {
            activeContainer = id;
            ready = false;
            failedProbes = 0;
        }

        bool healthy = await ApplianceCredentialsReadiness.CheckAsync(
            Path.Combine(socketDirectory, "credentialshost.sock"), Path.Combine(stateDirectory, "transport"), token)
            .ConfigureAwait(false);
        if (healthy)
        {
            if (!ready) LogCredentialsReady(logger, release.ReleaseId);
            ready = true;
            failedProbes = 0;
        }
        else if (++failedProbes >= 6)
        {
            LogReconcileFailed(logger, "CredentialsHost failed consecutive authenticated health checks");
            await RequireSuccessAsync(["stop", "--time", "10", id], 20, token).ConfigureAwait(false);
            failedProbes = 0;
            ready = false;
        }
    }

    internal async Task<string> EnsureRunningAsync(CancellationToken token)
    {
        PodmanCommandResult exists = await podman.RunAsync(["container", "exists", ContainerName], 10, token)
            .ConfigureAwait(false);
        if (exists.ExitCode == 1)
        {
            activeContainer = await CreateAsync(token).ConfigureAwait(false);
            await RequireSuccessAsync(["start", activeContainer], 30, token).ConfigureAwait(false);
            return activeContainer;
        }
        if (exists.ExitCode != 0)
            throw new InvalidOperationException(PodmanFailureFormatter.FormatFailure("CredentialsHost lookup", exists));

        PodmanCommandResult inspection = await RequireSuccessAsync(["container", "inspect", ContainerName], 15, token)
            .ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(inspection.StdOut);
        JsonElement container = document.RootElement.EnumerateArray().Single();
        string id = RequireContainerId(container.GetProperty("Id").GetString());
        JsonElement labels = container.GetProperty("Config").GetProperty("Labels");
        if (labels.ValueKind != JsonValueKind.Object || !HasLabel(labels, ManagedLabel, Role))
            throw new InvalidOperationException("The CredentialsHost container name is in use outside appliance supervision.");

        JsonElement host = container.GetProperty("HostConfig");
        if (!HasLabel(labels, ReleaseLabel, release.ReleaseId)
            || container.GetProperty("ImageName").GetString() != image
            || host.GetProperty("NetworkMode").GetString() != "none"
            || !host.GetProperty("ReadonlyRootfs").GetBoolean())
        {
            await RequireSuccessAsync(["rm", "--force", "--time", "10", id], 25, token).ConfigureAwait(false);
            activeContainer = await CreateAsync(token).ConfigureAwait(false);
            await RequireSuccessAsync(["start", activeContainer], 30, token).ConfigureAwait(false);
            return activeContainer;
        }

        if (!container.GetProperty("State").GetProperty("Running").GetBoolean())
        {
            ready = false;
            failedProbes = 0;
            await RequireSuccessAsync(["start", id], 30, token).ConfigureAwait(false);
        }
        return id;
    }

    private async Task<string> CreateAsync(CancellationToken token)
    {
        PodmanCommandResult created = await RequireSuccessAsync(
            [
                "create", "--name", ContainerName,
                "--label", $"{ManagedLabel}={Role}", "--label", $"{ReleaseLabel}={release.ReleaseId}",
                "--pull=never", "--network=none", "--read-only", "--cap-drop=ALL",
                "--security-opt=no-new-privileges", "--userns=keep-id:uid=65532,gid=65532", "--user=65532:65532",
                "--env", "Kautuka__Mode=prod", "--env", "Kautuka__ContainerStateRoot=/data",
                "--volume", $"{stateDirectory}:{ContainerStateDirectory}:Z",
                "--volume", $"{socketDirectory}:{ContainerSocketDirectory}:z",
                "--volume", $"{KautukaDatabaseWriterLease.LockPath}:{KautukaDatabaseWriterLease.LockPath}:ro,z",
                image,
            ], 30, token).ConfigureAwait(false);
        return RequireContainerId(created.StdOut.Trim());
    }

    private async Task<PodmanCommandResult> RequireSuccessAsync(IReadOnlyList<string> arguments, int timeout, CancellationToken token)
    {
        PodmanCommandResult result = await podman.RunAsync(arguments, timeout, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (result.ExitCode != 0)
            throw new InvalidOperationException(PodmanFailureFormatter.FormatFailure($"CredentialsHost {arguments[0]}", result));
        return result;
    }

    private static bool HasLabel(JsonElement labels, string key, string value)
        => labels.TryGetProperty(key, out JsonElement found) && found.ValueKind == JsonValueKind.String && found.GetString() == value;

    private static string RequireContainerId(string? id)
        => id is { Length: 64 } && id.All(char.IsAsciiHexDigit) ? id : throw new InvalidDataException("Invalid CredentialsHost container ID.");

    private static void RequireDirectory(string path)
    {
        var directory = new DirectoryInfo(path);
        if (!directory.Exists || directory.LinkTarget is not null || path.Contains(':', StringComparison.Ordinal))
            throw new InvalidDataException($"Appliance service requires a provisioned local directory at {path}.");
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Authenticated CredentialsHost readiness for appliance release {ReleaseId}")]
    private static partial void LogCredentialsReady(ILogger logger, string releaseId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Appliance service reconciliation failed: {Reason}")]
    private static partial void LogReconcileFailed(ILogger logger, string reason);
}
