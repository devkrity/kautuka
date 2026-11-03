namespace Kautuka.Installer.Windows.Installation;

/// <summary>
/// Platform-specific registration/unregistration (uninstall records, desktop entries).
/// </summary>
internal static class PlatformInstaller
{
    internal static Microsoft.Win32.RegistryKey OpenUserHive(bool writable)
    {
        string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value
            ?? throw new InvalidOperationException("Could not identify the Windows user.");
        return Microsoft.Win32.Registry.Users.OpenSubKey(sid, writable)
            ?? throw new InvalidOperationException("Could not open the Windows user's registry hive.");
    }

    public static void RegisterWindowsApplianceUninstall(
        string installerExecutablePath,
        string releaseId,
        string distroName,
        string installDir)
    {
        if (!OperatingSystem.IsWindows()) return;

        var uninstallCommand = $"\"{installerExecutablePath}\" --uninstall --distro-name \"{distroName}\" --install-dir \"{installDir}\"";

        using var hive = OpenUserHive(writable: true);
        using var key = hive.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KautukaWSL");
        if (key is null) throw new InvalidOperationException("Could not register Kautuka in Windows installed apps.");
        key.SetValue("DisplayName", "Kautuka WSL Appliance");
        key.SetValue("DisplayIcon", installerExecutablePath);
        key.SetValue("UninstallString", uninstallCommand);
        key.DeleteValue("QuietUninstallString", throwOnMissingValue: false);
        key.SetValue("InstallLocation", installDir);
        key.SetValue("DistroName", distroName);
        key.SetValue("Publisher", "Kautuka");
        key.SetValue("DisplayVersion", releaseId);
        key.SetValue("NoModify", 1, Microsoft.Win32.RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, Microsoft.Win32.RegistryValueKind.DWord);
        if (!string.Equals(key.GetValue("UninstallString") as string, uninstallCommand, StringComparison.Ordinal))
            throw new InvalidOperationException("Could not verify the Windows uninstall entry.");
    }

    public static void UnregisterWindowsAppliance()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var hive = OpenUserHive(writable: true);
        hive.DeleteSubKeyTree(
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\KautukaWSL",
            throwOnMissingSubKey: false);
        using var remaining = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\KautukaWSL");
        if (remaining is not null) throw new InvalidOperationException("Could not remove the Windows uninstall entry.");
    }
}
