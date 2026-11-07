namespace Kautuka.Installer.Windows.Installation;

internal static partial class WindowsApplianceInstaller
{
    private static bool IsDistroRegistered(string distroName)
        => GetDistroBasePath(distroName) is not null;

    private static bool HasWindowsRegistration()
    {
        using var hive = PlatformInstaller.OpenUserHive(writable: false);
        using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\KautukaWSL");
        return key is not null;
    }

    private static bool PathsEqual(string first, string second)
        => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);

    internal static bool MatchesOwnedRegistration(WindowsApplianceInstallState ownership, string? ownerSid, string name, string path, string? registrationId)
        => ownerSid is not null && !string.IsNullOrWhiteSpace(ownership.RegistrationId)
            && string.Equals(ownerSid, ownership.OwnerSid, StringComparison.Ordinal)
            && string.Equals(name, ownership.DistroName, StringComparison.OrdinalIgnoreCase)
            && PathsEqual(path, ownership.InstallDir)
            && string.Equals(registrationId, ownership.RegistrationId, StringComparison.OrdinalIgnoreCase);

    private static string? GetDistroBasePath(string distroName)
        => GetDistroRegistration(distroName)?.Path;

    private static (string Id, string Path)? GetDistroRegistration(string distroName)
    {
        using var hive = PlatformInstaller.OpenUserHive(writable: false);
        using var root = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
        if (root is null) return null;
        foreach (string name in root.GetSubKeyNames())
        {
            using var entry = root.OpenSubKey(name);
            if (string.Equals(entry?.GetValue("DistributionName") as string, distroName, StringComparison.OrdinalIgnoreCase))
                return (name, entry?.GetValue("BasePath") as string
                    ?? throw new InvalidOperationException($"The registered distribution '{distroName}' has no install path."));
        }
        return null;
    }

    private static async Task EnsureWslPrerequisitesAsync(
        WindowsApplianceInstallState state,
        Func<string, Task> log,
        CancellationToken cancellationToken)
    {
        if (!CommandAvailable("wsl.exe"))
            throw new InvalidOperationException("wsl.exe is not available on this host.");

        var featureStates = GetWslFeatureStates();
        var wslEnabled = AreWslFeaturesEnabled(featureStates);

        if (wslEnabled)
        {
            await log("WSL prerequisites already enabled.").ConfigureAwait(false);
            return;
        }

        if (!IsAdministrator())
            throw new InvalidOperationException("Enabling WSL prerequisites requires an elevated installer session.");

        WindowsApplianceStateStore.Save(state);
        RegisterResumeRunOnce(state.DistroName);

        await log("Enabling WSL prerequisites...").ConfigureAwait(false);
        var installAttempt = await RunProcessAsync(
            "wsl.exe",
            "--install --no-distribution",
            allowFailure: true,
            cancellationToken).ConfigureAwait(false);

        if (installAttempt.ExitCode != 0)
        {
            var wslEnableAttempt = await RunProcessAsync(
                "dism.exe",
                "/Online /Enable-Feature /FeatureName:Microsoft-Windows-Subsystem-Linux /All /NoRestart",
                allowFailure: true,
                cancellationToken).ConfigureAwait(false);
            var vmEnableAttempt = await RunProcessAsync(
                "dism.exe",
                "/Online /Enable-Feature /FeatureName:VirtualMachinePlatform /All /NoRestart",
                allowFailure: true,
                cancellationToken).ConfigureAwait(false);
            if (wslEnableAttempt.ExitCode != 0 || vmEnableAttempt.ExitCode != 0)
                throw new InvalidOperationException("Failed to enable WSL prerequisites.");
        }

        throw new InvalidOperationException("WSL prerequisites were enabled. Restart Windows to resume the appliance installation.");
    }

    private static WindowsApplianceInstallState StageAssets(WindowsApplianceInstallState state)
    {
        var installerRoot = Path.Combine(
            Directory.GetParent(state.InstallDir)?.FullName ?? Path.GetTempPath(),
            ".installer");
        Directory.CreateDirectory(installerRoot);

        var installerExecutablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(installerExecutablePath) || !File.Exists(installerExecutablePath))
            throw new InvalidOperationException("Unable to determine the current installer executable path.");

        var installerExecutableDestination = Path.Combine(installerRoot, Path.GetFileName(installerExecutablePath));
        if (!Path.GetFullPath(installerExecutablePath).Equals(Path.GetFullPath(installerExecutableDestination), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(installerExecutablePath, installerExecutableDestination, overwrite: true);
        }

        StageMonitor(
            installerExecutablePath,
            Path.Combine(installerRoot, ApplianceTaskPlan.MonitorFileName));

        return state with
        {
            InstallerExecutablePath = installerExecutableDestination,
        };
    }

    private static async Task EnsureDistroImportedAsync(
        WindowsApplianceInstallState state,
        bool resume,
        Func<string, Task> log,
        CancellationToken cancellationToken)
    {
        var registered = GetDistroRegistration(state.DistroName);
        if (registered is not null)
        {
            if (!resume)
                throw new InvalidOperationException("A distribution appeared during setup. Installation stopped without changing it.");
            if (!PathsEqual(registered.Value.Path, state.InstallDir)
                || (state.RegistrationId is not null && !string.Equals(registered.Value.Id, state.RegistrationId, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"A different distribution named '{state.DistroName}' is registered at another location. Resume stopped without changing it.");
            await log($"WSL appliance '{state.DistroName}' is already registered. Leaving it in place.")
                .ConfigureAwait(false);
            return;
        }

        Directory.CreateDirectory(state.InstallDir);
        await log($"Importing WSL appliance '{state.DistroName}'...").ConfigureAwait(false);
        await RunProcessAsync(
            "wsl.exe",
            ["--import", state.DistroName, state.InstallDir, state.RootfsPath, "--version", "2"],
            allowFailure: false,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task ConfigureDistroAsync(
        WindowsApplianceInstallState state,
        Func<string, Task> log,
        CancellationToken cancellationToken)
    {
        await log("Starting imported distro and verifying the appliance bootstrap layout...").ConfigureAwait(false);
        await RunWslBashAsync(
            state.DistroName,
            BootstrapLayoutCommand,
            cancellationToken).ConfigureAwait(false);
    }

    // First boot creates the current pointers. This is a layout check only;
    // WaitForWebUiReadyAsync still requires real Gateway and WebHost health.
    internal const string BootstrapLayoutCommand = """
        set -euo pipefail
        if test -d /opt/kautuka/manager/current; then
            exit 0
        fi
        test -x /usr/local/lib/kautuka/firstboot/start
        test -f /usr/local/lib/kautuka/firstboot/installer.sh
        test -f /usr/local/lib/kautuka/firstboot/release.json
        test -f /usr/local/lib/kautuka/firstboot/manifest-public-key.pem
        test -f /usr/local/lib/kautuka/firstboot/payload/bin/runtime-images.json
        test -f /etc/systemd/system/kautuka-wsl-firstboot.service
        """;

    private static void RegisterResumeRunOnce(string distroName)
    {
        var installerExecutablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(installerExecutablePath))
            throw new InvalidOperationException("Unable to determine the installer executable path for resume registration.");

        var command = $"\"{installerExecutablePath}\" --express --resume --distro-name \"{distroName}\"";
        using var hive = PlatformInstaller.OpenUserHive(writable: true);
        using var key = hive.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\RunOnce");
        key?.SetValue("KautukaWslInstallerResume", command);
    }

    private static string DeriveInstallDir(string distroName)
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kautuka",
            "WSL",
            distroName);
    }
}
