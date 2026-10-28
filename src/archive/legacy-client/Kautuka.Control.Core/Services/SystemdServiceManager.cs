using System.Diagnostics;

namespace Kautuka.Control.Core.Services;

/// <summary>
/// Linux service manager — systemd user units or system units.
/// User unit:   ~/.config/systemd/user/kautuka-{name}.service
/// System unit: /etc/systemd/system/kautuka-{name}.service
/// </summary>
public sealed class SystemdServiceManager : IServiceManager
{
    private readonly bool userMode;

    public SystemdServiceManager(bool userMode) => this.userMode = userMode;

    private static string UnitName(ServiceId id) => $"kautuka-{id.FolderName()}.service";
    private string UnitPath(ServiceId id)
    {
        var name = UnitName(id);
        if (userMode)
        {
            var configDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(configDir, "systemd", "user", name);
        }
        return Path.Combine("/etc/systemd/system", name);
    }

    private Result Systemctl(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo("systemctl")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
            };
            if (userMode) psi.ArgumentList.Add("--user");
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi)!;
            proc.WaitForExit();
            if (proc.ExitCode == 0) return Result.Ok();
            var err = proc.StandardError.ReadToEnd();
            return Result.Fail($"systemctl {string.Join(' ', args)} failed: {err}");
        }
        catch (InvalidOperationException ex) { return Result.Fail($"systemctl error: {ex.Message}"); }
        catch (System.ComponentModel.Win32Exception ex) { return Result.Fail($"systemctl error: {ex.Message}"); }
    }

    private Result WriteUnit(ServiceId id, string installDir, Config.PortAssignments ports)
    {
        var exe = id.ExePath(installDir);
        var envLines = string.Concat(
            id.EnvVars(ports).Select(kv => $"Environment=\"{kv.Key}={kv.Value}\"\n"));
        var content =
            $"[Unit]\nDescription={id.DisplayName()}\nAfter=network.target\n\n" +
            $"[Service]\nType=simple\nExecStart={exe}\n{envLines}" +
            $"Restart=on-failure\nRestartSec=5\n\n" +
            $"[Install]\nWantedBy=default.target\n";

        var path = UnitPath(id);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return Result.Ok();
        }
        catch (IOException ex) { return Result.Fail($"Cannot write unit file: {ex.Message}"); }
        catch (UnauthorizedAccessException ex) { return Result.Fail($"Cannot write unit file: {ex.Message}"); }
    }

    public Result Install(ServiceId id, string installDir, Config.PortAssignments ports)
    {
        var r = WriteUnit(id, installDir, ports);
        if (!r.IsOk) return r;
        r = Systemctl("daemon-reload");
        if (!r.IsOk) return r;
        return Systemctl("enable", UnitName(id));
    }

    public Result Uninstall(ServiceId id, string installDir)
    {
        Systemctl("disable", "--now", UnitName(id)); // best-effort
        var path = UnitPath(id);
        if (File.Exists(path))
        {
            try { File.Delete(path); }
            catch (IOException ex) { return Result.Fail($"Cannot remove unit: {ex.Message}"); }
            catch (UnauthorizedAccessException ex) { return Result.Fail($"Cannot remove unit: {ex.Message}"); }
        }
        return Systemctl("daemon-reload");
    }

    public Result Start(ServiceId id, string installDir, Config.PortAssignments ports) => Systemctl("start", UnitName(id));
    public Result StopService(ServiceId id, string installDir) => Systemctl("stop", UnitName(id));
    public Result Restart(ServiceId id, string installDir, Config.PortAssignments ports) => Systemctl("restart", UnitName(id));
}
