using System.Diagnostics;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Infrastructure.Observability;
using Kautuka.Telemetry.Telemetry;

namespace Kautuka.ContractTests;

public sealed class AgentRunTraceContinuationTests
{
    [Fact]
    public void CaptureRunTraceContextIgnoresAmbientHttpAndParentsResumeOntoTheSameTrace()
    {
        using ActivityListener listener = CreateListener(
            KautukaActivitySources.GatewayAgentRunSource,
            "Microsoft.AspNetCore");
        using var runSource = new ActivitySource(KautukaActivitySources.GatewayAgentRunSource);
        using Activity? firstAttempt = runSource.StartActivity("agent (assistant) run [narad]", ActivityKind.Internal);
        Assert.NotNull(firstAttempt);
        AgentRunHostCallTracer.MarkAgentRun(firstAttempt, "run-1");

        using var httpSource = new ActivitySource("Microsoft.AspNetCore");
        using Activity? http = httpSource.StartActivity("GET /v1/narad", ActivityKind.Server);
        Assert.NotNull(http);
        Activity.Current = http;

        AgentRunHostCallTracer.AgentRunTraceContext? waitState =
            AgentRunHostCallTracer.CaptureRunTraceContext(firstAttempt, "run-1");
        Assert.NotNull(waitState);
        Assert.Equal(firstAttempt.Id, waitState.Value.TraceParent);

        AgentRequestContext? resumeContext = AgentRunHostCallTracer.CreateRequestContext(
            waitState.Value.TraceParent,
            waitState.Value.TraceState);
        Assert.True(AgentRunHostCallTracer.TryParseRequestTraceContext(resumeContext?.Trace, out ActivityContext parentContext));

        using Activity? resumeAttempt = runSource.StartActivity(
            "agent (assistant) run [hitl]",
            ActivityKind.Internal,
            parentContext);
        Assert.NotNull(resumeAttempt);
        Assert.Equal(firstAttempt.TraceId, resumeAttempt.TraceId);
        Assert.Equal(firstAttempt.SpanId, resumeAttempt.ParentSpanId);
    }

    [Fact]
    public void NestedChildRunSharesParentTraceIdWhenRunActivityIsRestored()
    {
        using ActivityListener listener = CreateListener(KautukaActivitySources.GatewayAgentRunSource);
        using var runSource = new ActivitySource(KautukaActivitySources.GatewayAgentRunSource);
        using Activity? parentRun = runSource.StartActivity("agent (assistant) run [narad]", ActivityKind.Internal);
        Assert.NotNull(parentRun);
        AgentRunHostCallTracer.MarkAgentRun(parentRun, "run-parent");

        using var httpSource = new ActivitySource("Microsoft.AspNetCore");
        using ActivityListener httpListener = CreateListener("Microsoft.AspNetCore");
        using Activity? http = httpSource.StartActivity("GET /v1/narad", ActivityKind.Server);
        Activity.Current = http;

        AgentRunHostCallTracer.RestoreCurrent(parentRun);
        AgentRequestContext? childContext = AgentRunHostCallTracer.CaptureCurrentRequestContext();
        Assert.True(AgentRunHostCallTracer.TryParseRequestTraceContext(childContext?.Trace, out ActivityContext childParent));

        using Activity? childRun = runSource.StartActivity(
            "agent (roller) run [agent_tool]",
            ActivityKind.Internal,
            childParent);
        Assert.NotNull(childRun);
        Assert.Equal(parentRun.TraceId, childRun.TraceId);
        Assert.Equal(parentRun.SpanId, childRun.ParentSpanId);
    }

    [Fact]
    public void CaptureCurrentRunTraceContextDoesNotAdoptUnmarkedHttpActivity()
    {
        using ActivityListener listener = CreateListener("Microsoft.AspNetCore");
        using var httpSource = new ActivitySource("Microsoft.AspNetCore");
        using Activity? http = httpSource.StartActivity("GET /v1/narad", ActivityKind.Server);
        Assert.NotNull(http);
        Activity.Current = http;

        Assert.Null(AgentRunHostCallTracer.CaptureCurrentRunTraceContext("run-1"));
        Assert.Null(AgentRunHostCallTracer.CaptureCurrentRequestContext());
    }

    [Fact]
    public async Task RestoreCurrentRebindsRunActivityAfterAsyncIteratorYield()
    {
        using ActivityListener listener = CreateListener(KautukaActivitySources.GatewayAgentRunSource);
        using var runSource = new ActivitySource(KautukaActivitySources.GatewayAgentRunSource);
        using Activity? runActivity = runSource.StartActivity("agent (assistant) run [narad]", ActivityKind.Internal);
        Assert.NotNull(runActivity);
        AgentRunHostCallTracer.MarkAgentRun(runActivity, "run-yield");
        ActivityTraceId expected = runActivity.TraceId;

        int seen = 0;
        await foreach (ActivityTraceId traceId in YieldThenRestore(runActivity))
        {
            seen++;
            Assert.Equal(expected, traceId);
        }

        Assert.Equal(2, seen);
        AgentRunHostCallTracer.RestoreCurrent(runActivity);
        Assert.Equal(expected, Activity.Current?.TraceId);
    }

    private static async IAsyncEnumerable<ActivityTraceId> YieldThenRestore(Activity runActivity)
    {
        yield return Activity.Current!.TraceId;
        AgentRunHostCallTracer.RestoreCurrent(runActivity);
        yield return Activity.Current!.TraceId;
    }

    private static ActivityListener CreateListener(params string[] sourceNames)
    {
        var names = new HashSet<string>(sourceNames, StringComparer.Ordinal);
        var listener = new ActivityListener
        {
            ShouldListenTo = source => names.Contains(source.Name),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
