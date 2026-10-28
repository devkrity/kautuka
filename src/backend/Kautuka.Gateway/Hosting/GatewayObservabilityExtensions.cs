using OpenTelemetry.Metrics;

namespace Kautuka.Gateway.Hosting;

internal static partial class GatewayObservabilityExtensions
{
    internal const string MicrosoftExtensionsAiMeterName = "Experimental.Microsoft.Extensions.AI";
    internal const string MicrosoftAgentsAiMeterName = "Experimental.Microsoft.Agents.AI";

    public static void ConfigureGatewayMetrics(MeterProviderBuilder metrics)
    {
        metrics.AddMeter(GatewayObservability.AgentRunMeterName);
        metrics.AddMeter(MicrosoftExtensionsAiMeterName);
        metrics.AddMeter(MicrosoftAgentsAiMeterName);
    }
}
