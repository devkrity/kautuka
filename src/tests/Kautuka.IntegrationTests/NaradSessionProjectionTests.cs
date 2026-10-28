using System.Globalization;
using System.Text.Json.Nodes;

using Kautuka.Gateway.RuntimePlane.Projection;

using static Kautuka.IntegrationTests.NaradTestSupport;

namespace Kautuka.IntegrationTests;

public sealed class NaradSessionProjectionTests
{
    [Fact]
    public void OverlappingRunResponsesRetainTheirStartOrderInHydratedHistory()
    {
        NaradPersistedEvent[] history =
        [
            AuthorityEvent("overlap-1", NaradEventTypes.MessageUserRecorded, "session-overlap", 1, "auto", "auto", 1, new JsonObject { ["messageId"] = "u-auto", ["content"] = "Autopilot request" }),
            AuthorityEvent("overlap-2", NaradEventTypes.MessageTextStarted, "session-overlap", 2, "auto", "auto", 2, new JsonObject { ["messageId"] = "a-auto" }),
            AuthorityEvent("overlap-3", NaradEventTypes.MessageUserRecorded, "session-overlap", 3, "telegram", "telegram", 1, new JsonObject { ["messageId"] = "u-telegram", ["content"] = "roll d956" }),
            AuthorityEvent("overlap-4", NaradEventTypes.MessageTextStarted, "session-overlap", 4, "telegram", "telegram", 2, new JsonObject { ["messageId"] = "a-telegram" }),
            AuthorityEvent("overlap-5", NaradEventTypes.MessageTextDelta, "session-overlap", 5, "auto", "auto", 3, new JsonObject { ["messageId"] = "a-auto", ["delta"] = "Autopilot answer" }),
            AuthorityEvent("overlap-6", NaradEventTypes.MessageTextCompleted, "session-overlap", 6, "auto", "auto", 4, new JsonObject { ["messageId"] = "a-auto" }),
            AuthorityEvent("overlap-7", NaradEventTypes.MessageTextDelta, "session-overlap", 7, "telegram", "telegram", 3, new JsonObject { ["messageId"] = "a-telegram", ["delta"] = "956" }),
            AuthorityEvent("overlap-8", NaradEventTypes.MessageTextCompleted, "session-overlap", 8, "telegram", "telegram", 4, new JsonObject { ["messageId"] = "a-telegram" }),
        ];
        JsonObject snapshot = NaradSessionProjection.BuildMessagesSnapshot("session-overlap", history, DateTimeOffset.UtcNow);
        JsonArray messages = snapshot["messages"]!.AsArray();
        Assert.Equal("u-auto", messages[0]!["messageId"]!.GetValue<string>());
        Assert.Equal("a-auto", messages[1]!["messageId"]!.GetValue<string>());
        Assert.Equal("Autopilot answer", messages[1]!["content"]!.GetValue<string>());
        Assert.Equal("u-telegram", messages[2]!["messageId"]!.GetValue<string>());
        Assert.Equal("a-telegram", messages[3]!["messageId"]!.GetValue<string>());
        Assert.Equal(8, snapshot["coversThroughSessionSequence"]!.GetValue<long>());
    }

