namespace Kautuka.Installer.Windows.Installation;

internal static partial class WindowsApplianceInstaller
{
    private const string rootfsAssetName = "kautuka-wsl-appliance.tar.gz";
    private const string releaseManifestAssetName = "kautuka-platform-release.json";
    private const string webUrl = "http://localhost:20229";
    private const string defaultDistroName = "kautuka";
    private static readonly char[] newLineSeparators = ['\r', '\n'];

    public static WindowsApplianceStatusResponse GetStatus()
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
            HasResumeState = WindowsApplianceStateStore.Exists(),
            DefaultKautukaRoot = defaultInstallDir,
            DefaultInstallDir = defaultInstallDir,
            FeatureStates = featureStates,
        };
    }

    public static Task<string?> PickFolderForInstallAsync(string? initialPath, CancellationToken cancellationToken)
        => PickFolderAsync(initialPath, cancellationToken);

    public static async Task InstallAsync(
        WindowsApplianceInstallRequest request,
        Func<string, Task> log,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("The appliance installer is only supported on Windows hosts.");

        cancellationToken.ThrowIfCancellationRequested();

        var state = request.Resume
            ? WindowsApplianceStateStore.Load()
            : await ResolveStateAsync(request, log, cancellationToken).ConfigureAwait(false);
        if (request.AllowLanAccess && !state.AllowLanAccess)
            state = state with { AllowLanAccess = true };

        await log($"Preparing Windows appliance install for distro '{state.DistroName}'...").ConfigureAwait(false);

        await EnsureWslPrerequisitesAsync(state, log, cancellationToken).ConfigureAwait(false);

        state = StageAssets(state);
        await log("Staged installer executable.").ConfigureAwait(false);

        await EnsureDistroImportedAsync(state, log, cancellationToken).ConfigureAwait(false);
        await ConfigureDistroAsync(state, log, cancellationToken).ConfigureAwait(false);
        await WaitForWebUiReadyAsync(log, cancellationToken).ConfigureAwait(false);
        await ApplyLanChoiceAsync(state.AllowLanAccess, log, cancellationToken).ConfigureAwait(false);

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

        await RemoveMonitorResidencyAsync(request.DistroName, cancellationToken).ConfigureAwait(false);
        await RemoveLanAccessAsync(cancellationToken).ConfigureAwait(false);

        await RunProcessAsync(
            "wsl.exe",
            ["--unregister", request.DistroName],
            allowFailure: true,
            cancellationToken).ConfigureAwait(false);

        foreach (string shortcutName in new[] { "Kautuka.url", "Kautuka.lnk" })
        {
            string shortcutPath = DesktopFile(shortcutName);
            if (File.Exists(shortcutPath))
                File.Delete(shortcutPath);
        }

        PlatformInstaller.UnregisterWindowsAppliance();

        var installDir = string.IsNullOrWhiteSpace(request.InstallDir)
            ? DeriveInstallDir(request.DistroName)
            : request.InstallDir;

        if (request.DeleteData && Directory.Exists(installDir))
        {
            Directory.Delete(installDir, recursive: true);
        }
    }

}
