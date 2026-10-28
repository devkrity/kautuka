namespace Kautuka.Gateway.Domain.Usage;

internal interface IGatewayUsageCaptureAccessor
{
    IDisposable BeginScope();
    void RecordProviderRequestStarted();
    void RecordFirstModelOutput(DateTimeOffset observedAt);
    GatewayCapturedUsageMetrics? Snapshot();
}

internal sealed class GatewayUsageCaptureAccessor : IGatewayUsageCaptureAccessor
{
    private static readonly AsyncLocal<UsageCaptureState?> current = new();

    public IDisposable BeginScope()
    {
        UsageCaptureState? prior = current.Value;
        current.Value = new UsageCaptureState();
        return new UsageCaptureScope(prior);
    }

    public void RecordProviderRequestStarted()
    {
        if (current.Value is { } state)
        {
            state.ProviderRequestCount++;
        }
    }

    public void RecordFirstModelOutput(DateTimeOffset observedAt)
    {
        if (current.Value is { FirstModelOutputAt: null } state)
        {
            state.FirstModelOutputAt = observedAt;
        }
    }

    public GatewayCapturedUsageMetrics? Snapshot()
    {
        UsageCaptureState? state = current.Value;
        if (state is null)
        {
            return null;
        }

        return new GatewayCapturedUsageMetrics(
            state.ProviderRequestCount,
            state.FirstModelOutputAt?.ToString("O"));
    }

    private sealed class UsageCaptureState
    {
        public int ProviderRequestCount { get; set; }

        public DateTimeOffset? FirstModelOutputAt { get; set; }
    }

    private sealed class UsageCaptureScope(UsageCaptureState? prior) : IDisposable
    {
        private readonly UsageCaptureState? prior = prior;
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            current.Value = prior;
        }
    }
}
