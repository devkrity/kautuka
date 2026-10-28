using System.Diagnostics.Metrics;

namespace Kautuka.Gateway.Hosting;

/// <summary>
/// Factory for creating and caching meters that are properly registered with the OpenTelemetry system.
/// Meters created through this factory are created after MeterProvider initialization, ensuring
/// they're properly exported to OTLP endpoints like Prometheus.
/// </summary>
internal sealed class GatewayMeterFactory
{
    private readonly Dictionary<string, Meter> meters = [];
    private readonly object gate = new();

    public Meter GetOrCreateMeter(string meterName)
    {
        lock (gate)
        {
            if (meters.TryGetValue(meterName, out var existingMeter))
            {
                return existingMeter;
            }

            var meter = new Meter(meterName);
            meters.Add(meterName, meter);
            return meter;
        }
    }
}
