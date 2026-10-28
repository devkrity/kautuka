using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.RuntimePlane.Projection;

namespace Kautuka.IntegrationTests;

public sealed class NaradToolCompletionContractTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("error")]
    [InlineData("cancelled")]
    public void NewToolCompletionsRequireAnExplicitResponseMember(string outcome)
    {
        var context = new NaradEventContext("agent-1", "session-1", "owner-1", "root-1", "root-1");
        JsonElement missing = JsonSerializer.SerializeToElement(new { toolCallId = "call-1", outcome });
        NaradAuthorityEventDraft invalid = NaradAuthorityEventDraftFactory.Create("event-1", NaradEventTypes.ToolCallFinished, missing, context);
        Assert.Throws<ArgumentException>(() => NaradJournalValidation.ValidateAuthorityDraft(invalid));
        JsonElement body = AgentExecutionNaradToolBodies.ToolCallFinished("call-1", outcome);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("response").ValueKind);
        NaradJournalValidation.ValidateAuthorityDraft(NaradAuthorityEventDraftFactory.Create("event-1", NaradEventTypes.ToolCallFinished, body, context));
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("error")]
    public void HistoricalUnsuccessfulCompletionGetsNullResponseWithoutRewritingIdentityOrJournal(string outcome)
    {
        NaradPersistedEvent original = NaradTestSupport.AuthorityEvent("event-714", NaradEventTypes.ToolCallFinished,
            "session-1", 714, "root-1", "root-1", 74,
            new JsonObject { ["toolCallId"] = "call-1", ["outcome"] = outcome, ["reason"] = "cancelled" });
        string journal = original.EventJson;
        Assert.True(NaradPublicAuthorityFilter.TryBuildPublicPayload(original, out JsonObject wire));
        Assert.True(wire.ContainsKey("response"));
        Assert.Null(wire["response"]);
        Assert.Contains("\"response\":null", wire.ToJsonString(), StringComparison.Ordinal);
        Assert.Equal("event-714", wire["eventId"]!.GetValue<string>());
        Assert.Equal(714, wire["sessionSequence"]!.GetValue<int>());
        Assert.Equal("root-1", wire["runId"]!.GetValue<string>());
        Assert.Equal(outcome, wire["outcome"]!.GetValue<string>());
        Assert.Equal(journal, original.EventJson);
        Assert.False(JsonNode.Parse(original.EventJson)!.AsObject().ContainsKey("response"));
    }

    [Fact]
    public void HistoricalSuccessDoesNotGetAnInventedResultAndExistingResponseIsPreserved()
    {
        NaradPersistedEvent original = NaradTestSupport.AuthorityEvent("event-1", NaradEventTypes.ToolCallFinished,
            "session-1", 1, "root-1", "root-1", 1,
            new JsonObject { ["toolCallId"] = "call-1", ["outcome"] = "success" });
        Assert.True(NaradPublicAuthorityFilter.TryBuildPublicPayload(original, out JsonObject wire));
        Assert.False(wire.ContainsKey("response"));
        JsonObject body = JsonNode.Parse(original.EventJson)!.AsObject();
        body["outcome"] = "error";
        body["response"] = new JsonObject { ["message"] = "Tool failed" };
        Assert.True(NaradPublicAuthorityFilter.TryBuildPublicPayload(original with { EventJson = body.ToJsonString() }, out wire));
        Assert.Equal("Tool failed", wire["response"]!["message"]!.GetValue<string>());
    }
}
