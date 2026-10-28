using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Hitl;

using static Kautuka.IntegrationTests.ThreadCommandTestSupport;

namespace Kautuka.IntegrationTests;

/// <summary>
/// Stop-run side effects remain durable after approval resolution.
/// Run-scoped "approve all" / bypass is intentionally not product behaviour.
/// </summary>
public sealed class ApprovalBypassDurabilityTests
{
    [Fact]
    public async Task ChannelStopResolutionPersistsDurableStop()
    {
        GatewayToolApprovalRequest request = CreateApprovalRequest();
        var approvalStore = new ThreadCommandApprovalStore(request);
        NaradPersistedEvent started = RuntimeEvent(1, NaradEventTypes.RunStarted, new { trigger = "chat" });
        var agentStore = new ThreadCommandAgentStore(started);
        var stopSignals = new RunStopRegistry();
        var stopCommands = new RunStopCommandService(
            agentStore,
            new ThreadCommandPublisher(agentStore, () => stopSignals.IsStopped("run-1")),
            stopSignals);
        var service = CreateResolutionService(approvalStore, stopCommands);

        ApprovalResolutionResult result = await service.ResolveAsync(
            new ApprovalResolutionCommand(
                request.Id,
                Approved: false,
                Reason: "stopped",
                DecidedByUserId: "user-1",
                Source: HitlResolutionSource.ChannelCallback,
                SessionId: request.SessionId,
                TargetType: "agent",
                TargetId: request.AgentId,
                UserId: request.UserId,
                StopRun: true,
                OriginatingChannelType: "telegram"),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(ApprovalResolutionStatus.Resolved, result.Status);
        Assert.Single(
            agentStore.ReadRootHistory("run-1"),
            item => item.EventType == NaradEventTypes.RunStopRequested);
    }

    private static GatewayToolApprovalRequest CreateApprovalRequest()
        => new(
            Id: "req-stop-1",
            SessionId: "session-1",
            RunId: "run-1",
            CheckpointId: null,
            AgentId: "agent-1",
            UserId: "user-1",
            ToolCallId: "tool-call-1",
            ToolName: "run_shell",
            ToolType: "mcp",
            McpServerId: null,
            ArgumentsJson: "{}",
            RequestedAt: DateTimeOffset.UtcNow.ToString("O"),
            Decision: null,
            DecidedBy: null,
            DecidedAt: null,
            Reason: null,
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5).ToString("O"));
}
