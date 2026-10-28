using System.Runtime.Versioning;

using Microsoft.Win32;

namespace Kautuka.Control.Core.Install;

/// <summary>
/// Manages the Windows Add/Remove Programs (ARP) registry entry for Kautuka.
/// Only callable on Windows — guard all call sites with <see cref="OperatingSystem.IsWindows()"/>.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AddRemoveProgramsEntry
{
    private const string uninstallKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Kautuka";

    /// <summary>
    /// Creates or updates the ARP entry so Kautuka appears in Settings → Apps.
    /// </summary>
    public static void Register(
        string displayName,
        string version,
        string installDir,
        string uninstallExePath)
    {
        using var key = Registry.LocalMachine.CreateSubKey(uninstallKeyPath, writable: true);

        key.SetValue("DisplayName", displayName);
        key.SetValue("DisplayVersion", version);
        key.SetValue("Publisher", "Kautuka");
        key.SetValue("InstallLocation", installDir);
        key.SetValue("UninstallString", $"\"{uninstallExePath}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{uninstallExePath}\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        key.SetValue("EstimatedSize", EstimateSizeKb(installDir), RegistryValueKind.DWord);
    }

    /// <summary>Removes the ARP registry entry.</summary>
    public static void Remove()
    {
        Registry.LocalMachine.DeleteSubKeyTree(uninstallKeyPath, throwOnMissingSubKey: false);
    }

    private static int EstimateSizeKb(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        try
        {
            var bytes = new DirectoryInfo(dir)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .Sum(f => f.Length);
            return (int)(bytes / 1024);
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
