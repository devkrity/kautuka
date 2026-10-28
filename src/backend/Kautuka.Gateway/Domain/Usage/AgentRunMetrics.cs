using System.Diagnostics;
using System.Diagnostics.Metrics;


namespace Kautuka.Gateway.Domain.Usage;

internal sealed record AgentRunMetricContext(
    string AgentId,
    string Trigger,
    string? ProviderId,
    string? ModelId);

internal sealed record AgentRunMetricObservation(
    string Result,
    long DurationMs,
    int? FirstModelOutputLatencyMs,
    long TokenIn,
    long TokenOut,
    int? ProviderRequestCount,
    int ToolCallCount,
    int ApprovalRequestCount);

internal interface IAgentRunMetrics
{
    void RecordRunStarted(AgentRunMetricContext context);
    void RecordRunCompleted(AgentRunMetricContext context, AgentRunMetricObservation observation);
}

internal sealed class AgentRunMetrics : IAgentRunMetrics
{
    internal const string RunsStartedInstrumentName = "kautuka.agent_run.started_total";
    internal const string RunsFinishedInstrumentName = "kautuka.agent_run.finished_total";
    internal const string ToolCallsInstrumentName = "kautuka.agent_run.tool_calls_total";
    internal const string ApprovalRequestsInstrumentName = "kautuka.agent_run.approval_requests_total";

    private readonly Counter<long> runsStarted;
    private readonly Counter<long> runsFinished;
    private readonly Counter<long> toolCalls;
    private readonly Counter<long> approvalRequests;

    public AgentRunMetrics(GatewayMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        Meter meter = meterFactory.GetOrCreateMeter(GatewayObservability.AgentRunMeterName);

        runsStarted = meter.CreateCounter<long>(RunsStartedInstrumentName);
        runsFinished = meter.CreateCounter<long>(RunsFinishedInstrumentName);
        toolCalls = meter.CreateCounter<long>(ToolCallsInstrumentName);
        approvalRequests = meter.CreateCounter<long>(ApprovalRequestsInstrumentName);
    }

    public void RecordRunStarted(AgentRunMetricContext context)
        => runsStarted.Add(1, BuildBaseTags(context));

    public void RecordRunCompleted(AgentRunMetricContext context, AgentRunMetricObservation observation)
    {
        TagList baseTags = BuildBaseTags(context);
        TagList completionTags = BuildCompletionTags(baseTags, observation.Result);

        runsFinished.Add(1, completionTags);

        if (observation.ToolCallCount > 0)
        {
            toolCalls.Add(observation.ToolCallCount, completionTags);
        }

        if (observation.ApprovalRequestCount > 0)
        {
            approvalRequests.Add(observation.ApprovalRequestCount, completionTags);
        }
    }

    private static TagList BuildBaseTags(AgentRunMetricContext context)
    {
        var tags = new TagList
        {
            { "agent_id", context.AgentId },
            { "trigger", context.Trigger },
        };

        if (!string.IsNullOrWhiteSpace(context.ProviderId))
        {
            tags.Add("provider_id", context.ProviderId);
        }

        if (!string.IsNullOrWhiteSpace(context.ModelId))
        {
            tags.Add("model_id", context.ModelId);
        }

        return tags;
    }

    private static TagList BuildCompletionTags(TagList baseTags, string result)
    {
        var tags = baseTags;
        tags.Add("result", result);
        return tags;
    }
}
