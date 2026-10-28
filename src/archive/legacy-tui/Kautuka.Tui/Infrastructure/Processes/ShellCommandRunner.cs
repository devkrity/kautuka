// File: /home/ash/sources/kautuka/src/tui/Kautuka.Tui/Infrastructure/Processes/ShellCommandRunner.cs | Namespace: Kautuka.Tui.Infrastructure.Processes
using System.ComponentModel;
using System.Diagnostics;
using System.Text;

using Kautuka.Tui.Common;

namespace Kautuka.Tui.Infrastructure.Processes;

public sealed class ShellCommandRunner
{
    private readonly Encoding commandEncoding = Encoding.UTF8;

    public async Task<Result<ShellCommandResult>> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        ProcessStartInfo startInfo = new()
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = commandEncoding,
            StandardErrorEncoding = commandEncoding,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environmentVariables is not null)
        {
            foreach ((string key, string value) in environmentVariables)
            {
                startInfo.Environment[key] = value;
            }
        }

        try
        {
            using Process process = new() { StartInfo = startInfo };
            process.Start();

            Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);

            using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));

            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                return Results.Success(new ShellCommandResult(
                    124,
                    await standardOutputTask.ConfigureAwait(false),
                    await standardErrorTask.ConfigureAwait(false)));
            }

            return Results.Success(new ShellCommandResult(
                process.ExitCode,
                await standardOutputTask.ConfigureAwait(false),
                await standardErrorTask.ConfigureAwait(false)));
        }
        catch (Win32Exception exception)
        {
            return Results.Failure<ShellCommandResult>($"Unable to start '{fileName}': {exception.Message}");
        }
        catch (InvalidOperationException exception)
        {
            return Results.Failure<ShellCommandResult>($"Unable to execute '{fileName}': {exception.Message}");
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}

public sealed record ShellCommandResult(int ExitCode, string StdOut, string StdErr)
{
    public bool IsSuccess => ExitCode == 0;

    public string DescribeFailure(string action)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        List<string> parts = [$"{action} failed (exit code {ExitCode})."];
        string standardError = Normalize(StdErr);
        string standardOutput = Normalize(StdOut);

        if (!string.IsNullOrWhiteSpace(standardError))
        {
            parts.Add($"stderr: {standardError}");
        }

        if (!string.IsNullOrWhiteSpace(standardOutput))
        {
            parts.Add($"stdout: {standardOutput}");
        }

        if (parts.Count == 1 && ExitCode == 124)
        {
            parts.Add("timed out");
        }

        return string.Join(" | ", parts);
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string normalized = string.Join(
            " ",
            value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return normalized.Length <= 320
            ? normalized
            : $"{normalized[..320].TrimEnd()}...";
    }
}
