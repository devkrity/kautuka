using System.Diagnostics;
using System.Text.Json;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

public sealed partial class ContainerEgressRuntime
{
    // Called only while holding the topology gate. Never treat a failed inventory as free space.
    private async Task<HashSet<int>> ValidatePoolInventoryAsync(NetworkAddressPool pool, CancellationToken cancellationToken)
    {
        PodmanCommandResult listed = await RunAsync(["network", "ls", "--format", "{{.ID}}"], cancellationToken).ConfigureAwait(false);
        string[] ids = listed.StdOut.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string networkJson = "[]";
        if (ids.Length > 0)
        {
            var arguments = new List<string> { "network", "inspect" };
            arguments.AddRange(ids);
            networkJson = (await RunAsync(arguments, cancellationToken).ConfigureAwait(false)).StdOut;
        }
        string routeJson = await ReadHostRoutesAsync(cancellationToken).ConfigureAwait(false);
        using JsonDocument networksDocument = JsonDocument.Parse(networkJson);
        using JsonDocument routesDocument = JsonDocument.Parse(routeJson);
        return NetworkPoolInventory.Validate(pool, networksDocument.RootElement, routesDocument.RootElement);
    }

    private static async Task<string> ReadHostRoutesAsync(CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ip",
                ArgumentList = { "-j", "-4", "route", "show", "table", "all" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        try
        {
            process.Start();
            Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                cancellationToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException("Cannot verify host and VPN routes: the route inventory timed out.");
            }
            string stdout = await output.ConfigureAwait(false);
            string stderr = await error.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException("Cannot verify host and VPN routes: " + stderr.Trim());
            return stdout;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException("Cannot verify host and VPN routes: the ip command is unavailable.", ex);
        }
    }
}
