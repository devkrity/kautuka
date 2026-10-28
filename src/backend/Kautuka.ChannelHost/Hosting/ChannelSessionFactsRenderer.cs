using System.Text.Json;

namespace Kautuka.ChannelHost.Hosting;

internal static class ChannelSessionFactsRenderer
{
    private const int visibleQueueItems = 5;

    public static ChannelSemanticMessageAction Render(string payloadJson, string? transportMessageId = null)
    {
        ChannelSessionFactsEnvelope facts = JsonSerializer.Deserialize(payloadJson, ChannelContractsJsonContext.Default.ChannelSessionFactsEnvelope)
            ?? throw new JsonException("Session facts were empty.");
        if (facts.Version != ChannelFactContractVersions.SessionFactsEnvelope
            || facts.Selected?.Version != ChannelFactContractVersions.SelectedSession
            || facts.Active?.Version != ChannelFactContractVersions.ActiveSessions
            || facts.QueueFacts?.Version != ChannelFactContractVersions.Queue
            || facts.Selected.Revision != facts.Revision || facts.Active.Revision != facts.Revision || facts.QueueFacts.Revision != facts.Revision
            || facts.Active.Sessions is null || facts.QueueFacts.Queue is null
            || facts.Selected.ConversationId != facts.ConversationId || facts.Active.ConversationId != facts.ConversationId
            || facts.QueueFacts.ConversationId != facts.ConversationId
            || facts.Revision < 1 || facts.PageOffset < 0
            || facts.View is not ("summary" or "sessions" or "queue"))
            throw new JsonException("Session facts did not match a supported contract version.");

        string title = facts.View switch
        {
            "sessions" => "Switch session",
            "queue" => "Queued messages",
            _ => string.Join(" · ", new[] { facts.Selected.AgentDisplayName, facts.Selected.SessionLabel }.OfType<string>().Where(value => !string.IsNullOrWhiteSpace(value)))
        };
        if (string.IsNullOrWhiteSpace(title)) title = "Select a session";

        var blocks = new List<ChannelSemanticBlock>
        {
            new(ChannelSemanticBlockTypes.Header, Text: title),
        };

        if (facts.View == "queue")
        {
            foreach (ChannelQueueEntry queued in facts.QueueFacts.Queue)
            {
                // The bot-wide queue is an action list, not another session picker.
                // Keep the owning agent visible, but leave session navigation to the
                // Sessions control so this card stays compact and scannable.
                blocks.Add(new(ChannelSemanticBlockTypes.Context, Text: queued.AgentDisplayName));
                blocks.Add(new(ChannelSemanticBlockTypes.Section, Text: $"{queued.Order}. {queued.Preview}"));
                ChannelCardActionSeed? remove = facts.ActionsSeed.FirstOrDefault(seed => seed.Kind == "remove" && seed.TurnId == queued.TurnId);
                if (remove is not null)
                    blocks.Add(new(ChannelSemanticBlockTypes.Actions, Actions: [new(remove.ActionId, ChannelSemanticActionTypes.Button,
                        "Remove", remove.Token, ChannelSemanticStyles.Danger)]));
            }
            if (facts.QueueFacts.TotalCount > facts.QueueFacts.Queue.Count)
                blocks.Add(new(ChannelSemanticBlockTypes.Context, Text: $"{facts.QueueFacts.TotalCount - facts.QueueFacts.Queue.Count} more queued."));
        }
        else if (facts.View == "summary")
        {
            blocks.Add(new(ChannelSemanticBlockTypes.Context,
                Text: facts.Selected.SessionId is null ? "Choose a session before sending a prompt." : "Prompts go to this session until you switch or start a new session."));
            int activeCount = facts.Active.Sessions.Count(session => session.RunLabel is "running" or "waiting for approval");
            int queuedCount = facts.QueueFacts.TotalCount;
            var details = new List<ChannelSemanticBlock>();
            foreach (ChannelActiveSessionEntry session in facts.Active.Sessions)
                details.Add(new(ChannelSemanticBlockTypes.Section, Text: $"{session.Label} — {session.RunLabel}"));
            foreach (IGrouping<string, ChannelQueueEntry> sessionQueue in facts.QueueFacts.Queue.Take(visibleQueueItems).GroupBy(entry => entry.SessionId))
            {
                string session = facts.Active.Sessions.FirstOrDefault(item => item.SessionId == sessionQueue.Key)?.Label ?? sessionQueue.Key;
                details.Add(new(ChannelSemanticBlockTypes.Heading, Text: session));
                foreach (ChannelQueueEntry queued in sessionQueue)
                {
                    details.Add(new(ChannelSemanticBlockTypes.Section, Text: $"{queued.Order}. {queued.Preview}"));
                    ChannelCardActionSeed? remove = facts.ActionsSeed.FirstOrDefault(seed => seed.Kind == "remove" && seed.TurnId == queued.TurnId);
                    if (remove is not null)
                        details.Add(new(ChannelSemanticBlockTypes.Actions, Actions: [new(remove.ActionId, ChannelSemanticActionTypes.Button,
                            $"Remove #{queued.Order}", remove.Token)]));
                }
                ChannelCardActionSeed? clear = facts.ActionsSeed.FirstOrDefault(seed => seed.Kind == "clear" && seed.SessionId == sessionQueue.Key);
                if (clear is not null)
                    details.Add(new(ChannelSemanticBlockTypes.Actions, Actions: [new(clear.ActionId, ChannelSemanticActionTypes.Button,
                        "Clear this session", clear.Token, ChannelSemanticStyles.Danger)]));
            }
            blocks.Add(new(ChannelSemanticBlockTypes.Details,
                Text: $"{activeCount} active sessions · {queuedCount} messages waiting", Expanded: false, Blocks: details));
        }
        else
        {
            string selectedAgent = facts.Selected.AgentDisplayName ?? "your selected agent";
            blocks.Add(new(ChannelSemanticBlockTypes.Context, Text: $"Choose a session for {selectedAgent}."));
            foreach (ChannelActiveSessionEntry session in facts.Active.Sessions)
            {
                ChannelCardActionSeed? select = facts.ActionsSeed.FirstOrDefault(seed => seed.Kind == "switch" && seed.SessionId == session.SessionId);
                if (select is null) continue;
                blocks.Add(new(ChannelSemanticBlockTypes.Section, Text: $"{session.Label}\n{session.RunLabel}"));
                blocks.Add(new(ChannelSemanticBlockTypes.Actions, Actions: [new(select.ActionId, ChannelSemanticActionTypes.Button,
                    "Use this session", select.Token)]));
            }
        }

        ChannelCardActionSeed[] globalActions = facts.View == "queue"
            ? facts.ActionsSeed.Where(seed => seed.Kind == "page").ToArray()
            : facts.ActionsSeed.Where(seed => seed.Kind is "sessions" or "new" or "page").ToArray();
        if (globalActions.Length > 0)
            blocks.Add(new(ChannelSemanticBlockTypes.Actions, Actions: globalActions.Select(seed => new ChannelSemanticAction(
                seed.ActionId,
                ChannelSemanticActionTypes.Button,
                seed.Kind switch
                {
                    "sessions" => facts.View == "sessions" ? "Back" : "Switch session",
                    "new" => "New session",
                    "page" => seed.Offset < facts.PageOffset ? "Previous" : "More",
                    _ => seed.Kind,
                },
                seed.Token,
                seed.Kind == "new" ? ChannelSemanticStyles.Primary : null)).ToArray()));

        string fallback = string.Join('\n', blocks.SelectMany(Flatten).Where(block => !string.IsNullOrWhiteSpace(block.Text)).Select(block => block.Text));
        ChannelSemanticMessageDocument document = ChannelMessageTemplates.SessionPanel(fallback, blocks);
        return new ChannelSemanticMessageAction(facts.ConversationId, facts.ConversationThreadId, document,
            TransportMessageId: transportMessageId);
    }

    private static IEnumerable<ChannelSemanticBlock> Flatten(ChannelSemanticBlock block)
    {
        yield return block;
        if (block.Blocks is null) yield break;
        foreach (ChannelSemanticBlock child in block.Blocks)
            foreach (ChannelSemanticBlock descendant in Flatten(child))
                yield return descendant;
    }

}
