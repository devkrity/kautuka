using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace Kautuka.Installer.Windows.Installation;

internal static partial class WindowsApplianceInstaller
{
    private const string embeddedMonitorResource = "Kautuka.Installer.Windows.Kautuka.Monitor.exe";

    private const string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static string StagedMonitorPath(string installerExecutablePath)
        => Path.Combine(
            Path.GetDirectoryName(installerExecutablePath)
                ?? throw new InvalidOperationException("The staged installer path has no directory."),
            ApplianceTaskPlan.MonitorFileName);

    private static bool TryExtractEmbeddedMonitor(string destination)
    {
        using Stream? stream = typeof(WindowsApplianceInstaller).Assembly
            .GetManifestResourceStream(embeddedMonitorResource);
        if (stream is null)
            return false;

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using FileStream file = File.Create(destination);
        stream.CopyTo(file);
        return true;
    }

    private static void StageMonitor(string installerExecutablePath, string destination)
    {
        if (TryExtractEmbeddedMonitor(destination))
            return;

        string sibling = Path.Combine(
            Path.GetDirectoryName(installerExecutablePath)
                ?? throw new InvalidOperationException("The installer path has no directory."),
            ApplianceTaskPlan.MonitorFileName);

        if (string.Equals(Path.GetFullPath(sibling), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(destination))
                throw new InvalidOperationException(
                    $"{ApplianceTaskPlan.MonitorFileName} is not embedded in this installer and is not beside it.");
            return;
        }

        if (!File.Exists(sibling))
            throw new InvalidOperationException(
                $"{ApplianceTaskPlan.MonitorFileName} is not embedded in this installer and is not beside it.");

        File.Copy(sibling, destination, overwrite: true);
    }

    /// <summary>
    /// Registers the unelevated residency task, the logon tray, and the desktop shortcut.
    /// A failure here leaves the appliance installed and is logged. Windows will not start it after reboot until registration succeeds.
    /// </summary>
    private static async Task<bool> TryRegisterMonitorAsync(
        WindowsApplianceInstallState state,
        Func<string, Task> log,
        CancellationToken cancellationToken)
    {
        string monitorPath = StagedMonitorPath(state.InstallerExecutablePath);
        if (!File.Exists(monitorPath))
        {
            await log($"{ApplianceTaskPlan.MonitorFileName} was not staged. Windows will not start the appliance after reboot until the monitor is registered.")
                .ConfigureAwait(false);
            return false;
        }

        try
        {
            string userId = WindowsIdentity.GetCurrent().Name;
            ApplianceTaskPlan plan = ApplianceTaskPlan.Create(monitorPath, state.DistroName, state.InstallDir, userId);
            await RunPowerShellScriptAsync(plan.ToRegistrationScript(), cancellationToken).ConfigureAwait(false);
            RegisterMonitorRunKey(monitorPath, plan.TrayArguments);

            string shortcutPath = DesktopFile("Kautuka.lnk");
            await RunPowerShellScriptAsync(plan.ToShortcutScript(shortcutPath), cancellationToken).ConfigureAwait(false);
            string urlShortcut = DesktopFile("Kautuka.url");
            if (File.Exists(urlShortcut))
                File.Delete(urlShortcut);

            LaunchMonitorUnelevated(shortcutPath);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            await log($"Kautuka Monitor was not registered: {ex.Message}").ConfigureAwait(false);
            return false;
        }
        catch (IOException ex)
        {
            await log($"Kautuka Monitor was not registered: {ex.Message}").ConfigureAwait(false);
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            await log($"Kautuka Monitor was not registered: {ex.Message}").ConfigureAwait(false);
            return false;
        }
    }

    private static async Task RemoveMonitorResidencyAsync(string distroName, CancellationToken cancellationToken)
    {
        await RunPowerShellScriptAsync(ApplianceTaskPlan.ToRemovalScript(), cancellationToken, allowFailure: true)
            .ConfigureAwait(false);
        RemoveMonitorRunKey();
        StopMonitorProcesses();
        await RunProcessAsync(
            "wsl.exe",
            ["--terminate", distroName],
            allowFailure: true,
            cancellationToken).ConfigureAwait(false);
    }

    private static void RegisterMonitorRunKey(string monitorExecutable, string arguments)
    {
        using Microsoft.Win32.RegistryKey key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(runKeyPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the current user's Run key.");
        key.SetValue(ApplianceTaskPlan.RunValueName, $"\"{monitorExecutable}\" {arguments}");
    }

    private static void RemoveMonitorRunKey()
    {
        using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(runKeyPath, writable: true);
        key?.DeleteValue(ApplianceTaskPlan.RunValueName, throwOnMissingValue: false);
    }

    /// <summary>
    /// <c>explorer.exe</c> starts the tray outside this elevated token. A direct child of the
    /// installer would inherit <c>requireAdministrator</c> and prompt.
    /// </summary>
    private static void LaunchMonitorUnelevated(string shortcutPath)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{shortcutPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }

    private static void StopMonitorProcesses()
    {
        foreach (Process process in Process.GetProcessesByName("Kautuka.Monitor"))
        {
            try
            {
                process.Kill();
                process.WaitForExit(TimeSpan.FromSeconds(5));
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static string DesktopFile(string fileName)
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), fileName);

    private static Task<ProcessResult> RunPowerShellScriptAsync(
        string script,
        CancellationToken cancellationToken,
        bool allowFailure = false)
    {
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return RunProcessAsync(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-EncodedCommand", encoded],
            allowFailure,
            cancellationToken);
    }
}
