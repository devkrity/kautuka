using System.Diagnostics;
using System.Text;

namespace Kautuka.OciHook;

/// <summary>Runs one bounded helper process without invoking a shell.</summary>
internal static class HookProcess
{
    internal sealed record Result(int ExitCode, string Stdout, string Stderr);

#pragma warning disable CA2025, CA1031 // All I/O tasks are awaited during cleanup; preserve the initiating failure.
    internal static async Task<Result> RunAsync(string executable, IReadOnlyList<string> arguments,
        string? input, TimeSpan timeout, int maxOutputBytes, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxOutputBytes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new InvalidOperationException("Could not start hook helper.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        Task<string> output = ReadBoundedAsync(process.StandardOutput.BaseStream, maxOutputBytes, deadline.Token);
        Task<string> error = ReadBoundedAsync(process.StandardError.BaseStream, maxOutputBytes, deadline.Token);
        Task write = WriteInputAsync(process, input, deadline.Token);
        Task exit = process.WaitForExitAsync(deadline.Token);
        try
        {
            // Await individually: an output-limit failure must stop the process immediately.
            var remaining = new List<Task> { output, error, write, exit };
            while (remaining.Count != 0)
            {
                Task completed = await Task.WhenAny(remaining).ConfigureAwait(false);
                await completed.ConfigureAwait(false);
                remaining.Remove(completed);
            }
            return new(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch
        {
            await deadline.CancelAsync().ConfigureAwait(false);
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            try { await Task.WhenAll(output, error, write, exit).ConfigureAwait(false); }
            catch (Exception) { /* Preserve the original failure. */ }
            throw;
        }
    }

#pragma warning restore CA2025, CA1031
    private static async Task WriteInputAsync(Process process, string? input, CancellationToken token)
    {
        try
        {
            if (input is not null) await process.StandardInput.WriteAsync(input.AsMemory(), token).ConfigureAwait(false);
        }
        finally { process.StandardInput.Close(); }
    }

    private static async Task<string> ReadBoundedAsync(Stream stream, int limit, CancellationToken token)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("Hook helper output exceeded limit.");
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }
        return new UTF8Encoding(false, true).GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }
}
