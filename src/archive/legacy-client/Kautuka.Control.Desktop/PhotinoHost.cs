using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Kautuka.Control.Core.Config;
using Kautuka.Control.Core.Tray;

using Photino.NET;

namespace Kautuka.Control.Desktop;

/// <summary>
/// Creates and runs a Photino WebView window hosting the UI.
/// Blocks the calling thread (must be the main/STA thread).
/// </summary>
internal static class PhotinoHost
{
    private static readonly Action<ILogger, Exception?> logTrayUnsupported = LoggerMessage.Define(
        LogLevel.Information,
        new EventId(0, nameof(logTrayUnsupported)),
        "System tray not supported on this platform - running without tray.");

    public static void Run(DesktopConfig cfg, bool showTray, ILogger log, Action? onExit)
    {
        var tray = CreateTray(showTray, log);
        var forceQuit = false;

        var window = new PhotinoWindow()
            .SetTitle("Kautuka — AI Agent Orchestration")
            .SetUseOsDefaultSize(true)
            .SetContextMenuEnabled(false)
            .Load(cfg.UiUrl());

        // ── Window close: hide to tray instead of quitting ───────────────────
        window.WindowClosing += (_, _) =>
        {
            if (showTray && !forceQuit)
            {
                HideWindow(window);
                tray.ShowBalloon("Kautuka", "Minimised to system tray — double-click the tray icon to reopen.");
                return true; // cancel the OS close — keep process alive
            }
            tray.Dispose();
            onExit?.Invoke();
            return false; // allow close
        };

        // ── System tray menu ─────────────────────────────────────────────────
        if (showTray)
        {
            tray.Show(
                "Kautuka",
                [
                    TrayMenuItem.Item("Open App", () => ShowWindow(window)),
                    TrayMenuItem.Separator(),
                    TrayMenuItem.Item("Restart Kautuka", () =>
                    {
                        var exe = Environment.ProcessPath;
                        if (exe is not null)
                            Process.Start(new ProcessStartInfo { FileName = exe, UseShellExecute = true });
                        forceQuit = true;
                        RequestClose(window, onExit);
                    }),
                    TrayMenuItem.Item("Stop Kautuka", () =>
                    {
                        // Stop Kestrel + hide window; process stays running (tray only)
                        onExit?.Invoke();
                        HideWindow(window);
                        tray.UpdateStatus(false);
                    }),
                    TrayMenuItem.Separator(),
                    TrayMenuItem.Item("Quit", () =>
                    {
                        forceQuit = true;
                        RequestClose(window, onExit);
                    }),
                ],
                onActivate: () => ShowWindow(window));
        }

        // ── Gateway health polling ───────────────────────────────────────────
        Timer? healthTimer = null;
        HttpClient? healthClient = null;
        if (showTray)
        {
            healthClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var healthUri = new Uri(cfg.GatewayUrl(), "/health");
            healthTimer = new Timer(_ =>
            {
                try
                {
                    var resp = healthClient.GetAsync(healthUri).GetAwaiter().GetResult();
                    tray.UpdateStatus(resp.IsSuccessStatusCode);
                }
                catch (HttpRequestException)
                {
                    tray.UpdateStatus(false);
                }
                catch (TaskCanceledException)
                {
                    tray.UpdateStatus(false);
                }
                catch (InvalidOperationException)
                {
                    tray.UpdateStatus(false);
                }
            }, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        }

        window.WaitForClose();

        healthTimer?.Dispose();
        healthClient?.Dispose();
        tray.Dispose();
    }

    // ── Window show/hide helpers ─────────────────────────────────────────────

    private static void HideWindow(PhotinoWindow window)
    {
        if (OperatingSystem.IsWindows())
            ShowWindowWin32(window.WindowHandle, swHide);
        else
            window.SetMinimized(true);
    }

    private static void ShowWindow(PhotinoWindow window)
    {
        if (OperatingSystem.IsWindows())
        {
            ShowWindowWin32(window.WindowHandle, swRestore);
            SetForegroundWindow(window.WindowHandle);
        }
        else
        {
            window.SetMinimized(false);
        }
    }

    private static void RequestClose(PhotinoWindow window, Action? onExit)
    {
        if (OperatingSystem.IsWindows())
        {
            PostMessageWin32(window.WindowHandle, wmClose, IntPtr.Zero, IntPtr.Zero);
            return;
        }

        onExit?.Invoke();
        Environment.Exit(0);
    }

    // ── Tray factory ─────────────────────────────────────────────────────────

    private static ITrayManager CreateTray(bool showTray, ILogger log)
    {
        if (!showTray) return new NullTrayManager();
        if (OperatingSystem.IsWindows()) return new WindowsTrayManager(log);
        if (OperatingSystem.IsMacOS()) return new MacOsTrayManager(log);
        logTrayUnsupported(log, null);
        return new NullTrayManager();
    }

    // ── Win32 P/Invoke ───────────────────────────────────────────────────────

    private const int swHide = 0;
    private const int swRestore = 9;
    private const uint wmClose = 0x0010u;

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "ShowWindow")]
    [SupportedOSPlatform("windows")]
    private static extern bool ShowWindowWin32(IntPtr hWnd, int nCmdShow);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    [SupportedOSPlatform("windows")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", EntryPoint = "PostMessage")]
    [SupportedOSPlatform("windows")]
    private static extern bool PostMessageWin32(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}