    [Fact]
    public void GraphProjectionIsDeterministicAcrossRebuilds()
    {
        NaradPersistedEvent[] history =
        [
            AuthorityEvent("evt-1", NaradEventTypes.RunStarted, "session-graph", 1, "run-graph", "run-graph", 1, new JsonObject { ["trigger"] = "chat" }),
            AuthorityEvent("evt-3", NaradEventTypes.ToolCallStarted, "session-graph", 2, "run-graph", "run-graph", 2, new JsonObject
            {
                ["toolCallId"] = "tool-1",
                ["toolName"] = "search",
            }),
            AuthorityEvent("evt-5", NaradEventTypes.ToolCallFinished, "session-graph", 3, "run-graph", "run-graph", 3, new JsonObject
            {
                ["toolCallId"] = "tool-1",
                ["outcome"] = "success",
            }),
            AuthorityEvent("evt-6", NaradEventTypes.RunFinished, "session-graph", 4, "run-graph", "run-graph", 4),
        ];

        NaradGraphProjectionState first = NaradGraphProjectionBuilder.Build("run-graph", history);
        NaradGraphProjectionState second = NaradGraphProjectionBuilder.Build("run-graph", history);

        JsonObject firstSnapshot = first.BuildGraphSnapshot("session-graph", DateTimeOffset.Parse("2026-08-05T00:00:01Z", CultureInfo.InvariantCulture));
        JsonObject secondSnapshot = second.BuildGraphSnapshot("session-graph", DateTimeOffset.Parse("2026-08-05T00:00:01Z", CultureInfo.InvariantCulture));

        Assert.Equal(firstSnapshot["rootNodeId"]!.GetValue<string>(), secondSnapshot["rootNodeId"]!.GetValue<string>());
        Assert.Equal(firstSnapshot["nodes"]!.ToJsonString(), secondSnapshot["nodes"]!.ToJsonString());
        Assert.Equal("node-tool-1", firstSnapshot["nodes"]![1]!["id"]!.GetValue<string>());
    }

    [Fact]
    public void SessionProjectionTracksPendingInterruptsAndClearsOnResolve()
    {
        NaradPersistedEvent[] history =
        [
            AuthorityEvent("evt-1", NaradEventTypes.RunStarted, "session-1", 1, "run-1", "run-1", 1),
            AuthorityEvent("evt-2", NaradEventTypes.InterruptRequested, "session-1", 2, "run-1", "run-1", 2, new JsonObject
            {
                ["interruptId"] = "req-1",
                ["waitId"] = "wait:req-1",
                ["toolCallId"] = "tool-1",
            }),
            AuthorityEvent("evt-3", NaradEventTypes.InterruptResolved, "session-1", 3, "run-1", "run-1", 3, new JsonObject
            {
                ["interruptId"] = "req-1",
                ["decision"] = "approve",
            }),
        ];

        NaradSessionProjection projection = NaradSessionProjection.Fold("session-1", history);
        Assert.Empty(projection.PendingInterrupts);
        Assert.Equal(NaradEventTypes.RunStarted, projection.ActiveRun?.Lifecycle);
    }

