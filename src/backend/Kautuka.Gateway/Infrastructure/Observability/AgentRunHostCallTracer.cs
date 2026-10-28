using System.Diagnostics;

using Kautuka.Gateway.Domain.Execution;

namespace Kautuka.Gateway.Infrastructure.Observability;

internal static class AgentRunHostCallTracer
{
    private const string agentRunScopeBaggageKey = "kautuka.agent.run.active";
    private const string runIdBaggageKey = "kautuka.run.id";
    private static readonly HashSet<string> exportedParentSources = new(StringComparer.Ordinal)
    {
        KautukaActivitySources.GatewayAgentRunSource,
        "Experimental.Microsoft.Agents.AI",
        "Experimental.Microsoft.Extensions.AI",
    };
    private static readonly ActivitySource activitySource = new(KautukaActivitySources.GatewayAgentRunSource);

    internal readonly record struct AgentRunTraceContext(string RunId, string TraceParent, string? TraceState);

    public static AgentRequestContext? CaptureCurrentRequestContext()
    {
        Activity? current = FindActiveAgentRunActivity() ?? FindExportedParentActivity();
        if (string.IsNullOrWhiteSpace(current?.Id))
        {
            return null;
        }

        return new AgentRequestContext(new AgentRequestTraceContext(
            current.Id,
            current.TraceStateString,
            current.TraceId.ToString(),
            current.SpanId.ToString()));
    }

    public static void RestoreCurrent(Activity? runActivity)
    {
        if (runActivity is { IsStopped: false })
        {
            Activity.Current = runActivity;
        }
    }

    public static AgentRunTraceContext? CaptureRunTraceContext(Activity? runActivity, string runId)
    {
        RestoreCurrent(runActivity);
        if (runActivity is not { IsStopped: false }
            || string.IsNullOrWhiteSpace(runId)
            || string.IsNullOrWhiteSpace(runActivity.Id))
        {
            return null;
        }

        return new AgentRunTraceContext(runId, runActivity.Id, runActivity.TraceStateString);
    }

    public static AgentRequestContext? CreateRequestContext(string? traceParent, string? traceState)
    {
        if (string.IsNullOrWhiteSpace(traceParent)
            || !ActivityContext.TryParse(traceParent, traceState, out ActivityContext parentContext))
        {
            return null;
        }

        return new AgentRequestContext(new AgentRequestTraceContext(
            traceParent,
            traceState,
            parentContext.TraceId.ToString(),
            parentContext.SpanId.ToString()));
    }

    public static bool TryParseRequestTraceContext(AgentRequestTraceContext? traceContext, out ActivityContext parentContext)
    {
        if (traceContext is null
            || string.IsNullOrWhiteSpace(traceContext.TraceParent)
            || !ActivityContext.TryParse(traceContext.TraceParent, traceContext.TraceState, out parentContext))
        {
            parentContext = default;
            return false;
        }

        return true;
    }

    public static void MarkAgentRun(Activity? runActivity, string runId, string? tenantId = null, string? userId = null)
    {
        if (runActivity is null)
        {
            return;
        }

        runActivity.AddBaggage(agentRunScopeBaggageKey, bool.TrueString);
        if (!string.IsNullOrWhiteSpace(runId))
        {
            runActivity.AddBaggage(runIdBaggageKey, runId);
        }

        AddIdentity(runActivity, KautukaActivitySources.TenantIdAttribute, tenantId);
        AddIdentity(runActivity, KautukaActivitySources.EndUserIdAttribute, userId);
    }

    public static Activity? StartGrpcClientActivity(string hostKind, string methodName, string? address)
    {
        Activity? parentActivity = Activity.Current;
        if (!string.Equals(parentActivity?.GetBaggageItem(agentRunScopeBaggageKey), bool.TrueString, StringComparison.Ordinal))
        {
            return null;
        }

        Activity? activity = activitySource.StartActivity($"{hostKind} grpc {methodName}", ActivityKind.Client);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag("kautuka.host.kind", hostKind);
        activity.SetTag("kautuka.agent_run_scoped", true);
        activity.SetTag("rpc.system", "grpc");
        activity.SetTag("rpc.service", hostKind);
        activity.SetTag("rpc.method", methodName);

        if (!string.IsNullOrWhiteSpace(address))
        {
            activity.SetTag("server.address", address);
        }

        string? runId = parentActivity?.GetBaggageItem(runIdBaggageKey);
        if (!string.IsNullOrWhiteSpace(runId))
        {
            activity.SetTag("kautuka.run.id", runId);
        }

        return activity;
    }

