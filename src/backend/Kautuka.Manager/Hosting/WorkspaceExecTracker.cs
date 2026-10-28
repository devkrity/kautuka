using System.Threading.Channels;

namespace Kautuka.Manager.Hosting;

/// <summary>
/// Tracks concurrent <c>Exec</c> calls into the runtime. When the last in-flight exec
/// completes and no others are running, a signal is written to a bounded channel so the
/// <see cref="WorkspaceContainerSupervisorWorker"/> can trigger an idle drift audit immediately
/// rather than waiting up to 30 minutes for the periodic timer.
///
/// The channel has capacity 1 with <see cref="BoundedChannelFullMode.DropWrite"/> so that
/// multiple rapid session-end events naturally coalesce into a single audit trigger.
/// </summary>
public sealed class WorkspaceExecTracker
{
    private static readonly BoundedChannelOptions channelOptions = new(capacity: 1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false,
    };

    private readonly Channel<byte> idleTrigger = Channel.CreateBounded<byte>(channelOptions);
    private int activeExecs;

    /// <summary>
    /// The channel reader the worker loop awaits. Receives a signal whenever the last
    /// concurrent exec completes and the system becomes idle.
    /// </summary>
    public ChannelReader<byte> IdleTrigger => idleTrigger.Reader;

    /// <summary>
    /// Signals that an <c>Exec</c> call is starting. Must be paired with <see cref="ExecEnded"/>.
    /// </summary>
    public void ExecStarted() => Interlocked.Increment(ref activeExecs);

    /// <summary>
    /// Signals that an <c>Exec</c> call completed. If this was the last in-flight exec,
    /// writes to the idle channel so the worker loop wakes for a drift audit.
    /// </summary>
    public void ExecEnded()
    {
        int remaining = Interlocked.Decrement(ref activeExecs);
        if (remaining <= 0)
        {
            // Clamp to zero in case of mismatched calls.
            Interlocked.CompareExchange(ref activeExecs, 0, remaining);
            // DropWrite if channel already has a pending signal — natural coalescing.
            idleTrigger.Writer.TryWrite(0);
        }
    }

    /// <summary>Active exec count — exposed for diagnostics/logging only.</summary>
    public int ActiveExecCount => Volatile.Read(ref activeExecs);
}
