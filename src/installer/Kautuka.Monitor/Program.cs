namespace Kautuka.Monitor;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string distroName = ReadOption(args, "--distro-name") ?? AppliancePaths.DefaultDistroName;
        string installDir = ReadOption(args, "--install-dir") ?? AppliancePaths.InstallDir(distroName);

        if (HasFlag(args, "--supervise"))
            return RunSupervise(distroName, installDir);

        return MonitorTray.Run(distroName, installDir);
    }

    private static int RunSupervise(string distroName, string installDir)
    {
        try
        {
            ApplianceSupervisorResult result = ApplianceSupervisor
                .RunAsync(distroName, installDir, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            return result.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return 1;
        }
        catch (IOException)
        {
            return 1;
        }
        catch (UnauthorizedAccessException)
        {
            return 1;
        }
    }

    private static bool HasFlag(string[] args, string flag)
        => args.Any(arg => string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase));

    private static string? ReadOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        }

        return null;
    }
}
