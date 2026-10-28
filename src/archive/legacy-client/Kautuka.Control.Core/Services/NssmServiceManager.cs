using System.Diagnostics;

namespace Kautuka.Control.Core.Services;

/// <summary>
/// Windows service manager via NSSM (Non-Sucking Service Manager).
/// Wraps nssm.exe which must be present at {installDir}/tools/nssm.exe.
/// </summary>
public sealed class NssmServiceManager : IServiceManager
{
    private static string NssmPath(string installDir) =>
        Path.Combine(installDir, "tools", "nssm.exe");

    private static Result RunNssm(string installDir, params string[] args)
    {
        var nssm = NssmPath(installDir);
        if (!File.Exists(nssm))
            return Result.Fail($"nssm.exe not found at '{nssm}'");

        try
        {
            var psi = new ProcessStartInfo(nssm)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi)!;
            proc.WaitForExit();
            if (proc.ExitCode == 0) return Result.Ok();
            var err = proc.StandardError.ReadToEnd();
            var out_ = proc.StandardOutput.ReadToEnd();
            return Result.Fail($"nssm {args[0]} failed: {out_} {err}".Trim());
        }
        catch (InvalidOperationException ex) { return Result.Fail($"nssm error: {ex.Message}"); }
        catch (System.ComponentModel.Win32Exception ex) { return Result.Fail($"nssm error: {ex.Message}"); }
    }

    public Result Install(ServiceId id, string installDir, Config.PortAssignments ports)
    {
        var exe = id.ExePath(installDir);
        var svc = id.ServiceName();

        var steps = new string[][]
        {
            ["install", svc, exe],
            ["set", svc, "DisplayName", id.DisplayName()],
            ["set", svc, "AppDirectory", Path.GetDirectoryName(exe)!],
            ["set", svc, "AppExit", "Default", "Restart"],
            ["set", svc, "Start", "SERVICE_AUTO_START"],
        };

        foreach (var step in steps)
        {
            var r = RunNssm(installDir, step);
            if (!r.IsOk) return r;
        }

        // Environment variables
        var envStr = string.Join("\n", id.EnvVars(ports)
            .Select(kv => $"{kv.Key}={kv.Value}"));
        if (!string.IsNullOrEmpty(envStr))
        {
            var r = RunNssm(installDir, "set", svc, "AppEnvironmentExtra", envStr);
            if (!r.IsOk) return r;
        }

        // Dependencies
        var deps = string.Join(" ", id.DependsOn().Select(d => d.ServiceName()));
        if (!string.IsNullOrEmpty(deps))
        {
            var r = RunNssm(installDir, "set", svc, "DependOnService", deps);
            if (!r.IsOk) return r;
        }

        return Result.Ok();
    }

    public Result Uninstall(ServiceId id, string installDir)
    {
        RunNssm(installDir, "stop", id.ServiceName()); // best-effort
        return RunNssm(installDir, "remove", id.ServiceName(), "confirm");
    }

    public Result Start(ServiceId id, string installDir, Config.PortAssignments ports) =>
        RunNssm(installDir, "start", id.ServiceName());

    public Result StopService(ServiceId id, string installDir) =>
        RunNssm(installDir, "stop", id.ServiceName());

    public Result Restart(ServiceId id, string installDir, Config.PortAssignments ports) =>
        RunNssm(installDir, "restart", id.ServiceName());
}
