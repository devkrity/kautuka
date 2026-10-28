using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kautuka.Control.Core.Config;

public sealed record DesktopConfig
{
    [JsonPropertyName("install_dir")]
    public string InstallDir { get; init; } = string.Empty;

    [JsonPropertyName("ports")]
    public PortAssignments Ports { get; init; } = PortAssignments.Defaults();



    public Uri GatewayUrl() => new($"http://127.0.0.1:{Ports.GatewayPublic}", UriKind.Absolute);
    public Uri UiUrl() => new($"http://127.0.0.1:{Ports.UiHost}", UriKind.Absolute);
    public string WebDistPath()
    {
        // Explicit override (e.g. CI or custom install layouts)
        if (Environment.GetEnvironmentVariable("KAUTUKA_WEB_ROOT") is { Length: > 0 } root)
            return root;

        // Production install: <InstallDir>/web/dist
        var installPath = Path.Combine(InstallDir, "web", "dist");
        if (Directory.Exists(installPath))
            return installPath;

        // Dev: walk up from the assembly to find the repo's web/dist
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "web", "dist");
            if (Directory.Exists(candidate))
                return candidate;
            var parent = Path.GetDirectoryName(dir);
            if (parent is null || parent == dir) break;
            dir = parent;
        }

        return installPath; // return install path so the error message is meaningful
    }

    // ── Load/Save ──────────────────────────────────────────────────────────

    public static DesktopConfig? Load(string installDir)
    {
        var path = ConfigPath(installDir);
        if (!File.Exists(path)) return null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            return JsonSerializer.Deserialize(bytes, AppConfigJsonContext.Default.DesktopConfig);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static DesktopConfig? LoadDefault()
    {
        var dir = ResolveInstallDir();
        return dir is null ? null : Load(dir);
    }

    public void Save()
    {
        var path = ConfigPath(InstallDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(this, AppConfigJsonContext.Default.DesktopConfig);
        File.WriteAllText(path, json);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static string ConfigPath(string installDir) =>
        Path.Combine(installDir, "config", "ports.json");

    public static string? ResolveInstallDir()
    {
        if (Environment.GetEnvironmentVariable("KAUTUKA_INSTALL_DIR") is { Length: > 0 } env)
            return env;

        if (OperatingSystem.IsWindows()) return @"C:\Kautuka";
        if (OperatingSystem.IsMacOS())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Kautuka");
        // Linux
        return Path.Combine(
            Environment.GetEnvironmentVariable("XDG_DATA_HOME") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"),
            "kautuka");
    }
}
