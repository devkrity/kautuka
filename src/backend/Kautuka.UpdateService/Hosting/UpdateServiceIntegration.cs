using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;

using Grpc.Core;

using Kautuka.UpdateService.Abstractions;
using Kautuka.UpdateService.Models;

using Microsoft.Extensions.Options;

namespace Kautuka.UpdateService.Hosting;

public sealed class UpdateServiceIntegration(
    UpdateServiceRuntimePaths runtimePaths,
    IOptions<UpdateOptions> updateOptions,
    IManagerStatusClient managerStatusClient,
    IHostApplicationLifetime applicationLifetime,
    ILogger<UpdateServiceIntegration> logger) : IUpdateServiceIntegration
{
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
    private const string recoveryJournalPath = "/var/lib/kautuka-recovery/operation.json";
    private const string recoveryMaintenancePath = "/var/lib/kautuka-recovery/maintenance.json";
    private const int updateRestartExitCode = 75;
    private const int terminatedBySigTermExitCode = 143;
    private static readonly string provisionedConfigRelativeRoot = Path.Combine("provisioning", "etc", "kautuka");
    private static readonly string[] orderedRuntimeServiceUnits =
    [
        "kautuka-credentialshost.service",
        "kautuka-manager.service",
        "kautuka-gateway.service",
        "kautuka-channelhost.service",
        "kautuka-webhost.service",
    ];

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

        (bool pointersConsistent, bool unitsActive) = ObserveReleaseHealth(managerReleaseId);
        bool updaterRunningCurrent = IsRunningCurrentUpdateService(AppContext.BaseDirectory);

        RecoveryJournal? recoveryJournal = ReadRecoveryJournal();
        return new UpdateServiceContext(
            runtimePaths.RuntimeRoot,
            managerInstallRoot,
            managerReleaseId,
            managerVersion,
            isReady,
            activeExecCount,
            hostStatusObserved,
            unitsActive,
            pointersConsistent,
            updaterRunningCurrent,
            OperatingSystem.IsLinux() && File.Exists(recoveryMaintenancePath),
            recoveryJournal?.OperationId,
            recoveryJournal?.Phase,
            recoveryJournal?.SourceRelease,
            recoveryJournal?.TargetRelease,
            recoveryJournal?.Snapshot);
    }

    public ValueTask RequestSupervisorReconcileAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    private async Task<string> RunApplyReleaseHelperAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
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
                return stdOut;
            }

            throw new InvalidOperationException($"Apply-release helper failed with exit code {process.ExitCode}: {stdErr}");
        }

        logger.LogInformation("Apply-release helper completed. StdOut: {StdOut}", stdOut.Trim());
        return stdOut;
    }

    private static string? TryResolveExpectedVersion(IReadOnlyList<string> arguments)
    {
        for (int i = 0; i < arguments.Count - 1; i++)
        {
            if (string.Equals(arguments[i], "--version", StringComparison.Ordinal)
                || string.Equals(arguments[i], "--complete-recovery", StringComparison.Ordinal))
            {
                return arguments[i + 1];
            }
        }

        return null;
    }

    private static RecoveryJournal? ReadRecoveryJournal()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(recoveryJournalPath))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(recoveryJournalPath));
            JsonElement root = document.RootElement;
            return new RecoveryJournal(
                ReadString(root, "operationId"),
                ReadString(root, "phase"),
                ReadString(root, "sourceRelease"),
                ReadString(root, "targetRelease"),
                ReadString(root, "snapshot"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private async ValueTask<bool> ContinueSafeRecoveryAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(recoveryMaintenancePath))
        {
            return false;
        }

        RecoveryJournal? journal = ReadRecoveryJournal();
        if (journal is null || string.IsNullOrWhiteSpace(journal.Phase))
        {
            logger.LogError("Safe update recovery marker exists but its durable journal is unreadable; keeping the appliance in maintenance.");
            return false;
        }

        if (string.Equals(journal.Phase, "verifying", StringComparison.Ordinal)
            || string.Equals(journal.Phase, "committed", StringComparison.Ordinal))
        {
            if (await WaitForReleaseHealthAsync(journal.TargetRelease, requireCurrentUpdater: true, cancellationToken).ConfigureAwait(false))
            {
                await RunApplyReleaseHelperAsync(["--commit", "--version", journal.TargetRelease!], cancellationToken).ConfigureAwait(false);
                RequestHostRestart("committed update", journal.TargetRelease!);
                return true;
            }

            if (string.Equals(journal.Phase, "committed", StringComparison.Ordinal))
            {
                logger.LogError("Committed release {ReleaseId} is not healthy. Keeping maintenance enabled; committed updates are repaired by rolling forward.", journal.TargetRelease);
                return false;
            }

            logger.LogWarning("Target release {ReleaseId} did not pass readiness before the safe recovery deadline; restoring the verified pre-update snapshot.", journal.TargetRelease);
            await RecoverAndHandoffToSourceAsync(journal.SourceRelease, cancellationToken).ConfigureAwait(false);
            journal = ReadRecoveryJournal();
        }
        else if (!string.Equals(journal.Phase, "recovered", StringComparison.Ordinal))
        {
            await RecoverAndHandoffToSourceAsync(journal.SourceRelease, cancellationToken).ConfigureAwait(false);
            journal = ReadRecoveryJournal();
        }

        if (journal is not null
            && string.Equals(journal.Phase, "recovered", StringComparison.Ordinal)
            && !IsRunningCurrentUpdateService(AppContext.BaseDirectory)
            && RequestRestartForSwitchedUpdateService("safe recovery", journal.SourceRelease ?? string.Empty))
        {
            return true;
        }

        if (journal is null || !string.Equals(journal.Phase, "recovered", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(journal.SourceRelease))
        {
            return false;
        }

        if (!await WaitForReleaseHealthAsync(journal.SourceRelease, requireCurrentUpdater: true, cancellationToken).ConfigureAwait(false))
        {
            logger.LogError("Restored source release {ReleaseId} did not become healthy; maintenance remains enabled.", journal.SourceRelease);
            return false;
        }

        await RunApplyReleaseHelperAsync(["--complete-recovery", "--version", journal.SourceRelease], cancellationToken).ConfigureAwait(false);
        RequestHostRestart("completed safe recovery", journal.SourceRelease);
        return true;
    }

    private async ValueTask RecoverAndHandoffToSourceAsync(string? sourceRelease, CancellationToken cancellationToken)
    {
        try
        {
            await RunApplyReleaseHelperAsync(["--recover"], cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // A failed recovery attempt may still have restored and verified the
            // source pointers but remain fenced while Gateway republishes Manager
            // desired state. Re-enter the source updater so it can retry completion.
            RecoveryJournal? journal = ReadRecoveryJournal();
            if (journal is not null && !string.IsNullOrWhiteSpace(sourceRelease)
                && string.Equals(journal.SourceRelease, sourceRelease, StringComparison.Ordinal))
            {
                RequestRestartForSwitchedUpdateService("safe recovery", sourceRelease);
            }
        }
    }

    private async Task<bool> WaitForReleaseHealthAsync(string? releaseId, bool requireCurrentUpdater, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(releaseId))
        {
            return false;
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(90);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UpdateServiceContext context = await GetHostContextAsync(cancellationToken).ConfigureAwait(false);
            if (string.Equals(context.ManagerReleaseId, releaseId, StringComparison.OrdinalIgnoreCase)
                && context.HostStatusObserved
                && context.IsReady
                && context.RuntimeUnitsActive
                && context.CurrentPointersConsistent
                && (!requireCurrentUpdater || context.UpdateServiceRunningCurrent))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private sealed record RecoveryJournal(string? OperationId, string? Phase, string? SourceRelease, string? TargetRelease, string? Snapshot);

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

        string capability = await RunApplyReleaseHelperAsync(["--capabilities"], cancellationToken).ConfigureAwait(false);
        if (!capability.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Any(line => string.Equals(line.Trim(), "safe-state-recovery-v5", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Installed apply-release helper does not support verified database snapshots and maintenance fencing. Run the current signed Linux installer bootstrap before updating.");
        }

        try
        {
            await RunApplyReleaseHelperAsync(
                ["--artifact", preparedArtifacts.PlatformPackagePath, "--version", preparedArtifacts.TargetReleaseId],
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception applyException)
        {
            if (File.Exists(recoveryMaintenancePath))
            {
                try
                {
                    bool recoveryCompleted = await ContinueSafeRecoveryAsync(CancellationToken.None).ConfigureAwait(false);
                    if (!recoveryCompleted)
                    {
                        logger.LogError(applyException, "Update apply failed and automatic database recovery remains fenced for boot-time continuation.");
                    }
                }
                catch (Exception recoveryException)
                {
                    logger.LogError(recoveryException, "Automatic pre-commit recovery failed; maintenance remains enabled.");
                }
            }

            throw;
        }

        RequestRestartForSwitchedUpdateService("apply", preparedArtifacts.TargetReleaseId);
    }

    public ValueTask ActivateRollbackAsync(UpdateReleaseReference release, CancellationToken cancellationToken)
    {
        _ = release;
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("User requested release rollback is disabled. Apply a forward update to repair the installed release.");
    }

    public ValueTask<bool> TryContinuePendingActivationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ContinueSafeRecoveryAsync(cancellationToken);
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

    private bool RequestRestartForSwitchedUpdateService(string operation, string releaseId)
    {
        // The helper has completed outside the user service's cgroup. Stop only
        // after it returns, so systemd can start the new updater via current.
        if (OperatingSystem.IsLinux() && !IsRunningCurrentUpdateService(AppContext.BaseDirectory))
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("INVOCATION_ID")))
            {
                logger.LogWarning("The updater is not running the current release but no systemd supervisor was detected; restart UpdateService to complete {Operation} of {ReleaseId}.", operation, releaseId);
                return false;
            }

            RequestHostRestart(operation, releaseId);
            return true;
        }

        return false;
    }

    internal static bool IsRunningCurrentUpdateService(string baseDirectory)
    {
        if (!OperatingSystem.IsLinux())
        {
            return true;
        }

        string? executable = new FileInfo("/proc/self/exe").LinkTarget;
        string applianceRoot = ResolveApplianceRoot(ResolveServiceInstallRoot(baseDirectory));
        string managedRoot = Path.Combine(applianceRoot, "updateservice");
        // Development/test processes are not launched from the appliance tree.
        if (!Path.GetFullPath(baseDirectory).StartsWith(managedRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return true;
        }

        return IsExecutableInCurrentVersion(executable, Path.Combine(managedRoot, "current"));
    }

    internal static bool IsExecutableInCurrentVersion(string? executable, string current)
    {
        if (string.IsNullOrWhiteSpace(executable) || executable.EndsWith(" (deleted)", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            FileSystemInfo? target = Directory.ResolveLinkTarget(current, returnFinalTarget: true);
            return target is not null
                && string.Equals(Path.GetDirectoryName(executable), target.FullName, StringComparison.Ordinal)
                && string.Equals(Path.GetFileName(executable), "kautuka-updateservice", StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
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

        DirectoryInfo directory = new(Path.TrimEndingDirectorySeparator(resolved));
        string? serviceRoot = Path.GetDirectoryName(Path.GetFullPath(currentPath));
        if (serviceRoot is null)
        {
            return false;
        }

        return string.Equals(directory.Name, expectedVersion, StringComparison.Ordinal)
            && string.Equals(directory.Parent?.FullName, Path.Combine(serviceRoot, "versions"), StringComparison.Ordinal);
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


}
