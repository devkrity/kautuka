using System.Text.Json;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Hitl;
using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.IntegrationTests;

public sealed class NaradInterruptObservationTests
{
    [Fact]
    public void ParseReadsInterruptRequestedFieldsDirectly()
    {
        NaradPersistedEvent persistedEvent = NaradStreamObservation.CreateSynthetic(
            "evt-interrupt",
            NaradEventTypes.InterruptRequested,
            "session-1",
            "run-1",
            "run-1",
            NaradEventBodies.InterruptRequested(
                "approval-1",
                "wait:approval-1",
                "approval",
                ["approve", "deny"],
                toolCallId: "tool-call-1",
                input: JsonDocument.Parse("""{"toolCallId":"tool-call-1","args":{}}""").RootElement));

        NaradInterruptRequestedFields fields = NaradInterruptObservation.Parse(persistedEvent);

        Assert.Equal("approval-1", fields.InterruptId);
        Assert.Equal("wait:approval-1", fields.WaitId);
        Assert.Equal("approval", fields.Kind);
        Assert.Equal("tool-call-1", fields.ToolCallId);
    }

    [Fact]
    public void TryBuildChatApprovalRequestUsesApprovalStoreMetadata()
    {
        var store = new InterruptObservationApprovalStore(new GatewayToolApprovalRequest(
            "approval-1",
            "session-1",
            "run-1",
            null,
            "agent-1",
            "user-1",
            "tool-call-1",
            "run_skill",
            "mcp",
            null,
            "{}",
            "2026-08-05T00:00:00Z",
            null,
            null,
            null,
            null,
            null));

        NaradPersistedEvent persistedEvent = NaradStreamObservation.CreateSynthetic(
            "evt-interrupt",
            NaradEventTypes.InterruptRequested,
            "session-1",
            "run-1",
            "run-1",
            NaradEventBodies.InterruptRequested("approval-1", "wait:approval-1", "approval", ["approve", "deny"]));

        NaradInterruptRequestedFields fields = NaradInterruptObservation.Parse(persistedEvent);
        GatewayChatApprovalRequest? resolved = NaradInterruptObservation.TryBuildChatApprovalRequest(fields, store);

        Assert.NotNull(resolved);
        Assert.Equal("approval-1", resolved!.RequestId);
        Assert.Equal("tool-call-1", resolved.ToolCallId);
        Assert.Equal("run_skill", resolved.ToolName);
    }

    [Fact]
    public void ResolveChatApprovalRequestUsesCanonicalInterruptBody()
    {
        var store = new InterruptObservationApprovalStore(new GatewayToolApprovalRequest(
            "approval-1",
            "session-1",
            "run-1",
            null,
            "agent-roller",
            "user-1",
            "tool-call-1",
            "run_skill_script",
            "skill",
            null,
            "{}",
            "2026-08-05T00:00:00Z",
            null,
            null,
            null,
            null,
            null));

        NaradPersistedEvent persistedEvent = NaradStreamObservation.CreateSynthetic(
            "evt-interrupt",
            NaradEventTypes.InterruptRequested,
            "session-1",
            "run-1",
            "run-1",
            NaradEventBodies.InterruptRequested(
                "approval-1",
                "wait:approval-1",
                "approval",
                ["approve", "deny"],
                toolCallId: "tool-call-1"));

        GatewayChatApprovalRequest resolved = NaradInterruptObservation.ResolveChatApprovalRequest(
            persistedEvent,
            store,
            fallbackAgentId: "agent-roller",
            fallbackSessionId: "session-1");

        Assert.Equal("approval-1", resolved.RequestId);
        Assert.Equal("tool-call-1", resolved.ToolCallId);
        Assert.Equal("run_skill_script", resolved.ToolName);
        Assert.Equal("agent-roller", resolved.AgentId);
        Assert.Equal("session-1", resolved.SessionId);
    }

    private sealed class InterruptObservationApprovalStore(GatewayToolApprovalRequest request) : IGatewayApprovalStore
    {
        public void CreateRequest(GatewayToolApprovalRequest request) { }
        public bool Resolve(string requestId, bool approved, string? reason, string? decidedBy, string resolutionSource, ApprovalContinuationWork? continuation = null) => false;
        public IReadOnlyList<GatewayToolApprovalRequest> ListPending(string? userId, bool adminAll) => [];
        public IReadOnlyList<GatewayToolApprovalRequest> ListHistory(string? userId, bool adminAll, int limit, int offset) => [];
        public IReadOnlyList<GatewayToolApprovalRequest> ListBySession(string sessionId) => [];
        public GatewayToolApprovalRequest? GetById(string requestId)
            => string.Equals(requestId, request.Id, StringComparison.Ordinal) ? request : null;
        public GatewayToolApprovalRequest? GetDecidedByRunAndTool(string runId, string toolCallId, string toolName) => null;
        public GatewayToolApprovalRequest? GetDecidedByRunToolAndArguments(string runId, string toolName, string? argumentsJson) => null;
        public void SetChannelTetherMode(string requestId, ChannelHitlTetherMode mode) { }
    }
}
