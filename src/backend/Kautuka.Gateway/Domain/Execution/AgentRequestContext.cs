namespace Kautuka.Gateway.Domain.Execution;

internal sealed record AgentRequestContext(
    AgentRequestTraceContext? Trace = null,
    string? RunId = null,
    string? UserMessageId = null,
    bool EarlyLifecycleEventsPublished = false);

internal sealed record AgentRequestTraceContext(
    string TraceParent,
    string? TraceState,
    string TraceId,
    string ParentSpanId);
