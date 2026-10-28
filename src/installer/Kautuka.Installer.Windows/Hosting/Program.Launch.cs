using System.Diagnostics;

using Microsoft.Extensions.Logging;

using Photino.NET;

namespace Kautuka.Installer.Windows.Hosting;

internal static partial class Program
{
    private static void RunServerBlocking(string url, ILogger log, WindowsApplianceLaunchOptions launchOptions)
    {
        using var ready = new ManualResetEventSlim(false);
        using var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        RunServer(url, ready, log, launchOptions, cts.Token);
    }

    private static void RunWithEmbeddedWindow(ILogger log, WindowsApplianceLaunchOptions launchOptions)
    {
        var port = PickPort(log);
        var url = $"http://127.0.0.1:{port}";
        var uiUrl = url + "/#token=" + Uri.EscapeDataString(InstallerEndpoints.ActionToken);
        using var ready = new ManualResetEventSlim(false);
        using var cts = new CancellationTokenSource();

        var serverThread = new Thread(() => RunServer(url, ready, log, launchOptions, cts.Token))
        {
            IsBackground = true,
            Name = "installer-server",
        };
        serverThread.Start();

        if (!ready.Wait(TimeSpan.FromSeconds(10)))
            ProgramLogMessages.ServerReadySignalMissing(log);

        ProgramLogMessages.OpeningInstaller(log, url);
        MaterializePhotinoNative();
        if (!TryOpenEmbeddedWindow(uiUrl, url, cts, log))
            TryOpenBrowser(uiUrl, url, log);

        serverThread.Join();
        Environment.Exit(0);
    }

    /// <summary>
    /// The public feed publishes one executable. Photino loads these two native libraries
    /// from the executable's directory, so a download of only the exe crashes unless they
    /// are written there first.
    /// </summary>
    private static void MaterializePhotinoNative()
    {
        string? directory = Path.GetDirectoryName(Environment.ProcessPath);
        if (string.IsNullOrWhiteSpace(directory))
            return;

        TryMaterializeNative("Kautuka.Installer.Windows.Photino.Native.dll", Path.Combine(directory, "Photino.Native.dll"));
        TryMaterializeNative("Kautuka.Installer.Windows.WebView2Loader.dll", Path.Combine(directory, "WebView2Loader.dll"));
    }

    private static void TryMaterializeNative(string resourceName, string destination)
    {
        using Stream? stream = typeof(Program).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            return;

        if (File.Exists(destination) && new FileInfo(destination).Length == stream.Length)
            return;

        using FileStream file = File.Create(destination);
        stream.CopyTo(file);
    }

    private static bool TryOpenEmbeddedWindow(string uiUrl, string loggedUrl, CancellationTokenSource cts, ILogger log)
    {
        try
        {
            var window = new PhotinoWindow()
                .SetTitle("Kautuka Installer")
                .SetUseOsDefaultSize(true)
                .SetContextMenuEnabled(false)
                .Load(uiUrl);

            window.WindowClosing += (_, _) =>
            {
                cts.Cancel();
                return false;
            };

            ProgramLogMessages.EmbeddedWindowLaunched(log, loggedUrl);
            window.WaitForClose();
            return true;
        }
        catch (InvalidOperationException ex)
        {
            ProgramLogMessages.EmbeddedWindowLaunchFailed(log, ex);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            ProgramLogMessages.EmbeddedWindowLaunchFailed(log, ex);
        }

        return false;
    }

    private static void TryOpenBrowser(string uiUrl, string loggedUrl, ILogger log)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = uiUrl,
                UseShellExecute = true,
            };

            _ = Process.Start(psi);
        }
        catch (InvalidOperationException ex)
        {
            ProgramLogMessages.BrowserLaunchFailed(log, ex, loggedUrl);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            ProgramLogMessages.BrowserLaunchFailed(log, ex, loggedUrl);
        }
    }
}
