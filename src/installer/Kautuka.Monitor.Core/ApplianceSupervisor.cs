using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Kautuka.Monitor;

/// <summary>Outcome of a single supervisor pass.</summary>
public sealed record ApplianceSupervisorResult
{
    public required string DistroName { get; init; }

    /// <summary>The distro is not registered for this user. Nothing else was attempted.</summary>
    public bool DistroMissing { get; init; }

    /// <summary>
    /// No keepalive held the lock, so this pass started one. On a distro that was already
    /// resident this is false; a true value means residency had lapsed and was restored.
    /// </summary>
    public bool KeepaliveStarted { get; init; }

    /// <summary><c>.wslconfig</c> content differs from the recorded baseline.</summary>
    public bool HostConfigChanged { get; init; }

    public IReadOnlyList<WslConfigFinding> Findings { get; init; } = [];

    /// <summary>Set when the pass could not establish the appliance's state.</summary>
    public string? Error { get; init; }

    public bool HasBreakingFinding
        => Findings.Any(finding => finding.Severity == WslConfigSeverity.Breaking);

    /// <summary>
    /// 0 healthy, 1 the pass could not run, 2 a host setting is breaking the appliance.
    /// Task Scheduler stores this as <c>LastTaskResult</c>.
    /// </summary>
    public int ExitCode
    {
        get
        {
            if (DistroMissing || !string.IsNullOrWhiteSpace(Error))
                return 1;

            return HasBreakingFinding ? 2 : 0;
        }
    }
}

/// <summary>
/// One short-lived residency and drift pass. Deliberately not a resident process: the
/// scheduled task starts it again. Closing the tray does not stop the appliance.
/// </summary>
public static class ApplianceSupervisor
{
    /// <summary>
    /// Lives on tmpfs, so it is cleared whenever the distro restarts. That is deliberate: a
    /// keepalive lock must not survive the instance it was holding up.
    /// </summary>
    private const string keepaliveLockPath = "/run/kautuka-keepalive.lock";

    private const string keepaliveCommand = "/usr/bin/sleep infinity";

    private const long supervisorLogMaxBytes = 256 * 1024;

    /// <summary>
    /// One short-lived residency and drift pass.
    /// </summary>
    /// <param name="distroName">The WSL distro to keep resident.</param>
    /// <param name="installDir">The distro's install directory. State is written beside it.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <param name="hostConfigPath">
    /// Overridable so tests can exercise drift evaluation against a fixture instead of the
    /// caller's real <c>.wslconfig</c>. Defaults to the live path.
    /// </param>
    public static async Task<ApplianceSupervisorResult> RunAsync(
        string distroName,
        string installDir,
        CancellationToken cancellationToken,
        string? hostConfigPath = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("The appliance supervisor is only supported on Windows hosts.");

        ArgumentException.ThrowIfNullOrWhiteSpace(distroName);
        ArgumentException.ThrowIfNullOrWhiteSpace(installDir);
        cancellationToken.ThrowIfCancellationRequested();

        KeepaliveState keepalive = await ProbeKeepaliveAsync(distroName, cancellationToken).ConfigureAwait(false);

        if (keepalive == KeepaliveState.DistroMissing)
        {
            return new ApplianceSupervisorResult
            {
                DistroName = distroName,
                DistroMissing = true,
                Error = $"WSL distro '{distroName}' is not registered for this user.",
            };
        }

        if (keepalive == KeepaliveState.Indeterminate)
        {
            return new ApplianceSupervisorResult
            {
                DistroName = distroName,
                Error = $"Could not determine keepalive state for '{distroName}'. The image may predate {keepaliveLockPath} support.",
            };
        }

        var keepaliveStarted = false;
        if (keepalive == KeepaliveState.Free)
        {
            // A failed start is still reported as an attempt. The next pass probes the lock again.
            await StartKeepaliveAsync(distroName, cancellationToken).ConfigureAwait(false);
            keepaliveStarted = true;
        }

        (bool changed, IReadOnlyList<WslConfigFinding> findings) = EvaluateHostConfig(
            installDir,
            hostConfigPath ?? WslConfigDrift.DefaultConfigPath());

        var result = new ApplianceSupervisorResult
        {
            DistroName = distroName,
            KeepaliveStarted = keepaliveStarted,
            HostConfigChanged = changed,
            Findings = findings,
        };

        AppendSupervisorLog(installDir, result);
        return result;
    }

