using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Kautuka.Control.Core.Services;

/// <summary>
/// Manages services as child processes of the current process.
/// On Windows a Job Object with KILL_ON_JOB_CLOSE ensures all child processes
/// are terminated automatically if the parent exits unexpectedly.
/// Cross-platform — no OS service registration required.
/// </summary>
public sealed partial class SidecarServiceManager : IServiceManager, IDisposable
{
    private readonly ConcurrentDictionary<ServiceId, (Process Process, Config.PortAssignments Ports)> running = new();

    // Windows Job Object — all child processes are assigned to this job so they
    // die automatically when the parent process exits (even on crash).
    private readonly IntPtr jobHandle;

    public SidecarServiceManager()
    {
        if (OperatingSystem.IsWindows())
            jobHandle = CreateKillOnCloseJobObject();
    }

    private static bool UseProjectReferences() =>
        string.Equals(Environment.GetEnvironmentVariable("KAUTUKA_DEV_PROJECT_REFERENCES"), "1", StringComparison.Ordinal);

    private static string ResolveRepoRoot()
    {
        if (Environment.GetEnvironmentVariable("KAUTUKA_REPO_ROOT") is { Length: > 0 } envRoot)
            return envRoot;

        var dir = Directory.GetCurrentDirectory();
        for (var i = 0; i < 8; i++)
        {
            if (File.Exists(Path.Combine(dir, "Kautuka.slnx")))
                return dir;

            var parent = Directory.GetParent(dir);
            if (parent is null)
                break;
            dir = parent.FullName;
        }

        return Directory.GetCurrentDirectory();
    }

    public Result Install(ServiceId id, string installDir, Config.PortAssignments ports)
    {
        var exe = id.ExePath(installDir);
        return File.Exists(exe) ? Result.Ok() : Result.Fail($"Binary not found: {exe}");
    }

    public Result Uninstall(ServiceId id, string installDir) => Result.Ok();

    public Result Start(ServiceId id, string installDir, Config.PortAssignments ports)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };

        if (UseProjectReferences())
        {
            var repoRoot = ResolveRepoRoot();
            var projectPath = Path.Combine(repoRoot, id.ProjectPath());
            if (!File.Exists(projectPath))
                return Result.Fail($"Project file not found at '{projectPath}'.");

            psi.FileName = "dotnet";
            psi.WorkingDirectory = repoRoot;
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add("--no-build");
            psi.ArgumentList.Add("--project");
            psi.ArgumentList.Add(projectPath);
            psi.ArgumentList.Add("--");
            foreach (var arg in id.StartArguments(ports))
                psi.ArgumentList.Add(arg);
        }
        else
        {
            var exe = id.ExePath(installDir);
            if (!File.Exists(exe))
                return Result.Fail($"Binary not found at '{exe}'. Publish the service first.");

            psi.FileName = exe;
            psi.WorkingDirectory = installDir;
            foreach (var arg in id.StartArguments(ports))
                psi.ArgumentList.Add(arg);
        }

        try
        {
            var proc = Process.Start(psi)
                       ?? throw new InvalidOperationException("Process.Start returned null");

            // Assign to the Job Object so the child is killed if the parent dies.
            if (OperatingSystem.IsWindows() && jobHandle != IntPtr.Zero)
                AssignProcessToJobObject(jobHandle, proc.Handle);

            // Catch immediate startup failures (bad args/config/missing dependencies).
            if (proc.WaitForExit(1200))
            {
                var exitCode = proc.ExitCode;
                proc.Dispose();
                return Result.Fail($"{id.DisplayName()} exited immediately with code {exitCode}.");
            }

            running[id] = (proc, ports);
            return Result.Ok();
        }
        catch (InvalidOperationException ex) { return Result.Fail($"Failed to start {id.DisplayName()}: {ex.Message}"); }
        catch (System.ComponentModel.Win32Exception ex) { return Result.Fail($"Failed to start {id.DisplayName()}: {ex.Message}"); }
    }

    public Result StopService(ServiceId id, string installDir)
    {
        if (!running.TryRemove(id, out var entry))
            return Result.Ok(); // already stopped

        var (proc, ports) = entry;
        try
        {
            if (!proc.HasExited)
            {
                // Attempt graceful shutdown for services that expose an HTTP management port.
                TryGracefulShutdown(id);
                // Wait up to 6 s for graceful exit before hard-killing.
                if (!proc.WaitForExit(6000))
                    proc.Kill(entireProcessTree: true);
                // Wait for the kill to complete so no orphan handles remain.
                proc.WaitForExit(5000);
            }
            proc.Dispose();
            return Result.Ok();
        }
        catch (InvalidOperationException ex) { return Result.Fail($"Kill failed: {ex.Message}"); }
        catch (System.ComponentModel.Win32Exception ex) { return Result.Fail($"Kill failed: {ex.Message}"); }
        catch (NotSupportedException ex) { return Result.Fail($"Kill failed: {ex.Message}"); }
    }

    public Result Restart(ServiceId id, string installDir, Config.PortAssignments ports)
    {
        var stop = StopService(id, installDir);
        if (!stop.IsOk) return stop;
        return Start(id, installDir, ports);
    }

    public void Dispose()
    {
        foreach (var (id, (proc, ports)) in running)
        {
            try
            {
                if (!proc.HasExited)
                {
                    TryGracefulShutdown(id);
                    if (!proc.WaitForExit(6000))
                        proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(5000);
                }
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            catch (NotSupportedException) { }
            proc.Dispose();
        }
        running.Clear();

        if (OperatingSystem.IsWindows() && jobHandle != IntPtr.Zero)
            CloseHandle(jobHandle);
    }

    // ── Graceful shutdown ────────────────────────────────────────────────────

    /// <summary>
    /// Posts a shutdown request to services that expose an HTTP management endpoint.
    /// Fire-and-forget — the caller waits on WaitForExit, not on this method.
    /// </summary>
    private static void TryGracefulShutdown(ServiceId id)
    {
        // Only the Gateway exposes an internal HTTP management endpoint.
        if (id != ServiceId.Gateway) return;

        // Gateway internal loopback port is always 8099 (hard-coded in StartArguments).
        var url = new Uri("http://127.0.0.1:8099/api/internal/shutdown", UriKind.Absolute);

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            using var _ = http.PostAsync(url, null).GetAwaiter().GetResult();
        }
        catch (HttpRequestException)
        {
        }
        catch (TaskCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
            // Graceful attempt failed (process may be unresponsive) — fall back to kill.
        }
    }

    // ── Windows Job Object ───────────────────────────────────────────────────

    [SupportedOSPlatform("windows")]
    private static IntPtr CreateKillOnCloseJobObject()
    {
        var hJob = CreateJobObject(IntPtr.Zero, null);
        if (hJob == IntPtr.Zero) return IntPtr.Zero;

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = jobObjectLimitKillOnJobClose,
            }
        };

        uint size = (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        if (!SetInformationJobObject(hJob, jobObjectExtendedLimitInformation, ref info, size))
        {
            CloseHandle(hJob);
            return IntPtr.Zero;
        }

        return hJob;
    }

    private const uint jobObjectLimitKillOnJobClose = 0x00002000;
    private const int jobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", StringMarshalling = StringMarshalling.Utf16)]
    [SupportedOSPlatform("windows")]
    private static partial IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll")]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(IntPtr hJob, int infoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, uint cbJobObjectInfoLength);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll")]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll")]
    [SupportedOSPlatform("windows")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr hObject);
}