    [Fact]
    public void MessagesSnapshotHydratesUserAndAssistantTranscript()
    {
        NaradPersistedEvent[] history =
        [
            AuthorityEvent("evt-1", NaradEventTypes.MessageUserRecorded, "session-1", 1, body: new JsonObject
            {
                ["messageId"] = "user-1",
                ["content"] = "hello",
            }),
            AuthorityEvent("evt-2", NaradEventTypes.MessageTextStarted, "session-1", 2, body: new JsonObject
            {
                ["messageId"] = "assistant-1",
                ["role"] = "assistant",
            }),
            AuthorityEvent("evt-3", NaradEventTypes.MessageTextDelta, "session-1", 3, body: new JsonObject
            {
                ["messageId"] = "assistant-1",
                ["delta"] = "hi",
            }),
            AuthorityEvent("evt-4", NaradEventTypes.MessageTextCompleted, "session-1", 4, body: new JsonObject
            {
                ["messageId"] = "assistant-1",
            }),
        ];

        JsonObject snapshot = NaradSessionProjection.BuildMessagesSnapshot("session-1", history, DateTimeOffset.Parse("2026-08-05T00:00:00Z", CultureInfo.InvariantCulture));
        Assert.Equal("messages.snapshot", snapshot["type"]!.GetValue<string>());
        Assert.Equal(2, snapshot["messages"]!.AsArray().Count);
        Assert.Equal("hi", snapshot["messages"]![1]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void MessagesSnapshotPreservesUserAttachmentIds()
    {
        NaradPersistedEvent[] history =
        [
            AuthorityEvent("evt-1", NaradEventTypes.MessageUserRecorded, "session-1", 1, body: new JsonObject
            {
                ["messageId"] = "user-1",
                ["content"] = "",
                ["attachmentIds"] = new JsonArray("asset-1", "asset-2"),
            }),
        ];

        JsonObject snapshot = NaradSessionProjection.BuildMessagesSnapshot("session-1", history, DateTimeOffset.Parse("2026-08-05T00:00:00Z", CultureInfo.InvariantCulture));
        JsonArray attachmentIds = snapshot["messages"]![0]!["attachmentIds"]!.AsArray();
        Assert.Equal(["asset-1", "asset-2"], attachmentIds.Select(static item => item!.GetValue<string>()));
    }

    [Fact]
    public void MessagesSnapshotStampsRunLineageForTurnReloads()
    {
        NaradPersistedEvent[] history =
        [
            AuthorityEvent(
                "evt-1",
                NaradEventTypes.MessageUserRecorded,
                "session-1",
                1,
                "run-reload",
                "run-reload",
                1,
                body: new JsonObject
                {
                    ["messageId"] = "user-1",
                    ["content"] = "hello",
                }),
            AuthorityEvent(
                "evt-2",
                NaradEventTypes.MessageTextStarted,
                "session-1",
                2,
                "run-reload",
                "run-reload",
                2,
                body: new JsonObject
                {
                    ["messageId"] = "assistant-1",
                    ["role"] = "assistant",
                }),
            AuthorityEvent(
                "evt-3",
                NaradEventTypes.MessageTextDelta,
                "session-1",
                3,
                "run-reload",
                "run-reload",
                3,
                body: new JsonObject
                {
                    ["messageId"] = "assistant-1",
                    ["delta"] = "done",
                }),
            AuthorityEvent(
                "evt-4",
                NaradEventTypes.MessageTextCompleted,
                "session-1",
                4,
                "run-reload",
                "run-reload",
                4,
                body: new JsonObject
                {
                    ["messageId"] = "assistant-1",
                }),
        ];

        JsonObject snapshot = NaradSessionProjection.BuildMessagesSnapshot(
            "session-1",
            history,
            DateTimeOffset.Parse("2026-08-05T00:00:00Z", CultureInfo.InvariantCulture));

        Assert.Equal("run-reload", snapshot["messages"]![0]!["rootRunId"]!.GetValue<string>());
        Assert.Equal("run-reload", snapshot["messages"]![1]!["rootRunId"]!.GetValue<string>());
        Assert.Equal("run-reload", snapshot["messages"]![1]!["runId"]!.GetValue<string>());
    }

    [Fact]
    public void GraphReplayMatchesIncrementalLiveApply()
    {
        NaradPersistedEvent[] history =
        [
            AuthorityEvent("evt-1", NaradEventTypes.RunStarted, "session-graph", 1, "run-graph", "run-graph", 1, new JsonObject { ["trigger"] = "chat" }),
            AuthorityEvent("evt-2", NaradEventTypes.ToolCallStarted, "session-graph", 2, "run-graph", "run-graph", 2, new JsonObject
            {
                ["toolCallId"] = "tool-1",
                ["toolName"] = "search",
            }),
            AuthorityEvent("evt-3", NaradEventTypes.RunPaused, "session-graph", 3, "run-graph", "run-graph", 3),
            AuthorityEvent("evt-4", NaradEventTypes.RunResumed, "session-graph", 4, "run-graph", "run-graph", 4),
            AuthorityEvent("evt-5", NaradEventTypes.RunFinished, "session-graph", 5, "run-graph", "run-graph", 5),
        ];

        NaradGraphProjectionState replay = NaradGraphProjectionBuilder.Build("run-graph", history);
        var live = new NaradGraphProjectionState("run-graph");
        NaradGraphApplyResult? lastApply = null;
        foreach (NaradPersistedEvent persistedEvent in history)
        {
            lastApply = live.Apply(persistedEvent);
        }

        JsonObject replaySnapshot = replay.BuildGraphSnapshot("session-graph", DateTimeOffset.Parse("2026-08-05T00:00:01Z", CultureInfo.InvariantCulture));
        JsonObject liveSnapshot = live.BuildGraphSnapshot("session-graph", DateTimeOffset.Parse("2026-08-05T00:00:01Z", CultureInfo.InvariantCulture));

        Assert.Equal(replay.Revision, live.Revision);
        Assert.Equal(replaySnapshot["nodes"]!.ToJsonString(), liveSnapshot["nodes"]!.ToJsonString());
        Assert.Equal(replaySnapshot["edges"]!.ToJsonString(), liveSnapshot["edges"]!.ToJsonString());
        Assert.NotNull(lastApply);
        Assert.Equal(replay.Revision, lastApply.Revision);
    }

    [Fact]
    public void GraphPatchIndicesMatchInsertionOrder()
    {
        var graph = new NaradGraphProjectionState("run-graph");
        graph.Apply(AuthorityEvent(
            "evt-1",
            NaradEventTypes.RunStarted,
            "session-graph",
            1,
            "run-graph",
            "run-graph",
            1,
            new JsonObject { ["trigger"] = "chat" }));
        NaradGraphApplyResult? paused = graph.Apply(AuthorityEvent(
            "evt-2",
            NaradEventTypes.RunPaused,
            "session-graph",
            2,
            "run-graph",
            "run-graph",
            2));

        Assert.NotNull(paused);
        Assert.Contains(paused.Patch, patch => patch["path"]!.GetValue<string>() == "/nodes");
        Assert.Contains(paused.Patch, patch => patch["op"]!.GetValue<string>() == "replace");
    }

    [Fact]
    public void GraphStatusPatchCanSerializeNodesMoreThanOnce()
    {
        // Regression: SerializeNodes put dictionary JsonObjects into a JsonArray. A second
        // PatchNodeStatus / BuildGraphSnapshot then threw "The node already has a parent"
        // and the stream endpoint returned 500 during fold after interrupt.resolved.
        var graph = new NaradGraphProjectionState("run-graph");
        graph.Apply(AuthorityEvent(
            "evt-1",
            NaradEventTypes.RunStarted,
            "session-graph",
            1,
            "run-graph",
            "run-graph",
            1,
            new JsonObject { ["trigger"] = "chat" }));
        graph.Apply(AuthorityEvent(
            "evt-2",
            NaradEventTypes.InterruptRequested,
            "session-graph",
            2,
            "run-graph",
            "run-graph",
            2,
            new JsonObject
            {
                ["interruptId"] = "approval-1",
                ["waitId"] = "wait:approval-1",
                ["kind"] = "approval",
                ["toolCallId"] = "call-1",
                ["toolName"] = "invoke_agent_roller",
            }));
        NaradGraphApplyResult? resolved = graph.Apply(AuthorityEvent(
            "evt-3",
            NaradEventTypes.InterruptResolved,
            "session-graph",
            3,
            "run-graph",
            "run-graph",
            3,
            new JsonObject
            {
                ["interruptId"] = "approval-1",
                ["waitId"] = "wait:approval-1",
                ["decision"] = "approved",
            }));

        Assert.NotNull(resolved);
        Assert.Contains(resolved.Patch, patch => patch["path"]!.GetValue<string>() == "/nodes");

        // Snapshot must also succeed after status patches owned SerializeNodes arrays.
        JsonObject snapshot = graph.BuildGraphSnapshot("session-graph", DateTimeOffset.Parse("2026-08-05T00:00:00Z", CultureInfo.InvariantCulture));
        Assert.True(snapshot["nodes"] is JsonArray nodes && nodes.Count >= 2);
    }

    [Fact]
    public void MessagesSnapshotIncludesInProgressAssistantText()
    {
        NaradPersistedEvent[] history =
        [
            AuthorityEvent("evt-1", NaradEventTypes.MessageTextStarted, "session-1", 1, body: new JsonObject
            {
                ["messageId"] = "assistant-1",
                ["role"] = "assistant",
            }),
            AuthorityEvent("evt-2", NaradEventTypes.MessageTextDelta, "session-1", 2, body: new JsonObject
            {
                ["messageId"] = "assistant-1",
                ["delta"] = "partial",
            }),
        ];

        JsonObject snapshot = NaradSessionProjection.BuildMessagesSnapshot("session-1", history, DateTimeOffset.Parse("2026-08-05T00:00:00Z", CultureInfo.InvariantCulture));
        JsonObject message = snapshot["messages"]![0]!.AsObject();
        Assert.Equal("partial", message["content"]!.GetValue<string>());
        Assert.False(message["completed"]!.GetValue<bool>());
    }

    [Fact]
    public void GraphProjectionAppliesAaatParentSessionLaneRowsFromEventJson()
    {
        NaradPersistedEvent[] history =
        [
            AuthorityEvent("evt-1", NaradEventTypes.RunStarted, "session-parent", 1, "run-root", "run-root", 1, new JsonObject { ["trigger"] = "chat" }),
            AuthorityEvent("evt-2", NaradEventTypes.ToolCallStarted, "session-parent", 2, "run-root", "run-root", 2, new JsonObject
            {
                ["toolCallId"] = "invoke-1",
                ["toolName"] = "invoke_agent_child",
            }),
            ParentProjectionEvent("aaatproj:child-run", NaradEventTypes.RunStarted, "session-parent", 3, "evt-child-run", "session-child", "run-root", "run-child", new JsonObject
            {
                ["trigger"] = "agent_tool",
                ["parentRunId"] = "run-root",
                ["parentToolCallId"] = "invoke-1",
            }),
        ];

        NaradSessionProjection projection = NaradSessionProjection.Fold("session-parent", history);
        NaradGraphProjectionState? graph = projection.GetGraph("run-root");
        Assert.NotNull(graph);
        Assert.True(graph.Revision > 0);
        Assert.Contains(projection.EnumerateGraphs(), static g => string.Equals(g.RootRunId, "run-root", StringComparison.Ordinal));

        JsonObject snapshot = graph.BuildGraphSnapshot("session-parent", DateTimeOffset.Parse("2026-08-05T00:00:01Z", CultureInfo.InvariantCulture));
        Assert.Contains("node-run-run-child", snapshot["nodes"]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void GraphApplyRequiresSnapshotUntilExplicitlySentEvenAfterManyPatches()
    {
        NaradPersistedEvent[] history =
        [
            AuthorityEvent("evt-1", NaradEventTypes.RunStarted, "session-graph", 1, "run-graph", "run-graph", 1, new JsonObject { ["trigger"] = "chat" }),
            AuthorityEvent("evt-2", NaradEventTypes.ToolCallStarted, "session-graph", 2, "run-graph", "run-graph", 2, new JsonObject
            {
                ["toolCallId"] = "tool-1",
                ["toolName"] = "search",
            }),
        ];

        var graph = new NaradGraphProjectionState("run-graph");
        NaradGraphApplyResult? first = graph.Apply(history[0]);
        Assert.NotNull(first);
        Assert.True(first.RequiresInitialSnapshot);
        Assert.Equal(1, first.Revision);

        NaradGraphApplyResult? second = graph.Apply(history[1]);
        Assert.NotNull(second);
        // Fold-style multi-apply without BuildGraphSnapshot must not invent wire deltas.
        Assert.True(second.RequiresInitialSnapshot);
        Assert.Equal(2, second.Revision);

        _ = graph.BuildGraphSnapshot("session-graph", DateTimeOffset.Parse("2026-08-05T00:00:01Z", CultureInfo.InvariantCulture));
        NaradGraphApplyResult? third = graph.Apply(AuthorityEvent(
            "evt-3",
            NaradEventTypes.ToolCallFinished,
            "session-graph",
            3,
            "run-graph",
            "run-graph",
            3,
            new JsonObject
            {
                ["toolCallId"] = "tool-1",
                ["outcome"] = "success",
            }));
        Assert.NotNull(third);
        Assert.False(third.RequiresInitialSnapshot);
        Assert.Equal(2, third.BaseRevision);
        Assert.Equal(3, third.Revision);
    }

    [Fact]
    public void MalformedEventJsonFailsClosedInParser()
    {
        var broken = new NaradPersistedEvent(
            "evt-bad",
            NaradProtocolVersions.V1,
            NaradEventTypes.RunStarted,
            "session-1",
            1,
            NaradRecordClasses.Authority,
            "{not-json",
            "2026-08-05T00:00:00Z",
            "run-1",
            "run-1",
            1,
            null,
            null);

        Assert.Throws<NaradProjectionException>(() => NaradEventJsonParser.ParseBody(broken));
    }
}
