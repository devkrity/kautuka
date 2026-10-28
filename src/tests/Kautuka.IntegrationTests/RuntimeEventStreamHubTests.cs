using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Execution;

namespace Kautuka.IntegrationTests;

public sealed class RuntimeEventStreamHubTests
{
    [Fact]
    public async Task SessionSubscriberReceivesEarlyLifecycleEventsOnPublish()
    {
        var hub = new RuntimeEventStreamHub();
        using RuntimeEventStreamSubscription subscription = hub.SubscribeForSession("session-1");
        NaradEventContext context = new("agent-1", "session-1", "user-1", "run-1", "run-1", SourceChannel: "narad");

        hub.Publish(CreateEvent(NaradEventTypes.RunStarted, context, sessionSequence: 1, rootSequence: 1));
        hub.Publish(CreateEvent(
            NaradEventTypes.MessageUserRecorded,
            context,
            sessionSequence: 2,
            rootSequence: 2,
            body: NaradEventBodies.MessageUserRecorded(
                NaradEventBodies.BuildUserMessageId("run-1", "run-1"),
                "hello")));

        NaradPersistedEvent first = await subscription.Reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        NaradPersistedEvent second = await subscription.Reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.Equal(NaradEventTypes.RunStarted, first.EventType);
        Assert.Equal(NaradEventTypes.MessageUserRecorded, second.EventType);
        Assert.Equal("session-1", first.SessionId);
        Assert.Equal("session-1", second.SessionId);
    }

    [Fact]
    public async Task SessionSubscriberReceivesConversationEventsWithoutTerminalBoundary()
    {
        var hub = new RuntimeEventStreamHub();
        using RuntimeEventStreamSubscription subscription = hub.SubscribeForSession("session-1");
        NaradEventContext context = new("agent-1", "session-1", "user-1", "run-1", "run-1", SourceChannel: "narad");

        hub.Publish(CreateEvent(
            NaradEventTypes.MessageTextDelta,
            context,
            sessionSequence: 3,
            rootSequence: 3,
            body: NaradEventBodies.MessageTextDelta("msg-1", "hi")));

        NaradPersistedEvent delivered = await subscription.Reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(NaradEventTypes.MessageTextDelta, delivered.EventType);
    }

    [Fact]
    public async Task SessionSubscribersOnlyReceiveEventsForTheirSessionId()
    {
        var hub = new RuntimeEventStreamHub();
        using RuntimeEventStreamSubscription parentSubscription = hub.SubscribeForSession("parent-session");
        using RuntimeEventStreamSubscription childSubscription = hub.SubscribeForSession("child-session");
        NaradEventContext context = new(
            "agent-1",
            "child-session",
            "user-1",
            "run-child",
            "run-root",
            SourceChannel: "narad");
        JsonElement body = JsonSerializer.SerializeToElement(
            new JsonObject
            {
                ["parentSessionId"] = "parent-session",
            },
            KautukaJsonContext.Default.JsonObject);

        hub.Publish(CreateEvent(NaradEventTypes.MessageUserRecorded, context, sessionSequence: 1, rootSequence: 1, body: body));
        Assert.False(await SubscriptionHasEvent(parentSubscription));

        hub.Publish(CreateEvent(NaradEventTypes.RunPaused, context, sessionSequence: 2, rootSequence: 2, body: body));
        // Hub routes by event.SessionId only; parent conversation projection is a separate journal event.
        Assert.False(await SubscriptionHasEvent(parentSubscription));
        NaradPersistedEvent childDelivered = await childSubscription.Reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        // First event: message.user.recorded for the child
        Assert.Equal(NaradEventTypes.MessageUserRecorded, childDelivered.EventType);
        NaradPersistedEvent childPaused = await childSubscription.Reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(NaradEventTypes.RunPaused, childPaused.EventType);
    }

