using System.ComponentModel;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

/// <summary>
/// Checks Podman and sticky-caches a successful sample so status and reconcile callers
/// never wait on a live <c>podman info</c> after the first healthy result. Failures are
/// not sticky — the next caller re-probes so a transient Podman hang cannot permanently
/// mark the runtime unavailable for the Manager process lifetime.
/// </summary>
public interface IManagerRuntimeHealthCommand
{
    Task<PodmanCommandResult> ExecuteAsync(CancellationToken cancellationToken);
}

public sealed class PodmanManagerRuntimeHealthCommand(PodmanCommandRunner podmanCommandRunner)
    : IManagerRuntimeHealthCommand
{
    private const int probeTimeoutSeconds = 3;

    public Task<PodmanCommandResult> ExecuteAsync(CancellationToken cancellationToken)
        => podmanCommandRunner.RunAsync(
            ["info", "--format", "json"],
            probeTimeoutSeconds,
            cancellationToken);
}

public sealed class ManagerRuntimeHealthProbe(IManagerRuntimeHealthCommand healthCommand)
    : IManagerRuntimeHealthProbe
{
    private static readonly TimeSpan timeoutConfirmationDelay = TimeSpan.FromMilliseconds(100);
    private readonly object sync = new();
    private bool? lastKnown;
    private Task<bool>? activeProbe;

    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        Task<bool> pending;
        lock (sync)
        {
            // Only a successful sample is sticky. A cached false would freeze agent
            // execution for the whole Manager lifetime after one hung/timed-out probe.
            if (lastKnown == true)
            {
                return Task.FromResult(true);
            }

            pending = EnsureProbeLocked();
        }

        return pending.WaitAsync(cancellationToken);
    }

    public Task<bool> RefreshAsync(CancellationToken cancellationToken)
    {
        Task<bool> pending;
        lock (sync)
        {
            pending = EnsureProbeLocked();
        }

        return pending.WaitAsync(cancellationToken);
    }

    private Task<bool> EnsureProbeLocked()
    {
        if (activeProbe is not null)
        {
            return activeProbe;
        }

        Task<bool> probe = ProbeAndStoreAsync();
        activeProbe = probe;
        _ = ClearActiveProbeAsync(probe);
        return probe;
    }

    private async Task<bool> ProbeAndStoreAsync()
    {
        bool available = await ProbeAsync().ConfigureAwait(false);
        lock (sync)
        {
            lastKnown = available ? true : null;
        }

        return available;
    }

    private async Task ClearActiveProbeAsync(Task<bool> probe)
    {
        try
        {
            await probe.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The original waiter observes the failure. This observer only owns
            // single-flight cleanup, including when every waiter was cancelled.
        }
        finally
        {
            lock (sync)
            {
                if (ReferenceEquals(activeProbe, probe))
                {
                    activeProbe = null;
                }
            }
        }
    }

    private async Task<bool> ProbeAsync()
    {
        try
        {
            PodmanCommandResult result = await healthCommand
                .ExecuteAsync(CancellationToken.None)
                .ConfigureAwait(false);

            if (result.ExitCode == 0)
            {
                return true;
            }

            // A timeout is ambiguous: Podman can briefly exceed the command deadline
            // while its VM is otherwise healthy. Confirm it once before resetting the
            // Manager readiness gate. Connection/process failures remain immediate.
            if (result.TerminationReason != ProcessTerminationReason.TimedOut)
            {
                return false;
            }

            await Task.Delay(timeoutConfirmationDelay).ConfigureAwait(false);
            result = await healthCommand.ExecuteAsync(CancellationToken.None).ConfigureAwait(false);
            return result.ExitCode == 0;
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException)
        {
            return false;
        }
    }
}
