using System.Diagnostics;
using System.Text;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

public sealed class PodmanCommandRunner
{
    public const string BinaryName = "podman";
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<PodmanCommandResult>>? commandHandler;

    public PodmanCommandRunner()
    {
    }

    internal PodmanCommandRunner(Func<IReadOnlyList<string>, CancellationToken, Task<PodmanCommandResult>> commandHandler)
    {
        ArgumentNullException.ThrowIfNull(commandHandler);
        this.commandHandler = commandHandler;
    }

    public async Task<PodmanCommandResult> RunAsync(
        IReadOnlyList<string> args,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (commandHandler is not null)
        {
            return await commandHandler(args, cancellationToken).ConfigureAwait(false);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = BinaryName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        if (environment is not null)
        {
            foreach ((string key, string value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        Task<string> stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            bool callerCancelled = cancellationToken.IsCancellationRequested;
            return new PodmanCommandResult(
                callerCancelled ? 130 : 124,
                await stdOutTask.ConfigureAwait(false),
                await stdErrTask.ConfigureAwait(false),
                callerCancelled ? ProcessTerminationReason.CallerCancelled : ProcessTerminationReason.TimedOut);
        }

        return new PodmanCommandResult(
            process.ExitCode,
            await stdOutTask.ConfigureAwait(false),
            await stdErrTask.ConfigureAwait(false),
            process.ExitCode == 0 ? ProcessTerminationReason.Completed : ProcessTerminationReason.NonZeroExit);
    }
}

public sealed record PodmanCommandResult(
    int ExitCode,
    string StdOut,
    string StdErr,
    ProcessTerminationReason TerminationReason = ProcessTerminationReason.NonZeroExit);

public enum ProcessTerminationReason
{
    Completed = 0,
    NonZeroExit = 1,
    TimedOut = 2,
    CallerCancelled = 3,
}

public static class PodmanFailureFormatter
{
    private const int maxOutputLength = 800;
    private const int outputHeadLength = 200;

    public static string FormatFailure(string subject, PodmanCommandResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(result);

        return FormatFailure(subject, result.ExitCode, result.StdOut, result.StdErr);
    }

    public static string FormatFailure(string subject, int exitCode, string? stdOut, string? stdErr)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        return string.Join(" | ", BuildParts($"{subject} failed", exitCode, stdOut, stdErr, includePreamblePeriod: true));
    }

    public static string FormatDetails(PodmanCommandResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return FormatDetails(result.ExitCode, result.StdOut, result.StdErr);
    }

    public static string FormatDetails(int exitCode, string? stdOut, string? stdErr)
        => string.Join(" | ", BuildParts($"exit code {exitCode}", exitCode, stdOut, stdErr, includePreamblePeriod: false));

    private static List<string> BuildParts(string preamble, int exitCode, string? stdOut, string? stdErr, bool includePreamblePeriod)
    {
        var parts = new List<string>
        {
            includePreamblePeriod ? $"{preamble} (exit code {exitCode})." : preamble
        };

        string stderr = NormalizeOutput(stdErr);
        string stdout = NormalizeOutput(stdOut);

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            parts.Add($"stderr: {stderr}");
        }

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            parts.Add($"stdout: {stdout}");
        }

        if (parts.Count == 1 && exitCode == 124)
        {
            parts.Add("timed out");
        }

        return parts;
    }

    private static string NormalizeOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return string.Empty;
        }

        string normalized = string.Join(
            " ",
            output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (normalized.Length <= maxOutputLength)
        {
            return normalized;
        }

        int tailLength = maxOutputLength - outputHeadLength - 5;
        return $"{normalized[..outputHeadLength].TrimEnd()} ... {normalized[^tailLength..].TrimStart()}";
    }
}
