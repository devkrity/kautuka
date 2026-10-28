namespace Kautuka.Monitor;

/// <summary>Host paths shared by the installer and <c>Kautuka.Monitor</c>.</summary>
public static class AppliancePaths
{
    public const string DefaultDistroName = "kautuka";

    public const string WebUrl = "http://localhost:20229";

    public static string InstallDir(string distroName)
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kautuka",
            "WSL",
            distroName);

    /// <summary>
    /// The staged-binary directory beside the distro folder. Supervisor state lives here with
    /// the executable that reads it.
    /// </summary>
    public static string StateRoot(string installDir)
        => Path.Combine(
            Directory.GetParent(installDir)?.FullName ?? Path.GetTempPath(),
            ".installer");
}
