using System.Diagnostics.Metrics;

using Kautuka.Gateway.Domain.Usage;

namespace Kautuka.IntegrationTests;

public sealed class AgentRunMetricsTests
{
    [Fact]
    public void AgentRunMetricsRecordsAppLevelCounters()
    {
        var longMeasurements = new List<(string InstrumentName, long Value, KeyValuePair<string, object?>[] Tags)>();

        using var listener = new MeterListener();
        listener.InstrumentPublished = static (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == GatewayObservability.AgentRunMeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
            longMeasurements.Add((instrument.Name, measurement, tags.ToArray())));
        listener.Start();

        var meterFactory = new GatewayMeterFactory();
        var metrics = new AgentRunMetrics(meterFactory);
        var context = new AgentRunMetricContext("ops-agent", "autopilot", "provider-alpha", "model-alpha");

        metrics.RecordRunStarted(context);
        metrics.RecordRunCompleted(context, new AgentRunMetricObservation(
            "completed",
            1200,
            250,
            110,
            44,
            2,
            3,
            1));

        Assert.Contains(longMeasurements, measurement =>
            measurement.InstrumentName == AgentRunMetrics.RunsStartedInstrumentName
            && measurement.Value == 1
            && HasTag(measurement.Tags, "agent_id", "ops-agent")
            && HasTag(measurement.Tags, "trigger", "autopilot"));

        Assert.Contains(longMeasurements, measurement =>
            measurement.InstrumentName == AgentRunMetrics.RunsFinishedInstrumentName
            && measurement.Value == 1
            && HasTag(measurement.Tags, "result", "completed"));

        Assert.Contains(longMeasurements, measurement =>
            measurement.InstrumentName == AgentRunMetrics.ToolCallsInstrumentName
            && measurement.Value == 3);

        Assert.Contains(longMeasurements, measurement =>
            measurement.InstrumentName == AgentRunMetrics.ApprovalRequestsInstrumentName
            && measurement.Value == 1);
    }

    private static bool HasTag(IEnumerable<KeyValuePair<string, object?>> tags, string key, string expectedValue)
        => tags.Any(tag => tag.Key == key && string.Equals(tag.Value?.ToString(), expectedValue, StringComparison.Ordinal));
}
