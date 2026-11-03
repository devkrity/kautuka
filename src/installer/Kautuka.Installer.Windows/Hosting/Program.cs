using Microsoft.Extensions.Logging;

using System.Runtime.InteropServices;

namespace Kautuka.Installer.Windows.Hosting;

internal static partial class Program
{
    [STAThread]
    internal static void Main(string[] args)
    {
        using var logFactory = LoggerFactory.Create(builder => builder.AddKautukaLogging(LogLevel.Information, "kautuka-installer-windows"));
        var log = logFactory.CreateLogger("kautuka-installer");

        // Handled before BuildLaunchOptions: the supervisor runs on a repeating trigger and must
        // not resolve manifest URLs or materialize signing keys on every pass.
        if (args.Contains("--supervise"))
        {
            RunApplianceSupervisor(log, BuildApplianceSuperviseRequest(args));
            return;
        }

        var launchOptions = BuildLaunchOptions(args);

        if (args.Contains("--uninstall"))
        {
            var result = MessageBoxW(IntPtr.Zero,
                $"Uninstall Kautuka? This permanently deletes the '{launchOptions.UninstallDefaults.DistroName}' Linux appliance and all data stored inside it, including accounts and databases. This cannot be undone.",
                "Uninstall Kautuka", 0x00000004 | 0x00000030 | 0x00000100);
            if (result != 6)
                return;
            RunApplianceUninstall(log, launchOptions.UninstallDefaults);
            return;
        }

        if (args.Contains("--express") || args.Contains("--resume"))
        {
            RunExpressApplianceInstall(log, launchOptions.InstallDefaults);
            return;
        }

        // --headless: Kestrel only, no UI launch. Useful for Windows automation.
        if (args.Contains("--headless"))
        {
            var port = PickPort(log);
            Console.WriteLine("Installer action token: " + InstallerEndpoints.ActionToken);
            RunServerBlocking($"http://127.0.0.1:{port}", log, launchOptions);
            return;
        }

        RunWithEmbeddedWindow(log, launchOptions);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
}
