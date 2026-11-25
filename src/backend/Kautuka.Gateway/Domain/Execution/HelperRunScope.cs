namespace Kautuka.Gateway.Domain.Execution;

// A nested helper shares its admitted parent's lease, even if a drain starts.
// Refcounts retain the OS lease until every inherited child has finished.
internal sealed class HelperRunScope : IDisposable
{
    private static readonly AsyncLocal<State?> current = new();
    private readonly State state;
    private State? previous;
    private bool entered;

    private HelperRunScope(State state) => this.state = state;

    internal static async ValueTask<HelperRunScope> AcquireAsync(CancellationToken cancellationToken)
    {
        State? inherited = current.Value;
        if (inherited is not null && inherited.TryRetain()) return new HelperRunScope(inherited);
        return new HelperRunScope(new State(await KautukaDatabaseWriterLease.AcquireHelperRunAsync(cancellationToken).ConfigureAwait(false)));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000", Justification = "Ownership transfers to the returned scope.")]
    internal static bool TryAcquireRoot(out HelperRunScope? scope)
    {
        if (!KautukaDatabaseWriterLease.TryAcquireHelperRun(out KautukaDatabaseWriterLease? lease))
        {
            scope = null;
            return false;
        }
        scope = new HelperRunScope(new State(lease));
        return true;
    }

    // Call in the execution body, not inside AcquireAsync: AsyncLocal changes
    // in an awaited callee do not propagate back to the caller's context.
    internal void Enter()
    {
        previous = current.Value;
        current.Value = state;
        entered = true;
    }

    public void Dispose()
    {
        if (entered) current.Value = previous;
        state.Release();
    }

    private sealed class State(KautukaDatabaseWriterLease? lease)
    {
        private readonly Lock sync = new();
        private int references = 1;
        internal bool TryRetain()
        {
            lock (sync)
            {
                if (references == 0) return false;
                references++;
                return true;
            }
        }
        internal void Release()
        {
            lock (sync)
            {
                if (--references == 0) lease?.Dispose();
            }
        }
    }
}