    [Fact]
    public async Task UserApprovalSubscriberReceivesInterruptWhenOwnerUserIdStamped()
    {
        var hub = new RuntimeEventStreamHub();
        using ApprovalInvalidationSubscription subscription = hub.SubscribeForUserApprovals("user-1");
        NaradEventContext context = new("agent-1", "session-1", "user-1", "run-1", "run-1", SourceChannel: "narad");

        JsonElement stamped = NaradEventBodies.WithOwnerUserId(
            NaradEventBodies.InterruptRequested(
                "apr-1",
                "wait:apr-1",
                "approval",
                ["approve", "deny"],
                "tool-1"),
            context.UserId);
        Assert.Contains("user-1", stamped.GetRawText(), StringComparison.Ordinal);

        hub.Publish(CreateRawEvent(
            NaradEventTypes.InterruptRequested,
            context,
            sessionSequence: 1,
            rootSequence: 1,
            body: stamped));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        ApprovalInvalidation delivered = await subscription.Reader.ReadAsync(timeout.Token).ConfigureAwait(true);
        Assert.Equal(NaradEventTypes.InterruptRequested, delivered.EventType);
        Assert.Equal("session-1", delivered.SessionId);
        Assert.Equal("run-1", delivered.RootRunId);
    }

    [Fact]
    public async Task UserApprovalSubscriberReceivesWorkflowPausedOnInstanceSession()
    {
        var hub = new RuntimeEventStreamHub();
        using ApprovalInvalidationSubscription subscription = hub.SubscribeForUserApprovals("user-1");
        const string instanceId = "wf-instance-1";
        string eventId = Guid.NewGuid().ToString("N");
        string occurredAt = DateTimeOffset.UtcNow.ToString("O");
        var envelope = new JsonObject
        {
            ["type"] = NaradEventTypes.WorkflowPaused,
            ["protocolVersion"] = "narad/v1",
            ["eventId"] = eventId,
            ["timestamp"] = occurredAt,
            ["sessionId"] = instanceId,
            ["sessionSequence"] = 1,
            ["workflowId"] = instanceId,
            ["workflowSequence"] = 1,
            ["waitId"] = "wait:apr-review",
            ["interruptId"] = "apr-review",
            ["userId"] = "user-1",
        };
        var persisted = new NaradPersistedEvent(
            eventId,
            "narad/v1",
            NaradEventTypes.WorkflowPaused,
            instanceId,
            SessionSequence: 1,
            "authority",
            envelope.ToJsonString(),
            occurredAt,
            RootRunId: null,
            RunId: null,
            Sequence: null,
            WorkflowId: instanceId,
            WorkflowSequence: 1);

        hub.Publish(persisted);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        ApprovalInvalidation delivered = await subscription.Reader.ReadAsync(timeout.Token).ConfigureAwait(true);
        Assert.Equal(NaradEventTypes.WorkflowPaused, delivered.EventType);
        Assert.Equal(instanceId, delivered.SessionId);
        Assert.Null(delivered.RootRunId);
    }

    [Fact]
    public void DraftFactoryStampsOwnerUserIdOnInterruptRequested()
    {
        NaradEventContext context = new("agent-1", "session-1", "user-1", "run-1", "run-1");
        NaradAuthorityEventDraft draft = NaradAuthorityEventDraftFactory.CreateNew(
            NaradEventTypes.InterruptRequested,
            NaradEventBodies.InterruptRequested(
                "apr-1",
                "wait:apr-1",
                "approval",
                ["approve", "deny"],
                "tool-1"),
            context);
        Assert.Equal("user-1", NaradPersistedEventReader.String(draft.Body, "userId"));
        Assert.Equal("agent-1", NaradPersistedEventReader.String(draft.Body, "agentId"));
    }

    [Fact]
    public void DraftFactoryStampsAgentIdAndSourceChannelOnMessageUserRecorded()
    {
        NaradEventContext context = new(
            "agent-surface",
            "session-1",
            "user-1",
            "run-1",
            "run-1",
            SourceChannel: "telegram");
        NaradAuthorityEventDraft draft = NaradAuthorityEventDraftFactory.CreateNew(
            NaradEventTypes.MessageUserRecorded,
            NaradEventBodies.MessageUserRecorded("msg-1", "hello"),
            context);
        Assert.Equal("agent-surface", NaradPersistedEventReader.String(draft.Body, "agentId"));
        Assert.Equal("telegram", NaradPersistedEventReader.String(draft.Body, "sourceChannel"));
    }

