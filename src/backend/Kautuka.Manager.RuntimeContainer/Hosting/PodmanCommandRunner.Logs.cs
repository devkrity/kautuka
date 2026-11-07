using System.Diagnostics;
using System.Text;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

internal sealed record PodmanLogBatch(PodmanCommandResult Result, bool Truncated, bool TailSaturated);

public sealed partial class PodmanCommandRunner
{
    /// <summary>Only the log reader uses bounded capture; image/inspect commands keep their existing semantics.</summary>
    internal async Task<PodmanLogBatch> ReadLogsAsync(string containerId, string since, string until, int maximumCharacters,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCharacters, 1);
        string[] arguments = ["logs", "--timestamps", "--since", since, "--until", until, "--tail", "512", containerId];
        if (commandHandler is not null)
        {
            PodmanCommandResult result = await commandHandler(arguments, 10, cancellationToken, null).ConfigureAwait(false);
            int stdoutLength = Math.Min(maximumCharacters, result.StdOut.Length);
            int stderrLength = Math.Min(maximumCharacters - stdoutLength, result.StdErr.Length);
            return new(result with { StdOut = result.StdOut[..stdoutLength], StdErr = result.StdErr[..stderrLength] },
                stdoutLength != result.StdOut.Length || stderrLength != result.StdErr.Length,
                CountLines(result.StdOut) + CountLines(result.StdErr) >= 512);
        }

        var info = new ProcessStartInfo(BinaryName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments) info.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = info };
        process.Start();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        int remaining = maximumCharacters;
        bool truncated = false;
        var captureGate = new object();
        async Task ReadAsync(StreamReader reader, StringBuilder captured)
        {
            char[] buffer = new char[4096];
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), timeout.Token).ConfigureAwait(false)) != 0)
            {
                lock (captureGate)
                {
                    int keep = Math.Min(remaining, count);
                    captured.Append(buffer, 0, keep);
                    remaining -= keep;
                    truncated |= keep != count;
                }
            }
        }
        var outputCapture = new StringBuilder();
        var errorCapture = new StringBuilder();
        Task stdout = ReadAsync(process.StandardOutput, outputCapture);
        Task stderr = ReadAsync(process.StandardError, errorCapture);
        ProcessTerminationReason reason = ProcessTerminationReason.Completed;
        try { await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            reason = cancellationToken.IsCancellationRequested ? ProcessTerminationReason.CallerCancelled : ProcessTerminationReason.TimedOut;
            truncated = true;
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
            }
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
        }
        string output = outputCapture.ToString();
        string error = errorCapture.ToString();
        int exitCode = reason == ProcessTerminationReason.Completed ? process.ExitCode : reason == ProcessTerminationReason.CallerCancelled ? 130 : 124;
        if (reason == ProcessTerminationReason.Completed && exitCode != 0) reason = ProcessTerminationReason.NonZeroExit;
        return new(new(exitCode, output, error, reason), truncated, CountLines(output) + CountLines(error) >= 512);
    }

    private static int CountLines(string value) => value.Length == 0 ? 0 : value.Count(static character => character == '\n') + (value.EndsWith('\n') ? 0 : 1);
}
