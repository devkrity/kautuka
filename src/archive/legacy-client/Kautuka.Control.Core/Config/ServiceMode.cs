namespace Kautuka.Control.Core.Config;

/// <summary>How Kautuka services are managed on this machine.</summary>
public enum ServiceMode
{
    /// <summary>Windows Service via NSSM — always-on (Windows only).</summary>
    Nssm,
    /// <summary>systemd user unit — starts on login (Linux only).</summary>
    SystemdUser,
    /// <summary>systemd system unit — always-on (Linux only).</summary>
    SystemdSystem,
    /// <summary>launchd LaunchAgent — starts on login (macOS only).</summary>
    LaunchdAgent,
    /// <summary>launchd LaunchDaemon — always-on (macOS only).</summary>
    LaunchdDaemon,
    /// <summary>Child processes of the control app — cross-platform, no admin required.</summary>
    Sidecar,
}

public static class ServiceModeExtensions
{
    public static bool IsSystemService(this ServiceMode mode) =>
        mode is ServiceMode.Nssm or ServiceMode.SystemdSystem or ServiceMode.LaunchdDaemon;

    public static string Label(this ServiceMode mode) => mode switch
    {
        ServiceMode.Nssm => "System service (Windows NSSM) &mdash; requires NSSM binary",
        ServiceMode.SystemdUser => "Start on login (systemd user)",
        ServiceMode.SystemdSystem => "System service (systemd)",
        ServiceMode.LaunchdAgent => "Start on login (launchd)",
        ServiceMode.LaunchdDaemon => "System service (launchd)",
        ServiceMode.Sidecar => "On demand (sidecar) &mdash; recommended",
        _ => mode.ToString(),
    };

    /// <summary>Returns the two canonical options for the current platform.</summary>
    public static (ServiceMode Primary, ServiceMode Secondary) PlatformDefaults()
    {
        if (OperatingSystem.IsWindows()) return (ServiceMode.Sidecar, ServiceMode.Nssm);
        if (OperatingSystem.IsLinux()) return (ServiceMode.SystemdSystem, ServiceMode.SystemdUser);
        if (OperatingSystem.IsMacOS()) return (ServiceMode.LaunchdDaemon, ServiceMode.LaunchdAgent);
        return (ServiceMode.Sidecar, ServiceMode.Sidecar);
    }
}
