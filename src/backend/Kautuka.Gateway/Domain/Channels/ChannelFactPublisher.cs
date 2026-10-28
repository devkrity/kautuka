using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Kautuka.Gateway.Domain.Execution;
using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Domain.Channels;

internal sealed partial class ChannelFactPublisher(
    IChannelOutboxStore outboxStore,
    IGatewaySessionStore sessionStore,
    IGatewayApprovalStore approvalStore,
    IChannelRoutingCoordinator routingCoordinator,
    ChannelQueueViewStore queueViews,
    SessionTurnQueueStore pendingTurns,
    GatewayState state,
    IChannelAttachmentRegistry attachments)
{
    private const int pageSize = 5;

    public Task PublishBotQueueFactsAsync(
        GatewayChannelRegistration registration,
        string chat,
        string? topic,
        IReadOnlyList<ChannelQueueEntry> queue,
        int offset,
        bool onlyIfChanged,
        bool bringToFront,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (ChannelOutboundDestination destination in ChannelOutboundDestinations.Resolve(
            attachments, registration, chat, topic))
        {
            PublishBotQueueFactsForDestination(destination.Registration, destination.ConversationId,
                destination.ConversationThreadId, queue, offset, onlyIfChanged, bringToFront);
        }
        return Task.CompletedTask;
    }

    private void PublishBotQueueFactsForDestination(
        GatewayChannelRegistration registration,
        string chat,
        string? topic,
        IReadOnlyList<ChannelQueueEntry> queue,
        int offset,
        bool onlyIfChanged,
        bool bringToFront)
    {
        offset = queue.Count == 0 ? 0 : Math.Clamp(offset, 0, (queue.Count - 1) / pageSize * pageSize);
        string scope = ChannelQueueViewStore.RegistrationScope(registration);
        string signature = $"bot-queue-v1:{offset}:" + string.Join('|', queue.Select(item =>
            $"{item.TurnId}:{item.SessionId}:{item.AgentDisplayName}:{item.AgentColor}:{item.SessionLabel}:{item.Preview}"));
        long revision = queueViews.NextQueueViewRevision(registration.OwnerUserId, scope + ":session", chat, topic,
            signature, onlyIfChanged, pageOffset: offset, view: ChannelPanelView.Queue);
        if (revision == 0) return;

        ChannelSelectedSessionFact selected = new(ChannelFactContractVersions.SelectedSession, chat, topic,
            null, null, null, revision);
        ChannelActiveSessionsFact active = new(ChannelFactContractVersions.ActiveSessions, chat, topic, revision, []);
        ChannelQueueFact queueFact = new(ChannelFactContractVersions.Queue, chat, topic, revision, queue.Count,
            queue.Skip(offset).Take(pageSize).ToArray());
        List<ChannelCardActionSeed> actions = queue.Skip(offset).Take(pageSize).Select(item => new ChannelCardActionSeed(
            "remove-" + item.TurnId,
            "remove",
            item.SessionId,
            item.TurnId,
            null,
            queueViews.CreateAction(registration.OwnerUserId, scope, chat, topic, "remove", item.SessionId, item.TurnId))).ToList();
        if (offset > 0) actions.Add(new("previous", "page", null, null, offset - pageSize, "q:view:" + (offset - pageSize)));
        if (offset + pageSize < queue.Count) actions.Add(new("next", "page", null, null, offset + pageSize, "q:view:" + (offset + pageSize)));
        ChannelSessionFactsEnvelope envelope = new("kautuka.session-facts/v1", chat, topic, "queue", offset,
            bringToFront, revision, selected, active, queueFact, actions);
        outboxStore.Enqueue(registration.ChannelType, registration.Id, "session_facts",
            JsonSerializer.Serialize(envelope, ChannelContractsJsonContext.Default.ChannelSessionFactsEnvelope),
            ownerUserId: registration.OwnerUserId);
    }

    public Task PublishSessionFactsAsync(
        GatewayChannelRegistration registration,
        GatewayChannelTargetBinding target,
        string chat,
        string? topic,
        GatewaySession? selected,
        GatewaySession[] active,
        List<QueuedSessionTurn> queue,
        int offset,
        string? addedId,
        bool onlyIfChanged,
        bool bringToFront,
        bool showQueue,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        offset = queue.Count == 0 ? 0 : Math.Clamp(offset, 0, (queue.Count - 1) / pageSize * pageSize);
        int addedPosition = addedId is null ? -1 : queue.FindIndex(turn => turn.Id == addedId);
        if (addedPosition >= 0) offset = addedPosition / pageSize * pageSize;

        string scope = ChannelQueueViewStore.RegistrationScope(registration);
        GatewaySession[] sessions = sessionStore.ListSessions(target.TargetType, target.TargetId, registration.OwnerUserId)
            .Where(session => AccessibleSession(registration.OwnerUserId, session.Id, target) is not null).ToArray();

        HashSet<string> approvals = active.Length == 0 ? [] : approvalStore.ListPending(registration.OwnerUserId, adminAll: false)
            .SelectMany(request => new[] { request.SessionId, request.OuterSessionId }).OfType<string>().ToHashSet(StringComparer.Ordinal);

        string RunLabel(GatewaySession session) => approvals.Contains(session.Id) ? "waiting for approval"
            : IsSessionBusy(target.TargetId, session.Id) ? "running" : "waiting";

        string Distinction(GatewaySession session) => sessions.Count(other => SessionLabel(other) == SessionLabel(session)) > 1
            ? " · " + session.Id[Math.Max(0, session.Id.Length - 6)..]
            : "";

        string Label(GatewaySession session) => SessionLabel(session) + Distinction(session);

        string destination = selected is null ? "Choose a session" : Label(selected);
        string signature = "session-facts-v1:" + selected?.Id + ":" + destination + ":" + target.TargetType + ":" + target.TargetId
            + ":" + routingCoordinator.ResolveTargetColor(target.TargetType, target.TargetId) + ":" + offset
            + ":" + string.Join('|', active.Select(session => session.Id + ":" + Label(session) + ":" + RunLabel(session)))
            + ":" + string.Join('|', queue.Select(turn => turn.Id));

        long revision = queueViews.NextQueueViewRevision(registration.OwnerUserId, scope + ":session", chat, topic, signature, onlyIfChanged, offset,
            showQueue ? ChannelPanelView.Queue : ChannelPanelView.Summary);

        if (revision == 0) return Task.CompletedTask;

        // Build SelectedSessionFact
        var selectedFact = new ChannelSelectedSessionFact(
            ChannelFactContractVersions.SelectedSession,
            chat,
            topic,
            selected?.Id,
            selected is not null ? Label(selected) : null,
            routingCoordinator.ResolveTargetDisplayName(target.TargetType, target.TargetId),
            revision,
            target.TargetType,
            target.TargetId,
            routingCoordinator.ResolveTargetColor(target.TargetType, target.TargetId));

        // Build ActiveSessionsFact
        var activeSessions = active.Select(session => new ChannelActiveSessionEntry(
            session.Id,
            Label(session),
            RunLabel(session))).ToArray();

        var activeFact = new ChannelActiveSessionsFact(
            ChannelFactContractVersions.ActiveSessions,
            chat,
            topic,
            revision,
            activeSessions);

        // Build QueueFact
        var queueTurns = queue.Skip(offset).Take(pageSize).Select(turn =>
        {
            string preview = JsonNode.Parse(turn.PayloadJson)?["message"]?.GetValue<string>()?.Replace('\n', ' ') ?? "Message with attachments";
            if (preview.Length > 140) preview = preview[..137] + "…";
            return new ChannelQueueEntry(turn.Id, turn.SessionId, (int)turn.Order, preview,
                routingCoordinator.ResolveTargetDisplayName(target.TargetType, target.TargetId), null,
                routingCoordinator.ResolveTargetColor(target.TargetType, target.TargetId));
        }).ToArray();

        var queueFact = new ChannelQueueFact(
            ChannelFactContractVersions.Queue,
            chat,
            topic,
            revision,
            queue.Count,
            queueTurns);

        // Build action seeds with opaque capability tokens
        var actionSeeds = new List<ChannelCardActionSeed>();

        // Remove actions for each queued turn
        foreach (QueuedSessionTurn turn in queue.Skip(offset).Take(pageSize))
        {
            string token = queueViews.CreateAction(registration.OwnerUserId, scope, chat, topic, "remove", turn.SessionId, turn.Id);
            actionSeeds.Add(new ChannelCardActionSeed(
                "remove-" + turn.Id,
                "remove",
                turn.SessionId,
                turn.Id,
                null,
                token));
        }

        // Clear actions for each active session with queue items
        foreach (GatewaySession session in active)
        {
            QueuedSessionTurn[] waiting = queue.Where(t => t.SessionId == session.Id).ToArray();
            if (waiting.Length > 0)
            {
                string token = queueViews.CreateAction(registration.OwnerUserId, scope, chat, topic, "clear", session.Id,
                    waiting[^1].Order.ToString(CultureInfo.InvariantCulture));
                actionSeeds.Add(new ChannelCardActionSeed(
                    "clear-" + session.Id,
                    "clear",
                    session.Id,
                    null,
                    null,
                    token));
            }
        }

        // Paging actions
        if (offset > 0)
        {
            string token = "q:view:" + (offset - pageSize);
            actionSeeds.Add(new ChannelCardActionSeed(
                "previous",
                "page",
                null,
                null,
                offset - pageSize,
                token));
        }
        if (offset + pageSize < queue.Count)
        {
            string token = "q:view:" + (offset + pageSize);
            actionSeeds.Add(new ChannelCardActionSeed(
                "next",
                "page",
                null,
                null,
                offset + pageSize,
                token));
        }

        // Sessions switch/new actions
        actionSeeds.Add(new ChannelCardActionSeed(
            "sessions",
            "sessions",
            null,
            null,
            0,
            "sessions:0"));
        actionSeeds.Add(new ChannelCardActionSeed(
            "new",
            "new",
            null,
            null,
            null,
            "sessions:new"));

        // Build the envelope
        var envelope = new ChannelSessionFactsEnvelope(
            "kautuka.session-facts/v1",
            chat,
            topic,
            "summary",
            offset,
            bringToFront,
            revision,
            selectedFact,
            activeFact,
            queueFact,
            actionSeeds);

        string payloadJson = JsonSerializer.Serialize(envelope, ChannelContractsJsonContext.Default.ChannelSessionFactsEnvelope);

        outboxStore.Enqueue(registration.ChannelType, registration.Id, "session_facts", payloadJson, ownerUserId: registration.OwnerUserId);

        return Task.CompletedTask;
    }

    public Task PublishSessionPickerFactsAsync(
        GatewayChannelRegistration registration,
        GatewayChannelTargetBinding target,
        string chat,
        string? topic,
        string? selectedSessionId,
        int offset,
        bool onlyIfChanged = false,
        bool bringToFront = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (ChannelOutboundDestination destination in ChannelOutboundDestinations.Resolve(
            attachments, registration, chat, topic))
        {
            GatewayChannelTargetBinding? destinationTarget = destination.Registration.Id == registration.Id
                ? target
                : attachments.GetBindings(destination.Registration.Id).FirstOrDefault(binding =>
                    string.Equals(binding.TargetType, target.TargetType, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(binding.TargetId, target.TargetId, StringComparison.Ordinal));
            if (destinationTarget is null) continue;
            PublishSessionPickerFactsForDestination(destination.Registration, destinationTarget,
                destination.ConversationId, destination.ConversationThreadId, selectedSessionId, offset,
                onlyIfChanged, bringToFront);
        }
        return Task.CompletedTask;
    }

    private void PublishSessionPickerFactsForDestination(
        GatewayChannelRegistration registration,
        GatewayChannelTargetBinding target,
        string chat,
        string? topic,
        string? selectedSessionId,
        int offset,
        bool onlyIfChanged,
        bool bringToFront)
    {
        GatewaySession[] sessions = sessionStore.ListSessions(target.TargetType, target.TargetId, registration.OwnerUserId)
            .Where(session => AccessibleSession(registration.OwnerUserId, session.Id, target) is not null)
            .OrderByDescending(session => session.UpdatedAt, StringComparer.Ordinal).ToArray();

        const int pageSize = 5;
        offset = sessions.Length == 0 ? 0 : Math.Clamp(offset, 0, (sessions.Length - 1) / pageSize * pageSize);

        var page = sessions.Skip(offset).Take(pageSize).Select(session => new
        {
            Session = session,
            Queued = pendingTurns.ListSession(registration.OwnerUserId, session.Id).Count,
            Busy = IsSessionBusy(target.TargetId, session.Id),
            Label = (selectedSessionId == session.Id ? "✓ " : "") + SessionLabel(session)
                + (sessions.Count(other => SessionLabel(other) == SessionLabel(session)) > 1 ? " · " + session.Id[Math.Max(0, session.Id.Length - 6)..] : ""),
        }).ToArray();

        string signature = "sessions:" + selectedSessionId + ":" + routingCoordinator.ResolveTargetColor(target.TargetType, target.TargetId)
            + ":" + offset.ToString(CultureInfo.InvariantCulture) + ":" + sessions.Length
            + ":" + string.Join('|', page.Select(row => row.Session.Id + ":" + row.Label + ":" + row.Busy + ":" + row.Queued));

        string scope = ChannelQueueViewStore.RegistrationScope(registration);
        long? revision = queueViews.NextQueueViewRevision(registration.OwnerUserId, scope + ":session", chat, topic, signature, onlyIfChanged, pageOffset: offset, view: ChannelPanelView.Sessions);
        if (revision == 0) return;

        // Build SelectedSessionFact for session picker
        GatewaySession? selected = selectedSessionId is not null ? Array.Find(sessions, s => s.Id == selectedSessionId) : null;
        string? selectedLabel = selected is not null ? SessionLabel(selected) : null;

        var selectedFact = new ChannelSelectedSessionFact(
            ChannelFactContractVersions.SelectedSession,
            chat,
            topic,
            selected?.Id,
            selectedLabel,
            routingCoordinator.ResolveTargetDisplayName(target.TargetType, target.TargetId),
            revision.Value,
            target.TargetType,
            target.TargetId,
            routingCoordinator.ResolveTargetColor(target.TargetType, target.TargetId));

        // Build ActiveSessionsFact
        var activeSessions = page.Select(row => new ChannelActiveSessionEntry(
            row.Session.Id,
            row.Label,
            row.Busy ? "running" : "waiting")).ToArray();

        var activeFact = new ChannelActiveSessionsFact(
            ChannelFactContractVersions.ActiveSessions,
            chat,
            topic,
            revision.Value,
            activeSessions);

        // Build QueueFact (empty for session picker)
        var queueFact = new ChannelQueueFact(
            ChannelFactContractVersions.Queue,
            chat,
            topic,
            revision.Value,
            0,
            []);

        // Build action seeds
        var actionSeeds = new List<ChannelCardActionSeed>();
        foreach (var row in page)
        {
            string token = queueViews.CreateAction(registration.OwnerUserId, scope, chat, topic, "switch", row.Session.Id, "");
            actionSeeds.Add(new ChannelCardActionSeed(
                "session-" + row.Session.Id,
                "switch",
                row.Session.Id,
                null,
                null,
                token));
        }
        actionSeeds.Add(new ChannelCardActionSeed(
            "new",
            "new",
            null,
            null,
            null,
            "sessions:new"));
        if (offset > 0)
        {
            actionSeeds.Add(new ChannelCardActionSeed(
                "previous",
                "page",
                null,
                null,
                offset - pageSize,
                "sessions:" + (offset - pageSize).ToString(CultureInfo.InvariantCulture)));
        }
        if (offset + pageSize < sessions.Length)
        {
            actionSeeds.Add(new ChannelCardActionSeed(
                "next",
                "page",
                null,
                null,
                offset + pageSize,
                "sessions:" + (offset + pageSize).ToString(CultureInfo.InvariantCulture)));
        }
        actionSeeds.Add(new ChannelCardActionSeed(
            "close",
            "sessions",
            null,
            null,
            0,
            "q:home:0"));

        var envelope = new ChannelSessionFactsEnvelope(
            "kautuka.session-facts/v1",
            chat,
            topic,
            "sessions",
            offset,
            bringToFront,
            revision.Value,
            selectedFact,
            activeFact,
            queueFact,
            actionSeeds);

        string payloadJson = JsonSerializer.Serialize(envelope, ChannelContractsJsonContext.Default.ChannelSessionFactsEnvelope);

        outboxStore.Enqueue(registration.ChannelType, registration.Id, "session_facts", payloadJson, ownerUserId: registration.OwnerUserId);

    }

    private static string SessionLabel(GatewaySession session)
    {
        string label = session.Title.Trim();
        return string.IsNullOrEmpty(label) ? "New chat" : label.Length > 90 ? label[..87] + "…" : label;
    }

    private bool IsSessionBusy(string targetId, string sessionId)
    {
        // Implementation based on existing logic in ChannelIngressService.PendingTurns
        return state.AgentDefinitionStore.HasActiveRunForSession(
            state.AgentDefinitionStore.GetStorageLocatorKey(targetId), sessionId);
    }

    private GatewaySession? AccessibleSession(string ownerUserId, string sessionId, GatewayChannelTargetBinding target)
    {
        return state.Sessions.TryGetValue(sessionId, out GatewaySession? session)
            && session.OwnerUserId == ownerUserId
            && session.TargetType == target.TargetType
            && session.TargetId == target.TargetId
            && session.ArchivedAt is null
            ? session
            : null;
    }
}
