namespace Kautuka.Installer.Windows.Installation;

internal static partial class WindowsApplianceInstaller
{
    private const string rootfsAssetName = "kautuka-wsl-appliance.tar.gz";
    private const string releaseManifestAssetName = "kautuka-platform-release.json";
    private const string releaseManifestSignatureAssetName = "kautuka-platform-release.sig";
    private const string webUrl = "http://localhost:20229";
    private const string defaultDistroName = "kautuka";
    private static readonly char[] newLineSeparators = ['\r', '\n'];

    public static WindowsApplianceStatusResponse GetStatus(string distroName = defaultDistroName)
    {
        var defaultInstallDir = DeriveInstallDir(defaultDistroName);

        Dictionary<string, string> featureStates = [];
        var isWindows = OperatingSystem.IsWindows();
        if (isWindows)
        {
            featureStates = GetWslFeatureStates();
        }

        var wslFeaturesEnabled = isWindows && AreWslFeaturesEnabled(featureStates);
        var wslAvailable = isWindows && CommandAvailable("wsl.exe");

        return new WindowsApplianceStatusResponse
        {
            IsWindows = isWindows,
            IsAdministrator = isWindows && IsAdministrator(),
            WslAvailable = wslAvailable,
            WslFeaturesEnabled = wslFeaturesEnabled,
            WslReady = wslAvailable && wslFeaturesEnabled,
            ExistingDistro = isWindows && (IsDistroRegistered(distroName) || IsDistroRegistered(defaultDistroName) || WindowsApplianceStateStore.InstalledExists() || HasWindowsRegistration()),
            HasResumeState = WindowsApplianceStateStore.Matches(distroName),
            DefaultKautukaRoot = defaultInstallDir,
            DefaultInstallDir = defaultInstallDir,
            FeatureStates = featureStates,
        };
    }

    public static Task<string?> PickFolderForInstallAsync(string? initialPath, CancellationToken cancellationToken)
        => PickFolderAsync(initialPath, cancellationToken);

