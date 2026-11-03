using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

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
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Kautuka", "InstallerNative");
        Directory.CreateDirectory(directory);
        if ((File.GetAttributes(Path.GetDirectoryName(directory)!) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Installer native library directory cannot be a link.");

        TryMaterializeNative("Kautuka.Installer.Windows.Photino.Native.dll", Path.Combine(directory, "Photino.Native.dll"));
        TryMaterializeNative("Kautuka.Installer.Windows.WebView2Loader.dll", Path.Combine(directory, "WebView2Loader.dll"));
        NativeLibrary.Load(Path.Combine(directory, "WebView2Loader.dll"));
        string photinoPath = Path.Combine(directory, "Photino.Native.dll");
        NativeLibrary.SetDllImportResolver(typeof(PhotinoWindow).Assembly, (name, assembly, searchPath) =>
            string.Equals(name, "Photino.Native", StringComparison.OrdinalIgnoreCase)
                ? NativeLibrary.Load(photinoPath) : IntPtr.Zero);
    }

    private static void TryMaterializeNative(string resourceName, string destination)
    {
        using Stream? stream = typeof(Program).Assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            return;

        using var content = new MemoryStream();
        stream.CopyTo(content);
        byte[] bytes = content.ToArray();
        if (File.Exists(destination) && CryptographicOperations.FixedTimeEquals(SHA256.HashData(File.ReadAllBytes(destination)), SHA256.HashData(bytes)))
            return;
        File.WriteAllBytes(destination, bytes);
    }

    private static bool TryOpenEmbeddedWindow(string uiUrl, string loggedUrl, CancellationTokenSource cts, ILogger log)
    {
        try
        {
            var window = new PhotinoWindow()
                .SetTitle("Kautuka Installer")
                .SetUseOsDefaultSize(false)
                .SetSize(820, 680)
                .SetMinSize(560, 480)
                .Center()
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
