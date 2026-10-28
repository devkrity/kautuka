namespace Kautuka.Installer.Windows.Installation;

internal static partial class WindowsApplianceInstaller
{
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
        RegisterResumeRunOnce();

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
        Func<string, Task> log,
        CancellationToken cancellationToken)
    {
        if (await DistroIsRegisteredAsync(state.DistroName, cancellationToken).ConfigureAwait(false))
        {
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

    /// <summary>
    /// Redirected <c>wsl.exe</c> text on this host is UTF-16 interpreted as another code page,
    /// so the error code arrives with nulls between the letters.
    /// </summary>
    private static async Task<bool> DistroIsRegisteredAsync(string distroName, CancellationToken cancellationToken)
    {
        ProcessResult probe = await RunProcessAsync(
            "wsl.exe",
            ["-d", distroName, "-e", "/bin/true"],
            allowFailure: true,
            cancellationToken).ConfigureAwait(false);
        if (probe.ExitCode == 0)
            return true;

        string detail = (probe.StandardOutput + probe.StandardError).Replace("\0", "", StringComparison.Ordinal);
        if (detail.Contains("WSL_E_DISTRO_NOT_FOUND", StringComparison.Ordinal))
            return false;

        throw new InvalidOperationException(
            $"Could not tell whether WSL distro '{distroName}' is registered (exit {probe.ExitCode}). {detail}".Trim());
    }

    private static async Task ConfigureDistroAsync(
        WindowsApplianceInstallState state,
        Func<string, Task> log,
        CancellationToken cancellationToken)
    {
        await log("Starting imported distro and verifying the prewarmed appliance image...").ConfigureAwait(false);
        await RunWslBashAsync(
            state.DistroName,
            "set -euo pipefail; test -d /opt/kautuka/manager/current; systemctl is-system-running --wait >/dev/null 2>&1 || true",
            cancellationToken).ConfigureAwait(false);
    }

    private static void RegisterResumeRunOnce()
    {
        var installerExecutablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(installerExecutablePath))
            throw new InvalidOperationException("Unable to determine the installer executable path for resume registration.");

        var command = $"\"{installerExecutablePath}\" --express --resume";
        using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(
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
