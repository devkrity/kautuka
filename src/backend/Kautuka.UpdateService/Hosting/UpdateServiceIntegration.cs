using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

using Grpc.Core;

using Kautuka.Hosting.Hosting;
using Kautuka.UpdateService.Abstractions;
using Kautuka.UpdateService.Models;

using Microsoft.Extensions.Options;

namespace Kautuka.UpdateService.Hosting;

public sealed class UpdateServiceIntegration(
    UpdateServiceRuntimePaths runtimePaths,
    IConfiguration configuration,
    IOptions<UpdateOptions> updateOptions,
    IManagerStatusClient managerStatusClient,
    IHostApplicationLifetime applicationLifetime,
    ILogger<UpdateServiceIntegration> logger) : IUpdateServiceIntegration
{
    private const string backupsRelativePath = "updates/backups";
    private const string activationLauncherRelativePath = "updates/activate-updateservice.sh";
    private const string activationRequestRelativePath = "updates/activation-request.env";
    private const string pendingActivationRequestRelativePath = "updates/pending-activation.env";
    private const string installedVersionFileName = "release-version.txt";
    private const string activationPathUnitName = "kautuka-activate.path";
    private const string serviceScopeDocumentPath = "/etc/kautuka/service-scope.json";
    private const string updateSourceDocumentPath = "/etc/kautuka/update-source.json";
    private const string manifestPublicKeyPath = "/etc/kautuka/manifest-public-key.pem";
    private const string sudoExecutablePath = "/usr/bin/sudo";
    private const string systemctlExecutablePath = "/usr/bin/systemctl";
    private const string privilegedUnitRestartHelperPath = "/usr/local/lib/kautuka/restart-system-units";
    private const string updateServiceUnitName = "kautuka-updateservice.service";
    private const int updateRestartExitCode = 75;
    private const int terminatedBySigTermExitCode = 143;
    private const int rollbackBackupRetentionCount = 1;
    private static readonly string provisionedConfigRelativeRoot = Path.Combine("provisioning", "etc", "kautuka");
    private static readonly string[] orderedRuntimeServiceUnits =
    [
        "kautuka-credentialshost.service",
        "kautuka-manager.service",
        "kautuka-gateway.service",
        "kautuka-channelhost.service",
        "kautuka-webhost.service",
    ];

    private readonly string sharedDataRoot = KautukaHostPathPolicy.ResolveSharedStateRoot(configuration);
    private readonly string visibleRoot = KautukaHostPathPolicy.Resolve(configuration).VisibleRoot;
    private readonly UpdateOptions updateOptions = updateOptions.Value;

    public async ValueTask<UpdateServiceContext> GetHostContextAsync(CancellationToken cancellationToken)
    {
        string managerInstallRoot = ResolveManagerInstallRoot(AppContext.BaseDirectory);
        string managerReleaseId = TryResolveCurrentInstalledReleaseId(AppContext.BaseDirectory, "manager")
            ?? ResolveManagerVersion(managerInstallRoot);
        string managerVersion = ResolveManagerVersion(managerInstallRoot);

        int activeExecCount = 0;
        bool isReady = false;
        bool hostStatusObserved = false;

        try
        {
            Kautuka.Manager.Protos.ManagerStatusResponse status = await managerStatusClient.GetHostStatusAsync(cancellationToken).ConfigureAwait(false);
            isReady = status.CoreReady;
            activeExecCount = status.ActiveExecCount;
            hostStatusObserved = true;
        }
        catch (RpcException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        (bool pointersConsistent, bool unitsActive) = ObserveReleaseHealth(managerVersion);

        return new UpdateServiceContext(
            runtimePaths.RuntimeRoot,
            managerInstallRoot,
            managerReleaseId,
            managerVersion,
            isReady,
            activeExecCount,
            hostStatusObserved,
            unitsActive,
            pointersConsistent);
    }

    public ValueTask RequestSupervisorReconcileAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    private async Task RunApplyReleaseHelperAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(updateOptions.ApplyReleaseHelperPath))
        {
            throw new InvalidOperationException("Update:ApplyReleaseHelperPath is not configured.");
        }

        if (!File.Exists(updateOptions.ApplyReleaseHelperPath))
        {
            throw new InvalidOperationException($"Apply-release helper was not found: {updateOptions.ApplyReleaseHelperPath}");
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = updateOptions.ApplyReleaseHelperUseSudo ? sudoExecutablePath : updateOptions.ApplyReleaseHelperPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = ResolveSafeWorkingDirectory(runtimePaths.RuntimeRoot),
            }
        };

        if (updateOptions.ApplyReleaseHelperUseSudo)
        {
            process.StartInfo.ArgumentList.Add("-n");
            process.StartInfo.ArgumentList.Add(updateOptions.ApplyReleaseHelperPath);
        }

        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        Task<string> stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(updateOptions.HostServiceActivationTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw new InvalidOperationException($"Apply-release helper timed out after {updateOptions.HostServiceActivationTimeout}.");
        }

        string stdOut = await stdOutTask.ConfigureAwait(false);
        string stdErr = await stdErrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            string? expectedVersion = TryResolveExpectedVersion(arguments);
            if (IsExpectedRestartHandoffExitCode(process.ExitCode)
                && !string.IsNullOrWhiteSpace(expectedVersion)
                && ObserveReleaseHealth(expectedVersion) is { PointersConsistent: true, UnitsActive: true })
            {
                logger.LogInformation(
                    "Apply-release helper exited {ExitCode} (SIGTERM) but release {Version} pointers and units are healthy; treating as successful handoff. StdErr: {StdErr}",
                    process.ExitCode,
                    expectedVersion,
                    stdErr.Trim());
                return;
            }

            throw new InvalidOperationException($"Apply-release helper failed with exit code {process.ExitCode}: {stdErr}");
        }

        logger.LogInformation("Apply-release helper completed. StdOut: {StdOut}", stdOut.Trim());
    }

    private static string? TryResolveExpectedVersion(IReadOnlyList<string> arguments)
    {
        for (int i = 0; i < arguments.Count - 1; i++)
        {
            if (string.Equals(arguments[i], "--version", StringComparison.Ordinal)
                || string.Equals(arguments[i], "--revert", StringComparison.Ordinal))
            {
                return arguments[i + 1];
            }
        }

        return null;
    }

    private (bool PointersConsistent, bool UnitsActive) ObserveReleaseHealth(string expectedVersion)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(expectedVersion))
        {
            return (true, true);
        }

        string applianceRoot = ResolveApplianceRoot(ResolveManagerInstallRoot(AppContext.BaseDirectory));
        string[] serviceKeys = ["manager", "updateservice", "webhost", "gateway", "channelhost", "credentialshost"];
        bool pointersConsistent = true;
        foreach (string serviceKey in serviceKeys)
        {
            if (!CurrentPointerTargetsVersion(Path.Combine(applianceRoot, serviceKey, "current"), expectedVersion))
            {
                pointersConsistent = false;
                break;
            }
        }

        bool credentialsActive = File.Exists("/etc/systemd/system/kautuka-credentialshost.service")
            ? IsSystemdUnitActive(systemctlExecutablePath, ["is-active", "--quiet", "kautuka-credentialshost.service"])
            : IsSystemdUnitActive(systemctlExecutablePath, ["--user", "is-active", "--quiet", "kautuka-credentialshost.service"]);
        bool unitsActive = IsSystemdUnitActive(systemctlExecutablePath, ["is-active", "--quiet", "kautuka-gateway.service"])
            && IsSystemdUnitActive(systemctlExecutablePath, ["is-active", "--quiet", "kautuka-channelhost.service"])
            && credentialsActive
            && IsSystemdUnitActive(systemctlExecutablePath, ["--user", "is-active", "--quiet", "kautuka-manager.service"])
            && IsSystemdUnitActive(systemctlExecutablePath, ["--user", "is-active", "--quiet", "kautuka-updateservice.service"])
            && IsSystemdUnitActive(systemctlExecutablePath, ["--user", "is-active", "--quiet", "kautuka-webhost.service"]);

        return (pointersConsistent, unitsActive);
    }

    private static bool IsSystemdUnitActive(string systemctlPath, IReadOnlyList<string> arguments)
    {
        if (!File.Exists(systemctlPath))
        {
            return false;
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = systemctlPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };
            foreach (string argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.Start();
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                return false;
            }

            return process.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async ValueTask ActivatePreparedUpdateAsync(UpdatePreparedArtifacts preparedArtifacts, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preparedArtifacts);
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(preparedArtifacts.PlatformPackagePath)
            || !File.Exists(preparedArtifacts.PlatformPackagePath))
        {
            throw new InvalidOperationException("Prepared update is missing a verified platform artifact.");
        }

        logger.LogInformation(
            "Delegating prepared update {ReleaseId} to apply-release helper {HelperPath}. Artifact={Artifact}",
            preparedArtifacts.TargetReleaseId,
            updateOptions.ApplyReleaseHelperPath,
            preparedArtifacts.PlatformPackagePath);

        await RunApplyReleaseHelperAsync(
            ["--artifact", preparedArtifacts.PlatformPackagePath, "--version", preparedArtifacts.TargetReleaseId],
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask ActivateRollbackAsync(UpdateReleaseReference release, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        cancellationToken.ThrowIfCancellationRequested();

        logger.LogInformation(
            "Delegating rollback to {ReleaseId} to apply-release helper {HelperPath}.",
            release.ReleaseId,
            updateOptions.ApplyReleaseHelperPath);

        await RunApplyReleaseHelperAsync(
            ["--revert", release.ReleaseId],
            cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<bool> TryContinuePendingActivationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(false);
    }

    internal ValueTask<bool> TryContinuePendingActivationAsync(string baseDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = baseDirectory;
        return ValueTask.FromResult(false);
    }

    private void RequestHostRestart(string operation, string releaseId)
    {
        Environment.ExitCode = updateRestartExitCode;
        logger.LogInformation(
            "Stopping UpdateService process to activate {Operation} for release {ReleaseId} with exit code {ExitCode}.",
            operation,
            releaseId,
            updateRestartExitCode);
        applicationLifetime.StopApplication();
    }

    private async ValueTask ContinuePreparedActivationAsync(string baseDirectory, string operation, string releaseId, string? platformExtractedPath, CancellationToken cancellationToken)
    {
        string managerInstallRoot = ResolveManagerInstallRoot(baseDirectory);
        logger.LogInformation(
            "Resolved manager install root to {ManagerInstallRoot} for release {ReleaseId}.",
            managerInstallRoot,
            releaseId);

        EnsureProvisionedConfigurationWritable(platformExtractedPath, updateSourceDocumentPath, manifestPublicKeyPath, logger);

        HostServiceVersionActivator.ActivateManagerAndHostServices(
            managerInstallRoot,
            releaseId,
            platformExtractedPath,
            logger);
        SyncProvisionedConfiguration(platformExtractedPath, updateSourceDocumentPath, manifestPublicKeyPath, logger);

        ClearGeneratedAgentWorkspaceState(visibleRoot, logger);
        LogResolvedInstallRoots(baseDirectory, releaseId);
        ClearPendingActivationRequest();

        bool includeUpdateServiceRestart = ShouldRestartUpdateServiceAfterApply(baseDirectory, releaseId);

        if (await TryRunUserScopedActivationAsync(operation, includeUpdateServiceRestart, cancellationToken).ConfigureAwait(false))
        {
            logger.LogInformation(
                "Activated {Operation} for release {ReleaseId} via user-scoped systemd restart.",
                operation,
                releaseId);
            return;
        }

        if (await TryRunPrivilegedSystemActivationAsync(operation, releaseId, includeUpdateServiceRestart, cancellationToken).ConfigureAwait(false))
        {
            logger.LogInformation(
                "Activated {Operation} for release {ReleaseId} via privileged system-unit restart.",
                operation,
                releaseId);
            return;
        }

        if (await TryRunActivationCommandAsync(operation, releaseId, cancellationToken).ConfigureAwait(false))
        {
            logger.LogInformation(
                "Activated {Operation} for release {ReleaseId} via configured host service activation command.",
                operation,
                releaseId);
            return;
        }

        if (await TryQueueSystemdActivationRequestAsync(operation, releaseId).ConfigureAwait(false))
        {
            logger.LogInformation(
                "Activated {Operation} for release {ReleaseId} by queuing a systemd activation request.",
                operation,
                releaseId);
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Yield();
            RequestHostRestart(operation, releaseId);
        }, CancellationToken.None);
    }

    internal static bool CurrentPointerTargetsVersion(string currentPath, string expectedVersion)
    {
        if (string.IsNullOrWhiteSpace(expectedVersion))
        {
            return false;
        }

        if (!Directory.Exists(currentPath) && !File.Exists(currentPath))
        {
            return false;
        }

        string resolved = Path.GetFullPath(currentPath);
        try
        {
            FileSystemInfo? target = File.ResolveLinkTarget(currentPath, returnFinalTarget: true);
            if (target is not null)
            {
                resolved = target.FullName;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return resolved.Contains(
            $"{Path.DirectorySeparatorChar}versions{Path.DirectorySeparatorChar}{expectedVersion}",
            StringComparison.Ordinal);
    }

    internal static string ResolveManagerInstallRoot(string baseDirectory)
    {
        string serviceInstallRoot = ResolveServiceInstallRoot(baseDirectory);
        string applianceRoot = ResolveApplianceRoot(serviceInstallRoot);
        return Path.Combine(applianceRoot, "manager", "current");
    }

    private static string ResolveServiceInstallRoot(string baseDirectory)
    {
        string resolvedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(baseDirectory));
        DirectoryInfo installDirectory = new(resolvedPath);

        if (string.Equals(installDirectory.Name, "current", StringComparison.OrdinalIgnoreCase))
        {
            return installDirectory.FullName;
        }

        DirectoryInfo? parentDirectory = installDirectory.Parent;
        if (parentDirectory is not null
            && string.Equals(parentDirectory.Name, "versions", StringComparison.OrdinalIgnoreCase)
            && parentDirectory.Parent is not null)
        {
            return Path.Combine(parentDirectory.Parent.FullName, "current");
        }

        return installDirectory.FullName;
    }

    private static string ResolveApplianceRoot(string serviceInstallRoot)
    {
        DirectoryInfo serviceCurrentDirectory = new(serviceInstallRoot);
        DirectoryInfo? serviceDirectory = serviceCurrentDirectory.Parent;
        DirectoryInfo? applianceRoot = serviceDirectory?.Parent;

        return applianceRoot?.FullName
            ?? throw new InvalidOperationException($"Service install root '{serviceInstallRoot}' does not have the expected appliance layout.");
    }

    private static string ResolveRunningVersion()
        => Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
            ?? "unknown";

    internal static string ResolveManagerVersion(string managerInstallRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(managerInstallRoot);

        string installedVersionPath = Path.Combine(managerInstallRoot, installedVersionFileName);
        if (File.Exists(installedVersionPath))
        {
            string installedVersion = File.ReadAllText(installedVersionPath).Trim();
            if (!string.IsNullOrWhiteSpace(installedVersion))
            {
                return installedVersion;
            }
        }

        string managerExecutablePath = Path.Combine(managerInstallRoot, "kautuka-manager");
        if (File.Exists(managerExecutablePath))
        {
            FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(managerExecutablePath);
            string? informationalVersion = versionInfo.ProductVersion;
            if (!string.IsNullOrWhiteSpace(informationalVersion))
            {
                return informationalVersion;
            }

            string? fileVersion = versionInfo.FileVersion;
            if (!string.IsNullOrWhiteSpace(fileVersion))
            {
                return fileVersion;
            }
        }

        return ResolveRunningVersion();
    }

    private async Task<string> CreateRollbackBackupAsync(string targetReleaseId, CancellationToken cancellationToken)
    {
        ReleaseTrustPolicy.EnsureSafeReleaseId(targetReleaseId);
        string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        string backupRoot = Path.Combine(runtimePaths.RuntimeRoot, backupsRelativePath, $"{timestamp}-{targetReleaseId}");
        string dataBackupRoot = Path.Combine(backupRoot, "data");
        string configBackupRoot = Path.Combine(backupRoot, "config", "etc", "kautuka");
        Directory.CreateDirectory(dataBackupRoot);

        CopyFileIfExists(Path.Combine(sharedDataRoot, "kautuka.db"), Path.Combine(dataBackupRoot, "kautuka.db"));
        CopyFileIfExists(Path.Combine(sharedDataRoot, "kautuka.key"), Path.Combine(dataBackupRoot, "kautuka.key"));
        CopyFileIfExists(Path.Combine(sharedDataRoot, "gateway-auth.key"), Path.Combine(dataBackupRoot, "gateway-auth.key"));
        CopyFileIfExists(updateSourceDocumentPath, Path.Combine(configBackupRoot, "update-source.json"));
        CopyFileIfExists(manifestPublicKeyPath, Path.Combine(configBackupRoot, "manifest-public-key.pem"));

        CopyDirectoryIfExists(Path.Combine(sharedDataRoot, "manager"), Path.Combine(dataBackupRoot, "manager"));
        CopyDirectoryIfExists(Path.Combine(sharedDataRoot, "gateway"), Path.Combine(dataBackupRoot, "gateway"));
        CopyDirectoryIfExists(Path.Combine(sharedDataRoot, "agents"), Path.Combine(dataBackupRoot, "agents"));
        CopyDirectoryIfExists(Path.Combine(sharedDataRoot, "credentialsHost"), Path.Combine(dataBackupRoot, "credentialsHost"));
        CopyDirectoryIfExists(Path.Combine(sharedDataRoot, "channelHost"), Path.Combine(dataBackupRoot, "channelHost"));
        CopyDirectoryIfExists(Path.Combine(sharedDataRoot, "updateService"), Path.Combine(dataBackupRoot, "updateService"), "updates");

        string manifestPath = Path.Combine(backupRoot, "backup-manifest.txt");
        string[] manifestLines =
        [
            $"createdAtUtc={DateTimeOffset.UtcNow:O}",
            $"targetReleaseId={targetReleaseId}",
            $"sharedDataRoot={sharedDataRoot}",
            "files=kautuka.db,kautuka.key,gateway-auth.key,/etc/kautuka/update-source.json,/etc/kautuka/manifest-public-key.pem",
            "directories=manager,gateway,agents,credentialsHost,channelHost,updateService(excluding updates)"
        ];

        await File.WriteAllLinesAsync(manifestPath, manifestLines, cancellationToken).ConfigureAwait(false);
        PruneRollbackBackups(backupRoot);
        return backupRoot;
    }

    private void PruneRollbackBackups(string newestBackupRoot)
    {
        string backupsRoot = Path.GetDirectoryName(newestBackupRoot)
            ?? throw new InvalidOperationException($"Backup root '{newestBackupRoot}' does not have a parent directory.");

        if (!Directory.Exists(backupsRoot))
        {
            return;
        }

        string[] backupDirectories = Directory.GetDirectories(backupsRoot)
            .OrderByDescending(static path => Path.GetFileName(path), StringComparer.Ordinal)
            .ToArray();

        if (backupDirectories.Length <= rollbackBackupRetentionCount)
        {
            return;
        }

        foreach (string backupDirectory in backupDirectories.Skip(rollbackBackupRetentionCount))
        {
            logger.LogInformation(
                "Removing expired rollback backup {BackupDirectory}. RetentionCount={RetentionCount}",
                backupDirectory,
                rollbackBackupRetentionCount);
            Directory.Delete(backupDirectory, recursive: true);
        }
    }

    private void RestoreLatestRollbackBackup()
    {
        string backupRoot = ResolveLatestRollbackBackupRoot();
        RestoreRollbackBackupSnapshot(backupRoot, sharedDataRoot, logger);
    }

    internal static void RestoreRollbackBackupSnapshot(string backupRoot, string sharedDataRoot, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(sharedDataRoot);
        ArgumentNullException.ThrowIfNull(logger);

        string dataBackupRoot = Path.Combine(backupRoot, "data");
        string configBackupRoot = Path.Combine(backupRoot, "config", "etc", "kautuka");

        if (!Directory.Exists(dataBackupRoot))
        {
            throw new InvalidOperationException($"Rollback backup '{backupRoot}' is missing its data directory.");
        }

        logger.LogInformation(
            "Restoring shared state from rollback backup {BackupRoot}.",
            backupRoot);

        RestoreFileIfExists(Path.Combine(dataBackupRoot, "kautuka.db"), Path.Combine(sharedDataRoot, "kautuka.db"));
        RestoreFileIfExists(Path.Combine(dataBackupRoot, "kautuka.key"), Path.Combine(sharedDataRoot, "kautuka.key"));
        RestoreFileIfExists(Path.Combine(dataBackupRoot, "gateway-auth.key"), Path.Combine(sharedDataRoot, "gateway-auth.key"));
        RestoreFileIfExists(Path.Combine(configBackupRoot, "update-source.json"), updateSourceDocumentPath);
        RestoreFileIfExists(Path.Combine(configBackupRoot, "manifest-public-key.pem"), manifestPublicKeyPath);

        RestoreDirectoryIfExists(Path.Combine(dataBackupRoot, "manager"), Path.Combine(sharedDataRoot, "manager"));
        RestoreDirectoryIfExists(Path.Combine(dataBackupRoot, "gateway"), Path.Combine(sharedDataRoot, "gateway"));
        RestoreDirectoryIfExists(Path.Combine(dataBackupRoot, "agents"), Path.Combine(sharedDataRoot, "agents"));
        RestoreDirectoryIfExists(Path.Combine(dataBackupRoot, "credentialsHost"), Path.Combine(sharedDataRoot, "credentialsHost"));
        RestoreDirectoryIfExists(Path.Combine(dataBackupRoot, "channelHost"), Path.Combine(sharedDataRoot, "channelHost"));
        RestoreDirectoryIfExists(Path.Combine(dataBackupRoot, "updateService"), Path.Combine(sharedDataRoot, "updateService"), "updates");

        logger.LogInformation(
            "Completed shared-state restore from rollback backup {BackupRoot}.",
            backupRoot);
    }

    internal static void ClearGeneratedAgentWorkspaceState(string visibleRoot, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(visibleRoot);
        ArgumentNullException.ThrowIfNull(logger);

        string agentsRoot = Path.Combine(Path.GetFullPath(visibleRoot), "workspaces", "agents");
        if (!Directory.Exists(agentsRoot))
        {
            logger.LogInformation(
                "Skipping generated workspace cleanup because agents root {AgentsRoot} does not exist.",
                agentsRoot);
            return;
        }

        foreach (string agentWorkspaceRoot in Directory.GetDirectories(agentsRoot))
        {
            string generatedStateRoot = Path.Combine(agentWorkspaceRoot, ".kautuka");

            if (Directory.Exists(generatedStateRoot))
            {
                logger.LogInformation(
                    "Clearing generated workspace state at {GeneratedStateRoot} during update activation.",
                    generatedStateRoot);
                Directory.Delete(generatedStateRoot, recursive: true);
            }
        }
    }

    private string ResolveLatestRollbackBackupRoot()
    {
        string backupsRoot = Path.Combine(runtimePaths.RuntimeRoot, backupsRelativePath);
        if (!Directory.Exists(backupsRoot))
        {
            throw new InvalidOperationException($"Rollback backup root '{backupsRoot}' does not exist.");
        }

        string? latestBackupRoot = Directory.GetDirectories(backupsRoot)
            .OrderByDescending(static path => Path.GetFileName(path), StringComparer.Ordinal)
            .FirstOrDefault();

        return latestBackupRoot
            ?? throw new InvalidOperationException("No rollback backup is available to restore shared state.");
    }

    private async Task<bool> TryQueueSystemdActivationRequestAsync(string operation, string releaseId)
    {
        string requestPath = Path.Combine(runtimePaths.RuntimeRoot, activationRequestRelativePath);
        bool hasActivationPathUnit = HasActivationPathUnit();
        logger.LogInformation(
            "Attempting to queue systemd activation request. Operation={Operation}, ReleaseId={ReleaseId}, RequestPath={RequestPath}, HasActivationPathUnit={HasActivationPathUnit}",
            operation,
            releaseId,
            requestPath,
            hasActivationPathUnit);

        if (!hasActivationPathUnit)
        {
            return false;
        }

        string directory = Path.GetDirectoryName(requestPath)!;
        Directory.CreateDirectory(directory);

        string tempPath = requestPath + ".tmp";
        string[] requestLines =
        [
            $"operation={operation}",
            $"release_id={releaseId}",
            $"requested_at_utc={DateTimeOffset.UtcNow:O}"
        ];

        await File.WriteAllLinesAsync(tempPath, requestLines, CancellationToken.None).ConfigureAwait(false);
        File.Move(tempPath, requestPath, overwrite: true);

        logger.LogInformation(
            "Queued systemd activation request for {Operation} of release {ReleaseId} at {RequestPath}.",
            operation,
            releaseId,
            requestPath);

        return true;
    }

    private static bool HasActivationPathUnit()
        => File.Exists(Path.Combine("/etc/systemd/system", activationPathUnitName))
            || File.Exists(Path.Combine("/usr/lib/systemd/system", activationPathUnitName));

    private async Task<bool> TryRunUserScopedActivationAsync(string operation, bool includeUpdateServiceRestart, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux() || !IsUserServiceScope())
        {
            return false;
        }

        IReadOnlyList<string> units = BuildSystemActivationPlan(operation, includeUpdateServiceRestart);
        await RunUserScopedUnitRestartAsync(units, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<bool> TryRunPrivilegedSystemActivationAsync(string operation, string releaseId, bool includeUpdateServiceRestart, CancellationToken cancellationToken)
    {
        if (IsUserServiceScope())
        {
            return false;
        }

        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        if (!File.Exists(privilegedUnitRestartHelperPath))
        {
            logger.LogInformation(
                "Privileged system-unit restart helper was not found at {HelperPath}. Operation={Operation}, ReleaseId={ReleaseId}",
                privilegedUnitRestartHelperPath,
                operation,
                releaseId);
            return false;
        }

        IReadOnlyList<string> units = BuildSystemActivationPlan(operation, includeUpdateServiceRestart);
        await RunPrivilegedUnitRestartAsync(units, cancellationToken).ConfigureAwait(false);

        return true;
    }

    private static List<string> BuildSystemActivationPlan(string operation, bool includeUpdateServiceRestart = false)
    {
        List<string> units = [.. orderedRuntimeServiceUnits];

        if (includeUpdateServiceRestart || string.Equals(operation, "rollback", StringComparison.Ordinal))
        {
            units.Add(updateServiceUnitName);
        }

        return units;
    }

    private async Task RunPrivilegedUnitRestartAsync(IReadOnlyList<string> units, CancellationToken cancellationToken)
    {
        if (units.Count == 0)
        {
            throw new InvalidOperationException("Privileged system-unit restart requires at least one unit.");
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = ResolveSafeWorkingDirectory(runtimePaths.RuntimeRoot),
            }
        };

        process.StartInfo.FileName = sudoExecutablePath;
        process.StartInfo.ArgumentList.Add("-n");
        process.StartInfo.ArgumentList.Add(privilegedUnitRestartHelperPath);
        foreach (string unit in units)
        {
            process.StartInfo.ArgumentList.Add(unit);
        }

        process.Start();

        Task<string> stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(updateOptions.HostServiceActivationTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw new InvalidOperationException($"Privileged system-unit restart timed out after {updateOptions.HostServiceActivationTimeout}.");
        }

        string stdOut = await stdOutTask.ConfigureAwait(false);
        string stdErr = await stdErrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Privileged system-unit restart failed with exit code {process.ExitCode}: {stdErr}");
        }

        logger.LogInformation(
            "Executed privileged system-unit restart for units {Units}. StdOut: {StdOut}",
            string.Join(", ", units),
            stdOut.Trim());
    }

    private async Task RunUserScopedUnitRestartAsync(IReadOnlyList<string> units, CancellationToken cancellationToken)
    {
        if (units.Count == 0)
        {
            throw new InvalidOperationException("User-scoped systemd restart requires at least one unit.");
        }

        await RunUserScopedSystemctlAsync(["daemon-reload"], cancellationToken).ConfigureAwait(false);

        List<string> restartArgs = ["--no-block", "restart", .. units];
        await RunUserScopedSystemctlAsync(restartArgs, cancellationToken).ConfigureAwait(false);
    }

    private async Task RunUserScopedSystemctlAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = File.Exists(systemctlExecutablePath) ? systemctlExecutablePath : "systemctl",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = ResolveSafeWorkingDirectory(runtimePaths.RuntimeRoot),
            }
        };

        process.StartInfo.ArgumentList.Add("--user");
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        ApplyUserSystemdEnvironment(process.StartInfo);

        process.Start();

        Task<string> stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(updateOptions.HostServiceActivationTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw new InvalidOperationException($"User-scoped systemd restart timed out after {updateOptions.HostServiceActivationTimeout}.");
        }

        string stdOut = await stdOutTask.ConfigureAwait(false);
        string stdErr = await stdErrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"User-scoped systemctl {string.Join(' ', arguments)} failed with exit code {process.ExitCode}: {stdErr}");
        }

        logger.LogInformation(
            "Executed user-scoped systemctl {Arguments}. StdOut: {StdOut}",
            string.Join(" ", arguments),
            stdOut.Trim());
    }

    private void ApplyUserSystemdEnvironment(ProcessStartInfo startInfo)
    {
        string? runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrWhiteSpace(runtimeDirectory))
        {
            string? userName = Environment.GetEnvironmentVariable("USER");
            if (string.IsNullOrWhiteSpace(userName))
            {
                userName = ReadInstalledServiceScope()?.Username;
            }

            if (!string.IsNullOrWhiteSpace(userName)
                && TryResolveUserId(userName, out int userId))
            {
                runtimeDirectory = $"/run/user/{userId}";
            }
        }

        if (!string.IsNullOrWhiteSpace(runtimeDirectory))
        {
            startInfo.Environment["XDG_RUNTIME_DIR"] = runtimeDirectory;
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DBUS_SESSION_BUS_ADDRESS")))
            {
                startInfo.Environment["DBUS_SESSION_BUS_ADDRESS"] = $"unix:path={runtimeDirectory}/bus";
            }
        }
    }

    private bool IsUserServiceScope()
        => string.Equals(ReadInstalledServiceScope()?.Scope, "user", StringComparison.OrdinalIgnoreCase);

    private bool ShouldRestartUpdateServiceAfterApply(string baseDirectory, string releaseId)
    {
        if (!string.Equals(TryResolveCurrentInstalledReleaseId(baseDirectory, "updateservice"), releaseId, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation(
                "Will include UpdateService in the final activation restart because the running release does not match target release {ReleaseId}.",
                releaseId);
            return true;
        }

        string? runningExecutablePath = new FileInfo("/proc/self/exe").LinkTarget;
        if (!string.IsNullOrWhiteSpace(runningExecutablePath)
            && runningExecutablePath.Contains(" (deleted)", StringComparison.Ordinal))
        {
            logger.LogInformation(
                "Will include UpdateService in the final activation restart because the current process is running from a deleted executable path: {ExecutablePath}",
                runningExecutablePath);
            return true;
        }

        return false;
    }

    private InstalledServiceScope? ReadInstalledServiceScope()
    {
        if (!File.Exists(serviceScopeDocumentPath))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(serviceScopeDocumentPath));
            JsonElement root = document.RootElement;

            string? scope = root.TryGetProperty("scope", out JsonElement scopeElement) && scopeElement.ValueKind == JsonValueKind.String
                ? scopeElement.GetString()
                : null;
            string? username = root.TryGetProperty("username", out JsonElement usernameElement) && usernameElement.ValueKind == JsonValueKind.String
                ? usernameElement.GetString()
                : null;
            string? target = root.TryGetProperty("target", out JsonElement targetElement) && targetElement.ValueKind == JsonValueKind.String
                ? targetElement.GetString()
                : null;

            return new InstalledServiceScope(scope, username, target);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse service scope contract at {Path}.", serviceScopeDocumentPath);
            return null;
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Failed to read service scope contract at {Path}.", serviceScopeDocumentPath);
            return null;
        }
    }

    private static bool TryResolveUserId(string userName, out int userId)
    {
        userId = 0;
        foreach (string line in File.ReadLines("/etc/passwd"))
        {
            string[] parts = line.Split(':');
            if (parts.Length < 3 || !string.Equals(parts[0], userName, StringComparison.Ordinal))
            {
                continue;
            }

            return int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out userId);
        }

        return false;
    }

    private sealed record PendingActivationRequest(string Operation, string ReleaseId, string? PlatformExtractedPath, DateTimeOffset RequestedAtUtc);

    private sealed record InstalledServiceScope(string? Scope, string? Username, string? Target);

    private void LogResolvedInstallRoots(string baseDirectory, string releaseId)
    {
        string applianceRoot = Path.GetDirectoryName(Path.GetDirectoryName(ResolveManagerInstallRoot(baseDirectory))!)!;
        string[] installKeys = ["manager", .. HostServiceVersionActivator.VersionedPayloadKeys];

        foreach (string serviceKey in installKeys)
        {
            string currentPath = Path.Combine(applianceRoot, serviceKey, "current");
            string versionsRoot = Path.Combine(applianceRoot, serviceKey, "versions");
            string targetVersionPath = Path.Combine(versionsRoot, releaseId);
            string resolvedCurrentPath = Directory.Exists(currentPath)
                ? Path.GetFullPath(currentPath)
                : "<missing>";

            logger.LogInformation(
                "Post-activation filesystem state for {ServiceKey}. CurrentPath={CurrentPath}, ResolvedCurrentPath={ResolvedCurrentPath}, TargetVersionPath={TargetVersionPath}, TargetVersionExists={TargetVersionExists}",
                serviceKey,
                currentPath,
                resolvedCurrentPath,
                targetVersionPath,
                Directory.Exists(targetVersionPath));
        }
    }

    private async Task<bool> TryRunActivationCommandAsync(string operation, string releaseId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(updateOptions.HostServiceActivationCommand))
        {
            return false;
        }

        string[] hostServiceUnits = HostServiceVersionActivator.HostServiceKeys.Select(static key => $"kautuka-{key}.service").ToArray();
        string[] allUnits = [updateServiceUnitName, "kautuka-manager.service", .. hostServiceUnits];
        string command = updateOptions.HostServiceActivationCommand
            .Replace("{operation}", EscapeShellToken(operation), StringComparison.Ordinal)
            .Replace("{release_id}", EscapeShellToken(releaseId), StringComparison.Ordinal)
            .Replace("{manager_unit}", EscapeShellToken("kautuka-manager.service"), StringComparison.Ordinal)
            .Replace("{host_units}", string.Join(' ', hostServiceUnits.Select(EscapeShellToken)), StringComparison.Ordinal)
            .Replace("{all_units}", string.Join(' ', allUnits.Select(EscapeShellToken)), StringComparison.Ordinal);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = ResolveSafeWorkingDirectory(runtimePaths.RuntimeRoot),
            }
        };

        process.StartInfo.ArgumentList.Add("-c");
        process.StartInfo.ArgumentList.Add(command);
        process.Start();

        Task<string> stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(updateOptions.HostServiceActivationTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw new InvalidOperationException($"Configured host service activation command timed out after {updateOptions.HostServiceActivationTimeout}.");
        }

        string stdOut = await stdOutTask.ConfigureAwait(false);
        string stdErr = await stdErrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Configured host service activation command failed with exit code {process.ExitCode}: {stdErr}");
        }

        logger.LogInformation(
            "Executed configured host service activation command for update {Operation} {ReleaseId}. StdOut: {StdOut}",
            operation,
            releaseId,
            stdOut.Trim());

        return true;
    }

    private async ValueTask<bool> TryRestartIntoTargetUpdateServiceAsync(string operation, string releaseId, CancellationToken cancellationToken)
    {
        string launcherPath = await EnsureActivationLauncherAsync(cancellationToken).ConfigureAwait(false);
        string requestPath = Path.Combine(runtimePaths.RuntimeRoot, pendingActivationRequestRelativePath);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = launcherPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = ResolveSafeWorkingDirectory(runtimePaths.RuntimeRoot),
            }
        };

        process.StartInfo.ArgumentList.Add(operation);
        process.StartInfo.ArgumentList.Add(releaseId);
        process.StartInfo.ArgumentList.Add(requestPath);
        process.Start();

        Task<string> stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(updateOptions.HostServiceActivationTimeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation(
                "Staged UpdateService handoff for release {ReleaseId} is continuing after the current process restart.",
                releaseId);
            return true;
        }

        string stdOut = await stdOutTask.ConfigureAwait(false);
        string stdErr = await stdErrTask.ConfigureAwait(false);

        if (IsExpectedRestartHandoffExitCode(process.ExitCode))
        {
            logger.LogInformation(
                "Staged UpdateService handoff for release {ReleaseId} was terminated by SIGTERM during service restart; treating as successful handoff.",
                releaseId);
            return true;
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"UpdateService handoff launcher failed with exit code {process.ExitCode}: {stdErr}");
        }

        logger.LogInformation(
            "Staged UpdateService handoff launcher exited for release {ReleaseId}. StdOut: {StdOut}",
            releaseId,
            stdOut.Trim());
        return true;
    }

    internal static bool IsExpectedRestartHandoffExitCode(int exitCode)
        => exitCode == terminatedBySigTermExitCode;

    private async ValueTask<string> EnsureActivationLauncherAsync(CancellationToken cancellationToken)
    {
        string launcherPath = Path.Combine(runtimePaths.RuntimeRoot, activationLauncherRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(launcherPath)!);

        string script = string.Join(
            '\n',
            "#!/usr/bin/env bash",
            "set -euo pipefail",
            "operation=\"${1:-}\"",
            "release_id=\"${2:-}\"",
            "request_path=\"${3:-}\"",
            "[[ -n \"$operation\" ]] || { echo 'missing operation' >&2; exit 64; }",
            "[[ -n \"$release_id\" ]] || { echo 'missing release id' >&2; exit 64; }",
            "[[ -n \"$request_path\" ]] || { echo 'missing request path' >&2; exit 64; }",
            "[[ -f \"$request_path\" ]] || { echo \"activation request not found: $request_path\" >&2; exit 66; }",
            "# shellcheck disable=SC1090",
            ". \"$request_path\"",
            "[[ \"$operation\" == \"${ACTIVATION_OPERATION:-}\" ]] || { echo 'activation operation mismatch' >&2; exit 65; }",
            "[[ \"$release_id\" == \"${ACTIVATION_RELEASE_ID:-}\" ]] || { echo 'activation release mismatch' >&2; exit 65; }",
            "[[ \"${ACTIVATION_OPERATION:-}\" == 'apply' ]] || { echo 'only apply handoff is supported' >&2; exit 65; }",
            "source_root=\"${ACTIVATION_PLATFORM_EXTRACTED_PATH}/services/updateservice\"",
            "[[ -d \"$source_root\" ]] || { echo \"staged UpdateService payload not found: $source_root\" >&2; exit 66; }",
            "install_root='/opt/kautuka/updateservice'",
            "version_root=\"$install_root/versions/$release_id\"",
            "current_link=\"$install_root/current\"",
            "next_link=\"$install_root/current.next\"",
            "rm -rf \"$version_root\"",
            "mkdir -p \"$version_root\"",
            "cp -a \"$source_root/.\" \"$version_root/\"",
            "chmod +x \"$version_root/kautuka-updateservice\"",
            "ln -sfn \"$version_root\" \"$next_link\"",
            "mv -Tf \"$next_link\" \"$current_link\"",
            "if [[ -f '/etc/kautuka/service-scope.json' ]] && grep -q '\"scope\"[[:space:]]*:[[:space:]]*\"user\"' '/etc/kautuka/service-scope.json'; then",
            "  : \"${XDG_RUNTIME_DIR:=/run/user/$(id -u)}\"",
            "  : \"${DBUS_SESSION_BUS_ADDRESS:=unix:path=${XDG_RUNTIME_DIR}/bus}\"",
            "  export XDG_RUNTIME_DIR DBUS_SESSION_BUS_ADDRESS",
            "  exec systemctl --user restart kautuka-updateservice.service",
            "fi",
            "helper_path='/usr/local/lib/kautuka/restart-system-units'",
            "[[ -x \"$helper_path\" ]] || { echo \"activation helper not found: $helper_path\" >&2; exit 66; }",
            $"exec {sudoExecutablePath} -n \"$helper_path\" kautuka-updateservice.service",
            string.Empty);

        await File.WriteAllTextAsync(launcherPath, script, cancellationToken).ConfigureAwait(false);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(
                launcherPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        return launcherPath;
    }

    private async ValueTask WritePendingActivationRequestAsync(PendingActivationRequest request, CancellationToken cancellationToken)
    {
        string requestPath = Path.Combine(runtimePaths.RuntimeRoot, pendingActivationRequestRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(requestPath)!);

        string[] lines =
        [
            $"ACTIVATION_OPERATION={request.Operation}",
            $"ACTIVATION_RELEASE_ID={request.ReleaseId}",
            $"ACTIVATION_PLATFORM_EXTRACTED_PATH={request.PlatformExtractedPath ?? string.Empty}",
            $"ACTIVATION_REQUESTED_AT_UTC={request.RequestedAtUtc:O}",
        ];

        string tempPath = requestPath + ".tmp";
        await File.WriteAllLinesAsync(tempPath, lines, cancellationToken).ConfigureAwait(false);
        File.Move(tempPath, requestPath, overwrite: true);
    }

    private PendingActivationRequest? TryReadPendingActivationRequest()
    {
        string requestPath = Path.Combine(runtimePaths.RuntimeRoot, pendingActivationRequestRelativePath);
        if (!File.Exists(requestPath))
        {
            return null;
        }

        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(requestPath))
        {
            int separatorIndex = line.IndexOf('=', StringComparison.Ordinal);
            if (separatorIndex <= 0)
            {
                continue;
            }

            string key = line[..separatorIndex].Trim();
            string value = line[(separatorIndex + 1)..].Trim();
            values[key] = value;
        }

        if (!values.TryGetValue("ACTIVATION_OPERATION", out string? operation)
            || !values.TryGetValue("ACTIVATION_RELEASE_ID", out string? releaseId))
        {
            return null;
        }

        DateTimeOffset requestedAtUtc = DateTimeOffset.UtcNow;
        if (values.TryGetValue("ACTIVATION_REQUESTED_AT_UTC", out string? requestedAtRaw)
            && DateTimeOffset.TryParse(requestedAtRaw, out DateTimeOffset parsedRequestedAtUtc))
        {
            requestedAtUtc = parsedRequestedAtUtc;
        }

        values.TryGetValue("ACTIVATION_PLATFORM_EXTRACTED_PATH", out string? platformExtractedPath);
        return new PendingActivationRequest(operation, releaseId, string.IsNullOrWhiteSpace(platformExtractedPath) ? null : platformExtractedPath, requestedAtUtc);
    }

    private void ClearPendingActivationRequest()
    {
        string requestPath = Path.Combine(runtimePaths.RuntimeRoot, pendingActivationRequestRelativePath);
        if (File.Exists(requestPath))
        {
            File.Delete(requestPath);
        }
    }

    private string? TryResolveCurrentInstalledReleaseId(string baseDirectory, string serviceKey)
    {
        string applianceRoot = ResolveApplianceRoot(ResolveServiceInstallRoot(baseDirectory));
        string serviceInstallRoot = Path.Combine(applianceRoot, serviceKey, "current");
        DirectoryInfo serviceDirectory = new(serviceInstallRoot);

        string? linkTarget = serviceDirectory.LinkTarget;
        if (!string.IsNullOrWhiteSpace(linkTarget))
        {
            return Path.GetFileName(Path.TrimEndingDirectorySeparator(linkTarget));
        }

        string resolvedPath = Path.GetFullPath(serviceInstallRoot);
        DirectoryInfo resolvedDirectory = new(resolvedPath);
        DirectoryInfo? parentDirectory = resolvedDirectory.Parent;
        if (parentDirectory is not null && string.Equals(parentDirectory.Name, "versions", StringComparison.OrdinalIgnoreCase))
        {
            return resolvedDirectory.Name;
        }

        return null;
    }

    private static string EscapeShellToken(string value)
        => $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    private static string ResolveSafeWorkingDirectory(string preferredDirectory)
    {
        if (!string.IsNullOrWhiteSpace(preferredDirectory) && Directory.Exists(preferredDirectory))
        {
            return preferredDirectory;
        }

        return "/";
    }

    private static void SyncProvisionedConfiguration(string? platformExtractedPath, string updateSourceDestinationPath, string manifestPublicKeyDestinationPath, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (string.IsNullOrWhiteSpace(platformExtractedPath))
        {
            return;
        }

        string provisionedConfigRoot = Path.Combine(platformExtractedPath, provisionedConfigRelativeRoot);
        if (!Directory.Exists(provisionedConfigRoot))
        {
            logger.LogInformation(
                "Skipping provisioned configuration sync because no staged config directory exists at {ProvisionedConfigRoot}.",
                provisionedConfigRoot);
            return;
        }

        SyncProvisionedFileIfExists(
            Path.Combine(provisionedConfigRoot, "update-source.json"),
            updateSourceDestinationPath,
            logger);
        SyncProvisionedFileIfExists(
            Path.Combine(provisionedConfigRoot, "manifest-public-key.pem"),
            manifestPublicKeyDestinationPath,
            logger);
    }

    private static void EnsureProvisionedConfigurationWritable(string? platformExtractedPath, string updateSourceDestinationPath, string manifestPublicKeyDestinationPath, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (string.IsNullOrWhiteSpace(platformExtractedPath))
        {
            return;
        }

        string provisionedConfigRoot = Path.Combine(platformExtractedPath, provisionedConfigRelativeRoot);
        if (!Directory.Exists(provisionedConfigRoot))
        {
            logger.LogInformation(
                "Skipping provisioned configuration preflight because no staged config directory exists at {ProvisionedConfigRoot}.",
                provisionedConfigRoot);
            return;
        }

        EnsureProvisionedDestinationWritableIfSourceExists(
            Path.Combine(provisionedConfigRoot, "update-source.json"),
            updateSourceDestinationPath);
        EnsureProvisionedDestinationWritableIfSourceExists(
            Path.Combine(provisionedConfigRoot, "manifest-public-key.pem"),
            manifestPublicKeyDestinationPath);
    }

    private static void EnsureProvisionedDestinationWritableIfSourceExists(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath))
        {
            return;
        }

        try
        {
            string destinationDirectory = Path.GetDirectoryName(destinationPath)
                ?? throw new InvalidOperationException($"Destination path '{destinationPath}' does not have a parent directory.");

            Directory.CreateDirectory(destinationDirectory);

            if (File.Exists(destinationPath))
            {
                using FileStream _ = new(destinationPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                return;
            }

            string probePath = Path.Combine(destinationDirectory, $".kautuka-write-probe-{Guid.NewGuid():N}.tmp");
            using (FileStream _ = new(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
            }

            File.Delete(probePath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException(
                $"Provisioned configuration destination '{destinationPath}' is not writable. Update activation cannot switch releases until the staged updater can replace this file.",
                ex);
        }
    }

    private static void SyncProvisionedFileIfExists(string sourcePath, string destinationPath, ILogger logger)
    {
        if (!File.Exists(sourcePath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Copy(sourcePath, destinationPath, overwrite: true);

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(
                destinationPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.GroupRead |
                UnixFileMode.OtherRead);
        }

        logger.LogInformation(
            "Synchronized provisioned configuration file from {SourcePath} to {DestinationPath}.",
            sourcePath,
            destinationPath);
    }

    private static void CopyFileIfExists(string sourcePath, string destinationPath)
    {
        if (!File.Exists(sourcePath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        File.Copy(sourcePath, destinationPath, overwrite: true);
    }

    private static void RestoreFileIfExists(string sourcePath, string destinationPath)
        => CopyFileIfExists(sourcePath, destinationPath);

    private static void CopyDirectoryIfExists(string sourceDirectory, string destinationDirectory, params string[] excludedRelativePaths)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            return;
        }

        HashSet<string> excludedPathSet = excludedRelativePaths.Length == 0
            ? []
            : excludedRelativePaths
                .Select(static path => path.Replace('\\', '/').Trim('/'))
                .Where(static path => path.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Directory.CreateDirectory(destinationDirectory);

        foreach (string directory in Directory.GetDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(sourceDirectory, directory).Replace('\\', '/');
            if (ShouldExcludeRelativePath(relativePath, excludedPathSet))
            {
                continue;
            }

            Directory.CreateDirectory(Path.Combine(destinationDirectory, relativePath));
        }

        foreach (string file in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(sourceDirectory, file).Replace('\\', '/');
            if (ShouldExcludeRelativePath(relativePath, excludedPathSet))
            {
                continue;
            }

            string destinationPath = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(file, destinationPath, overwrite: true);
        }
    }

    private static bool ShouldExcludeRelativePath(string relativePath, HashSet<string> excludedPathSet)
    {
        foreach (string excludedPath in excludedPathSet)
        {
            if (string.Equals(relativePath, excludedPath, StringComparison.OrdinalIgnoreCase)
                || relativePath.StartsWith(excludedPath + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void RestoreDirectoryIfExists(string sourceDirectory, string destinationDirectory, params string[] preservedRelativePaths)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            return;
        }

        HashSet<string> preservedPathSet = preservedRelativePaths.Length == 0
            ? []
            : preservedRelativePaths
                .Select(static path => path.Replace('\\', '/').Trim('/'))
                .Where(static path => path.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (preservedPathSet.Count == 0)
        {
            if (Directory.Exists(destinationDirectory))
            {
                Directory.Delete(destinationDirectory, recursive: true);
            }

            HostServiceVersionActivator.CopyDirectoryContents(sourceDirectory, destinationDirectory);
            return;
        }

        Directory.CreateDirectory(destinationDirectory);

        foreach (string filePath in Directory.GetFiles(destinationDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            string relativePath = Path.GetFileName(filePath).Replace('\\', '/');
            if (!ShouldExcludeRelativePath(relativePath, preservedPathSet))
            {
                File.Delete(filePath);
            }
        }

        foreach (string directoryPath in Directory.GetDirectories(destinationDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            string relativePath = Path.GetFileName(directoryPath).Replace('\\', '/');
            if (!ShouldExcludeRelativePath(relativePath, preservedPathSet))
            {
                Directory.Delete(directoryPath, recursive: true);
            }
        }

        HostServiceVersionActivator.CopyDirectoryContents(sourceDirectory, destinationDirectory);
    }
}