    public static string LogPath(string installDir)
        => Path.Combine(AppliancePaths.StateRoot(installDir), "supervisor.log");

    private enum KeepaliveState
    {
        Free,
        Held,
        DistroMissing,
        Indeterminate,
    }

    /// <summary>
    /// Uses <c>flock</c> rather than counting processes: the appliance ships without
    /// <c>procps-ng</c>, so <c>pgrep</c>/<c>ps</c> do not exist. The lock is also race-free.
    /// </summary>
    private static async Task<KeepaliveState> ProbeKeepaliveAsync(
        string distroName,
        CancellationToken cancellationToken)
    {
        ProcessResult probe = await RunProcessAsync(
            "wsl.exe",
            ["-d", distroName, "-u", "root", "--exec", "/usr/bin/flock", "-n", keepaliveLockPath, "-c", "true"],
            cancellationToken).ConfigureAwait(false);

        if (IsDistroNotFound(probe))
            return KeepaliveState.DistroMissing;

        // flock -n exits 1 specifically when the lock could not be taken. Any other non-zero
        // code is a different failure and must not be read as "a keepalive is running".
        return probe.ExitCode switch
        {
            0 => KeepaliveState.Free,
            1 => KeepaliveState.Held,
            _ => KeepaliveState.Indeterminate,
        };
    }

    /// <summary>
    /// Residency requires a process under a <c>Relay</c> parent, which only a host-initiated
    /// <c>wsl.exe</c> invocation produces. <c>setsid</c> detaches it so it outlives this process,
    /// and <c>flock -n</c> makes a duplicate start exit immediately.
    /// </summary>
    private static Task<ProcessResult> StartKeepaliveAsync(string distroName, CancellationToken cancellationToken)
        => RunProcessAsync(
            "wsl.exe",
            [
                "-d", distroName, "-u", "root", "--exec",
                "/usr/bin/setsid", "/usr/bin/flock", "-n", keepaliveLockPath, "-c", keepaliveCommand,
            ],
            cancellationToken);

    private static (bool Changed, IReadOnlyList<WslConfigFinding> Findings) EvaluateHostConfig(
        string installDir,
        string hostConfigPath)
    {
        string? configText = WslConfigDrift.TryRead(hostConfigPath);
        string hash = WslConfigDrift.ComputeHash(configText);

        string baselinePath = Path.Combine(AppliancePaths.StateRoot(installDir), "wslconfig-baseline.txt");
        string? baseline = File.Exists(baselinePath)
            ? File.ReadAllText(baselinePath).Trim()
            : null;

        IReadOnlyList<WslConfigFinding> findings = WslConfigDrift.Evaluate(configText);
        var changed = baseline is not null
            && !string.Equals(baseline, hash, StringComparison.OrdinalIgnoreCase);

        if (baseline is null || changed)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(baselinePath)!);
            File.WriteAllText(baselinePath, hash, Encoding.ASCII);
        }

        return (changed, findings);
    }

    private static void AppendSupervisorLog(string installDir, ApplianceSupervisorResult result)
    {
        string path = LogPath(installDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (File.Exists(path) && new FileInfo(path).Length > supervisorLogMaxBytes)
            File.Delete(path);

        var builder = new StringBuilder();
        builder.Append(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        builder.Append(" distro=").Append(result.DistroName);
        builder.Append(" keepaliveStarted=").Append(result.KeepaliveStarted);
        builder.Append(" hostConfigChanged=").Append(result.HostConfigChanged);

        foreach (WslConfigFinding finding in result.Findings)
        {
            builder.Append(" [").Append(finding.Severity).Append(' ')
                .Append(finding.Key).Append('=').Append(finding.Value).Append(']');
        }

        if (!string.IsNullOrWhiteSpace(result.Error))
            builder.Append(" error=").Append(result.Error);

        builder.AppendLine();
        File.AppendAllText(path, builder.ToString(), Encoding.UTF8);
    }

    private static bool IsDistroNotFound(ProcessResult result)
    {
        string combined = string.Join(
            Environment.NewLine,
            new[] { result.StandardOutput, result.StandardError }.Where(static line => !string.IsNullOrWhiteSpace(line)));
        return combined.Contains("WSL_E_DISTRO_NOT_FOUND", StringComparison.OrdinalIgnoreCase)
            || combined.Contains("There is no distribution with the supplied name", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        return new ProcessResult(
            process.ExitCode,
            await stdoutTask.ConfigureAwait(false),
            await stderrTask.ConfigureAwait(false));
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
