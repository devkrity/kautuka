using System.Diagnostics;

namespace Kautuka.Manager.Hosting;

internal static class EnvoySocketSelinuxLabel
{
    internal static void RelabelIfRequired(bool isLinux, string? enabled, string socketDirectory, Func<string, int>? relabel = null)
    {
        if (!isLinux || !string.Equals(enabled, "1", StringComparison.Ordinal))
            return;

        if ((relabel ?? Relabel)(socketDirectory) != 0)
            throw new InvalidOperationException("Could not label the Envoy authorization socket directory for SELinux.");
    }

    private static int Relabel(string socketDirectory)
    {
        var start = new ProcessStartInfo("/usr/bin/chcon") { UseShellExecute = false };
        start.ArgumentList.Add("-t");
        start.ArgumentList.Add("container_file_t");
        start.ArgumentList.Add(socketDirectory);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start SELinux socket relabeling.");
        if (!process.WaitForExit(10_000))
        {
            process.Kill();
            throw new InvalidOperationException("SELinux socket relabeling timed out.");
        }
        return process.ExitCode;
    }
}
