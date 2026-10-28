using System.Runtime.InteropServices;

using Kautuka.Control.Core.Config;
using Kautuka.Control.Core.Services;
using Kautuka.Telemetry;

namespace Kautuka.Control.Desktop;

internal static class Program
{
    private static readonly Action<ILogger, Exception?> logMissingConfigDefaults = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(100, nameof(logMissingConfigDefaults)),
        "No config found - using defaults (gateway :8000, ui :8001). Pass --gateway <url> or --ui-port <port> to override.");
    private static readonly Action<ILogger, string, Exception?> logMissingWebDist = LoggerMessage.Define<string>(
        LogLevel.Error,
        new EventId(101, nameof(logMissingWebDist)),
        "Web dist not found at '{Path}'. Run 'npm run build:web' first.");
    private static readonly Action<ILogger, Exception?> logNoSidecars = LoggerMessage.Define(
        LogLevel.Information,
        new EventId(102, nameof(logNoSidecars)),
        "Desktop running with --no-sidecars; using externally managed backend services.");
    private static readonly Action<ILogger, Exception?> logServerReadyTimeout = LoggerMessage.Define(
        LogLevel.Warning,
        new EventId(103, nameof(logServerReadyTimeout)),
        "Server did not signal ready in time - opening window anyway.");
    private static readonly Action<ILogger, string, Exception?> logHeadlessUrl = LoggerMessage.Define<string>(
        LogLevel.Information,
        new EventId(104, nameof(logHeadlessUrl)),
        "Running in headless mode. Server at {Url}");
    private static readonly Action<ILogger, Exception?> logBackendUnavailable = LoggerMessage.Define(
        LogLevel.Error,
        new EventId(105, nameof(logBackendUnavailable)),
        "One or more backend services failed to start. The UI will report service unavailability.");
    private static readonly Action<ILogger, string, string, Exception?> logSidecarStartFailed = LoggerMessage.Define<string, string>(
        LogLevel.Error,
        new EventId(106, nameof(logSidecarStartFailed)),
        "Failed to start {Service}: {Error}");
    private static readonly Action<ILogger, string, Exception?> logSidecarStarted = LoggerMessage.Define<string>(
        LogLevel.Information,
        new EventId(107, nameof(logSidecarStarted)),
        "Started {Service} as background sidecar.");
    private static readonly Action<ILogger, string, string, Exception?> logSidecarStopFailed = LoggerMessage.Define<string, string>(
        LogLevel.Warning,
        new EventId(108, nameof(logSidecarStopFailed)),
        "Failed to stop {Service}: {Error}");

    [STAThread]
    internal static void Main(string[] args)
    {
        HideConsoleWindowIfPresent();

        var showTray = !args.Contains("--no-tray");
        var headless = args.Contains("--headless") || DetectHeadless();
        var noSidecars = args.Contains("--no-sidecars");
        var installOverride = GetArg(args, "--install-dir");
        var gatewayOverride = GetArg(args, "--gateway");
        var uiPortOverride = GetArg(args, "--ui-port");

        using var logFactory = LoggerFactory.Create(builder => builder.AddKautukaLogging(LogLevel.Information, "kautuka-desktop"));
        var log = logFactory.CreateLogger("kautuka-desktop");

        var installDir = !string.IsNullOrWhiteSpace(installOverride)
            ? installOverride
            : DesktopConfig.ResolveInstallDir();
        var cfg = installDir is not null
            ? DesktopConfig.Load(installDir)
            : null;

        // Fall back to sensible defaults if no config exists (dev / first-run scenario)
        if (cfg is null)
        {
            logMissingConfigDefaults(log, null);
            installDir ??= AppContext.BaseDirectory;
            cfg = new DesktopConfig
            {
                InstallDir = installDir,
                Ports = new Kautuka.Control.Core.Config.PortAssignments
                {
                    GatewayPublic = 8000,
                    UiHost = 8001,
                    ChannelHost = 8098,
                    WorkspaceGrpc = 8100,
                },
            };
        }

        // CLI overrides applied after config is resolved
        if (gatewayOverride is not null || uiPortOverride is not null)
        {
            var gwPort = gatewayOverride is not null && Uri.TryCreate(gatewayOverride, UriKind.Absolute, out var gwUri)
                ? (ushort)gwUri.Port
                : cfg.Ports.GatewayPublic;
            var uiPort = uiPortOverride is not null && ushort.TryParse(uiPortOverride, out var p)
                ? p
                : cfg.Ports.UiHost;
            cfg = cfg with { Ports = cfg.Ports with { GatewayPublic = gwPort, UiHost = uiPort } };
        }

        var webDist = cfg.WebDistPath();
        if (!Directory.Exists(webDist))
        {
            logMissingWebDist(log, webDist, null);
            Environment.Exit(1);
            return;
        }

        using var sidecar = new SidecarServiceManager();
        var sidecarsStarted = false;
        Task sidecarStartupTask = Task.CompletedTask;
        if (!noSidecars)
        {
            sidecarsStarted = true; // always attempt cleanup on exit
            // Fire sidecar startup in background so the window opens immediately.
            // The UI shows a "waking up" screen while polling gateway health.
            sidecarStartupTask = Task.Run(() =>
            {
                if (!StartBackendSidecars(sidecar, cfg, log))
                    logBackendUnavailable(log, null);
            });
        }
        else
        {
            logNoSidecars(log, null);
        }

        // Always start Kestrel — single UI entry point in all environments
        using var serverReady = new ManualResetEventSlim(false);
        using var cts = new CancellationTokenSource();

        var serverThread = new Thread(() =>
        {
            DesktopServer.Run(cfg, webDist, serverReady, cts.Token);
        })
        {
            IsBackground = true,
            Name = "kestrel-server",
        };
        serverThread.Start();

        // Wait for server to bind (max 5 s)
        if (!serverReady.Wait(TimeSpan.FromSeconds(5)))
            logServerReadyTimeout(log, null);

        if (headless)
        {
            logHeadlessUrl(log, cfg.UiUrl().AbsoluteUri, null);
            cts.Token.WaitHandle.WaitOne();
            // Wait for sidecar startup task so processes are registered before we stop them.
            sidecarStartupTask.Wait(TimeSpan.FromSeconds(30));
            if (sidecarsStarted)
            {
                StopBackendSidecars(sidecar, cfg, log);
            }
            return;
        }

        PhotinoHost.Run(cfg, showTray, log, onExit: () => cts.Cancel());

        // Ensure sidecar startup task has finished before stopping (processes must be registered to be killed).
        sidecarStartupTask.Wait(TimeSpan.FromSeconds(30));
        // Ensure server thread unwinds before stopping child processes.
        serverThread.Join(TimeSpan.FromSeconds(5));
        if (sidecarsStarted)
        {
            StopBackendSidecars(sidecar, cfg, log);
        }
    }

    private static bool StartBackendSidecars(SidecarServiceManager manager, DesktopConfig cfg, ILogger log)
    {
        // Start all services in parallel — each has its own 1.2 s crash-detection window,
        // but they run concurrently so total wait is ~1.2 s regardless of service count.
        var ids = ServiceIdExtensions.BackendServices().ToList();
        var failureCount = 0;

        Parallel.ForEach(ids, id =>
        {
            var started = manager.Start(id, cfg.InstallDir, cfg.Ports);
            if (!started.IsOk)
            {
                var displayName = id.DisplayName();
                logSidecarStartFailed(log, displayName, started.Error, null);
                Interlocked.Increment(ref failureCount);
            }
            else
            {
                var displayName = id.DisplayName();
                logSidecarStarted(log, displayName, null);
            }
        });

        if (failureCount > 0)
        {
            StopBackendSidecars(manager, cfg, log);
            return false;
        }

        return true;
    }

    private static void StopBackendSidecars(SidecarServiceManager manager, DesktopConfig cfg, ILogger log)
    {
        foreach (var id in ServiceIdExtensions.BackendServices())
        {
            var stopped = manager.StopService(id, cfg.InstallDir);
            if (!stopped.IsOk)
                logSidecarStopFailed(log, id.DisplayName(), stopped.Error, null);
        }
    }

    private static string? GetArg(string[] args, string flag)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == flag) return args[i + 1];
        return null;
    }

    private static bool DetectHeadless()
    {
        if (OperatingSystem.IsLinux())
            return string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) &&
                   string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        return false;
    }

    private static void HideConsoleWindowIfPresent()
    {
        if (!OperatingSystem.IsWindows())
            return;

        IntPtr hwnd = GetConsoleWindow();
        if (hwnd != IntPtr.Zero)
            ShowWindow(hwnd, swHide);
    }

    private const int swHide = 0;

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