    [Fact]
    public async Task UserApprovalSubscriberDoesNotReceiveInterruptWithoutOwnerUserId()
    {
        var hub = new RuntimeEventStreamHub();
        using ApprovalInvalidationSubscription subscription = hub.SubscribeForUserApprovals("user-1");
        NaradEventContext context = new("agent-1", "session-1", "user-1", "run-1", "run-1", SourceChannel: "narad");

        // Simulate pre-fix journal frames that lack owner stamp.
        hub.Publish(CreateRawEvent(
            NaradEventTypes.InterruptRequested,
            context,
            sessionSequence: 1,
            rootSequence: 1,
            body: NaradEventBodies.InterruptRequested(
                "apr-1",
                "wait:apr-1",
                "approval",
                ["approve", "deny"],
                "tool-1")));

        Assert.False(await SubscriptionHasEvent(subscription));
    }

    [Fact]
    public async Task AdminApprovalSubscriberReceivesInterruptEvenWithoutUserId()
    {
        var hub = new RuntimeEventStreamHub();
        using ApprovalInvalidationSubscription subscription = hub.SubscribeForAdminApprovals();
        NaradEventContext context = new("agent-1", "session-1", "user-1", "run-1", "run-1", SourceChannel: "narad");

        hub.Publish(CreateRawEvent(
            NaradEventTypes.InterruptRequested,
            context,
            sessionSequence: 1,
            rootSequence: 1,
            body: NaradEventBodies.InterruptRequested(
                "apr-1",
                "wait:apr-1",
                "approval",
                ["approve", "deny"],
                "tool-1")));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        ApprovalInvalidation delivered = await subscription.Reader.ReadAsync(timeout.Token).ConfigureAwait(true);
        Assert.Equal(NaradEventTypes.InterruptRequested, delivered.EventType);
    }

    private static async Task<bool> SubscriptionHasEvent(RuntimeEventStreamSubscription subscription)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(100));
        try
        {
            _ = await subscription.Reader.ReadAsync(timeout.Token).ConfigureAwait(true);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<bool> SubscriptionHasEvent(ApprovalInvalidationSubscription subscription)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(100));
        try
        {
            _ = await subscription.Reader.ReadAsync(timeout.Token).ConfigureAwait(true);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static NaradPersistedEvent CreateEvent(
        string eventType,
        NaradEventContext context,
        long sessionSequence,
        long rootSequence,
        JsonElement? body = null)
        => NaradTestEventFactory.FromDraft(
            NaradAuthorityEventDraftFactory.CreateNew(
                eventType,
                body ?? JsonSerializer.SerializeToElement(new JsonObject(), KautukaJsonContext.Default.JsonObject),
                context),
            context,
            sessionSequence,
            rootSequence);

    private static NaradPersistedEvent CreateRawEvent(
        string eventType,
        NaradEventContext context,
        long sessionSequence,
        long rootSequence,
        JsonElement? body = null)
    {
        // Bypass draft factory owner stamp so hub can be tested with bare bodies.
        JsonObject envelope = new()
        {
            ["type"] = eventType,
            ["protocolVersion"] = "narad/v1",
            ["eventId"] = Guid.NewGuid().ToString("N"),
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
            ["sessionId"] = context.SessionId,
            ["sessionSequence"] = sessionSequence,
            ["rootRunId"] = context.RootRunId,
            ["runId"] = context.RunId,
            ["sequence"] = rootSequence,
        };
        if (body is { ValueKind: JsonValueKind.Object } bodyObject)
        {
            foreach (JsonProperty property in bodyObject.EnumerateObject())
            {
                if (!envelope.ContainsKey(property.Name))
                {
                    envelope[property.Name] = JsonNode.Parse(property.Value.GetRawText());
                }
            }
        }

        string eventJson = envelope.ToJsonString();
        return new NaradPersistedEvent(
            envelope["eventId"]!.GetValue<string>(),
            "narad/v1",
            eventType,
            context.SessionId,
            sessionSequence,
            "authority",
            eventJson,
            envelope["timestamp"]!.GetValue<string>(),
            context.RootRunId,
            context.RunId,
            rootSequence);
    }
}