    public static async Task StartExistingAsync(string distroName, CancellationToken cancellationToken)
    {
        var ownership = WindowsApplianceStateStore.LoadInstalled();
        string? sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        string name = ownership.DistroName;
        string path = ownership.InstallDir;
        var registered = GetDistroRegistration(name);
        if (!string.Equals(name, distroName, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(path)
            || registered is null || !PathsEqual(path, registered.Value.Path)
            || !MatchesOwnedRegistration(ownership, sid, name, path, registered.Value.Id))
            throw new InvalidOperationException("The existing environment does not match the Kautuka installation record.");
        await WaitForWebUiReadyAsync(name, _ => Task.CompletedTask, cancellationToken).ConfigureAwait(false);
    }

    public static async Task InstallAsync(
        WindowsApplianceInstallRequest request,
        Func<string, Task> log,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("The appliance installer is only supported on Windows hosts.");

        cancellationToken.ThrowIfCancellationRequested();

        if (!request.Resume && (IsDistroRegistered(request.DistroName) || IsDistroRegistered(defaultDistroName)
            || WindowsApplianceStateStore.InstalledExists() || HasWindowsRegistration()))
            throw new InvalidOperationException($"A WSL distribution named '{request.DistroName}' already exists. Open Kautuka to check for updates; installation has not modified it.");

        var state = request.Resume
            ? WindowsApplianceStateStore.Load()
            : await ResolveStateAsync(request, log, cancellationToken).ConfigureAwait(false);
        string ownerSid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Could not identify the Windows user for this installation.");
        if (request.Resume && (!string.Equals(state.OwnerSid, ownerSid, StringComparison.Ordinal)
            || !string.Equals(state.DistroName, request.DistroName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The saved installation belongs to a different Windows user or is missing ownership information.");
        state = state with { OwnerSid = ownerSid };
        WindowsApplianceStateStore.VerifyRootfs(state);
        if (request.AllowLanAccess && !state.AllowLanAccess)
            state = state with { AllowLanAccess = true };

        await log($"Preparing Windows appliance install for distro '{state.DistroName}'...").ConfigureAwait(false);

        WindowsApplianceStateStore.Save(state);

        await EnsureWslPrerequisitesAsync(state, log, cancellationToken).ConfigureAwait(false);

        state = StageAssets(state);
        await log("Staged installer executable.").ConfigureAwait(false);

        await EnsureDistroImportedAsync(state, request.Resume, log, cancellationToken).ConfigureAwait(false);
        var imported = GetDistroRegistration(state.DistroName)
            ?? throw new InvalidOperationException("The imported Kautuka environment was not registered.");
        if (!PathsEqual(imported.Path, state.InstallDir))
            throw new InvalidOperationException("The imported Kautuka environment has an unexpected location.");
        state = state with { RegistrationId = imported.Id };
        WindowsApplianceStateStore.Save(state);
        await ConfigureDistroAsync(state, log, cancellationToken).ConfigureAwait(false);
        await WaitForWebUiReadyAsync(state.DistroName, log, cancellationToken).ConfigureAwait(false);
        await ApplyLanChoiceAsync(state.AllowLanAccess, log, cancellationToken).ConfigureAwait(false);

        WindowsApplianceStateStore.SaveInstalled(state);
        PlatformInstaller.RegisterWindowsApplianceUninstall(
            state.InstallerExecutablePath,
            state.ReleaseId,
            state.DistroName,
            state.InstallDir);

        var supervisor = await ApplianceSupervisor.RunAsync(state.DistroName, state.InstallDir, cancellationToken)
            .ConfigureAwait(false);
        var monitorRegistered = await TryRegisterMonitorAsync(state, log, cancellationToken).ConfigureAwait(false);
        if (supervisor.Error is not null)
            await log($"WSL keepalive warning: {supervisor.Error}").ConfigureAwait(false);

        if (supervisor.KeepaliveStarted)
            await log("Started a WSL keepalive so the appliance stays up.").ConfigureAwait(false);
        else if (supervisor.Error is null)
            await log("WSL keepalive already held.").ConfigureAwait(false);

        if (monitorRegistered && supervisor.Error is null)
        {
            await log("Kautuka Monitor is registered to start the appliance when you sign in and after Windows starts.")
                .ConfigureAwait(false);
        }
        else
        {
            await log("Kautuka Monitor was not registered. Windows will not start the appliance after reboot until that registration succeeds.").ConfigureAwait(false);
        }

        WindowsApplianceStateStore.Delete();
        try { WindowsApplianceStateStore.DeleteRootfs(state); }
        catch (IOException ex) { await log($"The installation succeeded, but its temporary archive could not be removed: {ex.Message}").ConfigureAwait(false); }
        catch (UnauthorizedAccessException ex) { await log($"The installation succeeded, but its temporary archive could not be removed: {ex.Message}").ConfigureAwait(false); }

        await log($"Kautuka appliance installed. Open {webUrl}.").ConfigureAwait(false);
        await log($"WSL distro: {state.DistroName}").ConfigureAwait(false);
        await log($"WSL install directory: {state.InstallDir}").ConfigureAwait(false);
    }

    public static async Task UninstallAsync(
        WindowsApplianceUninstallRequest request,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("The appliance installer is only supported on Windows hosts.");

        cancellationToken.ThrowIfCancellationRequested();

        var ownership = WindowsApplianceStateStore.LoadInstalled();
        string? currentSid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        if (currentSid is null || !string.Equals(currentSid, ownership.OwnerSid, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(ownership.RegistrationId))
            throw new InvalidOperationException("The protected installation record does not identify this Windows user and appliance. Nothing was removed.");
        string recordedName = ownership.DistroName;
        string recordedPath = ownership.InstallDir;
        if (string.IsNullOrWhiteSpace(recordedPath) || string.IsNullOrWhiteSpace(request.InstallDir)
            || !string.Equals(request.DistroName, recordedName, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(recordedName, ownership.DistroName, StringComparison.OrdinalIgnoreCase)
            || !PathsEqual(recordedPath, request.InstallDir) || !PathsEqual(recordedPath, ownership.InstallDir))
            throw new InvalidOperationException("The requested uninstall target does not match the Kautuka installation record. Nothing was removed.");

        var registered = GetDistroRegistration(recordedName);
        if (registered is not null && (!PathsEqual(registered.Value.Path, recordedPath)
            || !MatchesOwnedRegistration(ownership, currentSid, recordedName, registered.Value.Path, registered.Value.Id)))
            throw new InvalidOperationException("The registered distribution is in a different folder. Nothing was removed.");

        if (registered is not null)
        {
            var removal = await RunProcessAsync("wsl.exe", ["--unregister", recordedName],
                allowFailure: true, cancellationToken).ConfigureAwait(false);
            if (removal.ExitCode != 0 && !IsDistroNotFound(removal))
                throw new InvalidOperationException("Windows could not remove the Kautuka environment. Its uninstall entry was retained so you can retry.");
            if (GetDistroBasePath(recordedName) is not null)
                throw new InvalidOperationException("The Kautuka environment is still registered. Its uninstall entry was retained so you can retry.");
        }

        await RemoveMonitorResidencyAsync(recordedName, cancellationToken).ConfigureAwait(false);
        await RemoveLanAccessAsync(cancellationToken).ConfigureAwait(false);

        foreach (string shortcutName in new[] { "Kautuka.url", "Kautuka.lnk" })
        {
            string shortcutPath = DesktopFile(shortcutName);
            if (File.Exists(shortcutPath))
                File.Delete(shortcutPath);
        }

        PlatformInstaller.UnregisterWindowsAppliance();
        WindowsApplianceStateStore.DeleteInstalled();

        if (request.DeleteData && Directory.Exists(recordedPath))
        {
            // Never recursively delete a caller-selected Windows folder. WSL unregister
            // removes its virtual disk; remove the folder only when it is empty.
            if (!Directory.EnumerateFileSystemEntries(recordedPath).Any())
                Directory.Delete(recordedPath);
        }
    }

}