    public static Activity? StartScopedInternalActivity(string operationName)
    {
        Activity? parentActivity = Activity.Current;
        if (!string.Equals(parentActivity?.GetBaggageItem(agentRunScopeBaggageKey), bool.TrueString, StringComparison.Ordinal))
        {
            return null;
        }

        Activity? activity = activitySource.StartActivity(operationName, ActivityKind.Internal);
        if (activity is null)
        {
            return null;
        }

        activity.SetTag("kautuka.agent_run_scoped", true);
        string? runId = parentActivity?.GetBaggageItem(runIdBaggageKey);
        if (!string.IsNullOrWhiteSpace(runId))
        {
            activity.SetTag("kautuka.run.id", runId);
        }

        return activity;
    }

    public static AgentRunTraceContext? CaptureCurrentRunTraceContext()
    {
        Activity? current = FindActiveAgentRunActivity();
        if (current is null || string.IsNullOrWhiteSpace(current.Id))
        {
            return null;
        }

        string? runId = current.GetBaggageItem(runIdBaggageKey);
        if (string.IsNullOrWhiteSpace(runId))
        {
            return null;
        }

        return new AgentRunTraceContext(runId, current.Id, current.TraceStateString);
    }

    public static AgentRunTraceContext? CaptureCurrentRunTraceContext(string runId)
    {
        Activity? current = FindActiveAgentRunActivity();
        if (string.IsNullOrWhiteSpace(runId) || current is null || string.IsNullOrWhiteSpace(current.Id))
        {
            return null;
        }

        return new AgentRunTraceContext(runId, current.Id, current.TraceStateString);
    }

    public static Activity? StartBackgroundDispatchScope(
        string operationName,
        string? runId,
        string? traceParent,
        string? traceState)
    {
        if (string.IsNullOrWhiteSpace(runId)
            || string.IsNullOrWhiteSpace(traceParent)
            || !ActivityContext.TryParse(traceParent, traceState, out ActivityContext parentContext))
        {
            return null;
        }

        Activity? activity = activitySource.StartActivity(operationName, ActivityKind.Internal, parentContext);
        if (activity is null)
        {
            return null;
        }

        activity.AddBaggage(agentRunScopeBaggageKey, bool.TrueString);
        activity.AddBaggage(runIdBaggageKey, runId);
        activity.SetTag("kautuka.agent_run_scoped", true);
        activity.SetTag("kautuka.run.id", runId);
        activity.SetTag("kautuka.dispatch.kind", "background");
        return activity;
    }

    public static void MarkSuccess(Activity? activity)
        => activity?.SetStatus(ActivityStatusCode.Ok);

    public static void MarkFailure(Activity? activity, Exception exception)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        activity.SetTag("error.type", exception.GetType().FullName ?? exception.GetType().Name);
    }

    private static Activity? FindActiveAgentRunActivity()
    {
        for (Activity? current = Activity.Current; current is not null; current = current.Parent)
        {
            if (string.Equals(current.GetBaggageItem(agentRunScopeBaggageKey), bool.TrueString, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(current.Id))
            {
                return current;
            }
        }

        return null;
    }

    private static Activity? FindExportedParentActivity()
    {
        Activity? current = Activity.Current;
        if (current is not null
            && !string.IsNullOrWhiteSpace(current.Id)
            && exportedParentSources.Contains(current.Source.Name))
        {
            return current;
        }

        return null;
    }

    private static void AddIdentity(Activity activity, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string normalizedValue = value.Trim();
        activity.AddBaggage(key, normalizedValue);
        activity.SetTag(key, normalizedValue);
    }
}
