using System.Diagnostics;

namespace Kautuka.IntegrationTests;

internal static class FileSystemLinkFixture
{
    internal static Process? Start(string link, string target, bool directoryLink)
    {
        var startInfo = new ProcessStartInfo
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = $"/c mklink /{(directoryLink ? "J" : "H")} \"{link}\" \"{target}\"";
        }
        else
        {
            startInfo.FileName = "ln";
            if (directoryLink)
            {
                startInfo.ArgumentList.Add("-s");
            }

            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add(target);
            startInfo.ArgumentList.Add(link);
        }

        return Process.Start(startInfo);
    }
}
