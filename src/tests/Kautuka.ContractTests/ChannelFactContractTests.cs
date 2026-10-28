using System.Text.Json;

using Kautuka.ChannelContracts.Channels;
using Kautuka.ChannelContracts.Serialization;

namespace Kautuka.ContractTests;

public sealed class ChannelFactContractTests
{
    [Fact]
    public void SessionFactsEnvelopeRoundTripPreservesDomainFactsAndGatewayActions()
    {
        var selected = new ChannelSelectedSessionFact(
            ChannelFactContractVersions.SelectedSession, "chat-1", "topic-1", "session-1", "Invoices", "Assistant", 42,
            "agent", "agent-1");
        var active = new ChannelActiveSessionsFact(
            ChannelFactContractVersions.ActiveSessions, "chat-1", "topic-1", 42,
            [new ChannelActiveSessionEntry("session-1", "Invoices", "waiting for approval")]);
        var queue = new ChannelQueueFact(
            ChannelFactContractVersions.Queue, "chat-1", "topic-1", 42,
            1,
            [new ChannelQueueEntry("turn-1", "session-1", 1, "Send the report")]);
        ChannelCardActionSeed[] actionSeeds =
        [
            new("remove-turn-1", "remove", "session-1", "turn-1", null, "ui:opaque-token"),
            new("new-session", "new", null, null, null, "sessions:new"),
        ];
        var envelope = new ChannelSessionFactsEnvelope(
            ChannelFactContractVersions.SessionFactsEnvelope, "chat-1", "topic-1", "summary", 0, true, 42,
            selected, active, queue, actionSeeds);

        string json = JsonSerializer.Serialize(envelope, ChannelContractsJsonContext.Default.ChannelSessionFactsEnvelope);
        ChannelSessionFactsEnvelope? actual = JsonSerializer.Deserialize(json, ChannelContractsJsonContext.Default.ChannelSessionFactsEnvelope);

        Assert.NotNull(actual);
        Assert.Equal(envelope.Version, actual.Version);
        Assert.Equal(envelope.View, actual.View);
        Assert.Equal(envelope.Revision, actual.Revision);
        Assert.Equal(envelope.ActionsSeed, actual.ActionsSeed);
        Assert.Equal("agent-1", actual.Selected?.TargetId);
        Assert.Equal("waiting for approval", actual.Active?.Sessions.Single().RunLabel);
        Assert.Equal("Send the report", actual.QueueFacts?.Queue.Single().Preview);
        Assert.Equal("ui:opaque-token", actual.ActionsSeed[0].Token);
    }

    [Fact]
    public void ApprovalAndPromptFactsRoundTripWithoutProviderPresentationFields()
    {
        var request = new ChannelApprovalRequestFact(
            ChannelFactContractVersions.ApprovalRequest, "chat-1", "topic-1", "request-1", "run_skill_script",
            "{\"sides\":44}", "roll a d44", "session-1", "root-run-1", "roll a d44", "interaction-1", DateTimeOffset.UtcNow);
        var resolution = new ChannelApprovalResolutionFact(
            ChannelFactContractVersions.ApprovalResolution, "chat-1", "topic-1", "request-1", "approved", "user-1", "by admin", "123");
        var provenance = new ChannelPromptProvenanceFact(
            ChannelFactContractVersions.PromptProvenance, "chat-1", "topic-1", "session-1", "root-run-1", "roll a d44", "456");

        ChannelApprovalRequestFact? requestCopy = RoundTrip(request, ChannelContractsJsonContext.Default.ChannelApprovalRequestFact);
        ChannelApprovalResolutionFact? resolutionCopy = RoundTrip(resolution, ChannelContractsJsonContext.Default.ChannelApprovalResolutionFact);
        ChannelPromptProvenanceFact? provenanceCopy = RoundTrip(provenance, ChannelContractsJsonContext.Default.ChannelPromptProvenanceFact);

        Assert.Equal(request, requestCopy);
        Assert.Equal(resolution, resolutionCopy);
        Assert.Equal(provenance, provenanceCopy);
    }

    [Fact]
    public void UnknownFieldsAreIgnoredForForwardCompatibleSelectedSessionFacts()
    {
        const string json = """
            {
              "version":"kautuka.selected-session/v1",
              "conversationId":"chat-1",
              "conversationThreadId":"topic-1",
              "sessionId":"session-1",
              "sessionLabel":"Invoices",
              "agentDisplayName":"Assistant",
              "revision":3,
              "targetType":"agent",
              "targetId":"agent-1",
              "futureField":"ignored"
            }
            """;

        ChannelSelectedSessionFact? fact = JsonSerializer.Deserialize(json, ChannelContractsJsonContext.Default.ChannelSelectedSessionFact);

        Assert.NotNull(fact);
        Assert.Equal("session-1", fact.SessionId);
        Assert.Equal("agent-1", fact.TargetId);
    }

    private static T? RoundTrip<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        string json = JsonSerializer.Serialize(value, typeInfo);
        return JsonSerializer.Deserialize(json, typeInfo);
    }
}
