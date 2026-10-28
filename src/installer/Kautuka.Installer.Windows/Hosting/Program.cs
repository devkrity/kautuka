using Microsoft.Extensions.Logging;

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
}
