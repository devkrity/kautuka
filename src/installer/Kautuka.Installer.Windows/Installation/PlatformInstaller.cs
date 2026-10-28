namespace Kautuka.Installer.Windows.Installation;

/// <summary>
/// Platform-specific registration/unregistration (uninstall records, desktop entries).
/// </summary>
internal static class PlatformInstaller
{
    public static void RegisterWindowsApplianceUninstall(
        string installerExecutablePath,
        string releaseId,
        string distroName,
        string installDir)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            var uninstallCommand = $"\"{installerExecutablePath}\" --uninstall --distro-name \"{distroName}\" --install-dir \"{installDir}\"";

            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KautukaWSL");
            if (key is null) return;
            key.SetValue("DisplayName", "Kautuka WSL Appliance");
            key.SetValue("DisplayIcon", installerExecutablePath);
            key.SetValue("UninstallString", uninstallCommand);
            key.SetValue("QuietUninstallString", uninstallCommand);
            key.SetValue("InstallLocation", installDir);
            key.SetValue("Publisher", "Kautuka");
            key.SetValue("DisplayVersion", releaseId);
            key.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
        }
        catch (ArgumentException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static void UnregisterWindowsAppliance()
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KautukaWSL",
                throwOnMissingSubKey: false);
        }
        catch (ArgumentException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
