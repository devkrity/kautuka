using System.Diagnostics;

namespace Kautuka.Control.Core.Services;

/// <summary>
/// macOS service manager — launchd LaunchAgent or LaunchDaemon.
/// Agent:  ~/Library/LaunchAgents/com.kautuka.{name}.plist
/// Daemon: /Library/LaunchDaemons/com.kautuka.{name}.plist
/// </summary>
public sealed class LaunchdServiceManager : IServiceManager
{
    private readonly bool agentMode; // true = LaunchAgent, false = LaunchDaemon

    public LaunchdServiceManager(bool agentMode) => this.agentMode = agentMode;

    private static string Label(ServiceId id) => $"com.kautuka.{id.FolderName()}";
    private string PlistPath(ServiceId id)
    {
        var label = LaunchdServiceManager.Label(id);
        if (agentMode)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, "Library", "LaunchAgents", $"{label}.plist");
        }
        return Path.Combine("/Library/LaunchDaemons", $"{label}.plist");
    }

    private static Result Launchctl(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("launchctl")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi)!;
            proc.WaitForExit();
            if (proc.ExitCode == 0) return Result.Ok();
            var err = proc.StandardError.ReadToEnd();
            return Result.Fail($"launchctl {string.Join(' ', args)} failed: {err}");
        }
        catch (InvalidOperationException ex) { return Result.Fail($"launchctl error: {ex.Message}"); }
        catch (System.ComponentModel.Win32Exception ex) { return Result.Fail($"launchctl error: {ex.Message}"); }
    }

    private Result WritePlist(ServiceId id, string installDir, Config.PortAssignments ports)
    {
        var exe = id.ExePath(installDir);
        var label = LaunchdServiceManager.Label(id);
        var folder = id.FolderName();
        var envEntries = string.Concat(id.EnvVars(ports)
            .Select(kv => $"\t\t<key>{kv.Key}</key>\n\t\t<string>{kv.Value}</string>\n"));

        var content =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" " +
            "\"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n" +
            "<plist version=\"1.0\">\n<dict>\n" +
            $"\t<key>Label</key><string>{label}</string>\n" +
            $"\t<key>ProgramArguments</key><array><string>{exe}</string></array>\n" +
            $"\t<key>EnvironmentVariables</key>\n\t<dict>\n{envEntries}\t</dict>\n" +
            "\t<key>RunAtLoad</key><true/>\n" +
            "\t<key>KeepAlive</key><true/>\n" +
            $"\t<key>StandardErrorPath</key><string>/tmp/kautuka-{folder}.err</string>\n" +
            $"\t<key>StandardOutPath</key><string>/tmp/kautuka-{folder}.out</string>\n" +
            "</dict>\n</plist>\n";

        var path = PlistPath(id);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return Result.Ok();
        }
        catch (IOException ex) { return Result.Fail($"Cannot write plist: {ex.Message}"); }
        catch (UnauthorizedAccessException ex) { return Result.Fail($"Cannot write plist: {ex.Message}"); }
    }

    public Result Install(ServiceId id, string installDir, Config.PortAssignments ports)
    {
        var r = WritePlist(id, installDir, ports);
        if (!r.IsOk) return r;
        return Launchctl("load", "-w", PlistPath(id));
    }

    public Result Uninstall(ServiceId id, string installDir)
    {
        var path = PlistPath(id);
        Launchctl("unload", "-w", path); // best-effort
        if (File.Exists(path))
        {
            try { File.Delete(path); }
            catch (IOException ex) { return Result.Fail($"Cannot remove plist: {ex.Message}"); }
            catch (UnauthorizedAccessException ex) { return Result.Fail($"Cannot remove plist: {ex.Message}"); }
        }
        return Result.Ok();
    }

    public Result Start(ServiceId id, string installDir, Config.PortAssignments ports) => Launchctl("start", Label(id));
    public Result StopService(ServiceId id, string installDir) => Launchctl("stop", Label(id));
    public Result Restart(ServiceId id, string installDir, Config.PortAssignments ports)
    {
        Launchctl("stop", Label(id)); // best-effort
        return Launchctl("start", Label(id));
    }
}
