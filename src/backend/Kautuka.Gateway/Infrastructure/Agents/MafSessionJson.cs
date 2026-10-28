using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Kautuka.Gateway.Infrastructure.Agents;

/// <summary>
/// MAF session JSON helpers for Native AOT.
/// </summary>
/// <remarks>
/// MAF 1.19 tags each request message with <see cref="AgentRequestMessageSourceAttribution"/>
/// under <c>AdditionalProperties["_attribution"]</c> for the current run (filter by source).
/// That struct is not registered in MAF's source-gen JSON context, and
/// <see cref="AIAgent.SerializeSessionAsync"/> hard-wires nested serialization to
/// <see cref="AgentAbstractionsJsonUtilities.DefaultOptions"/> (read-only — we cannot
/// add the missing TypeInfo). Until MAF ships the attribute in their context, we must
/// clear these ephemeral tags before serialize; they are not part of durable history.
/// </remarks>
internal static class MafSessionJson
{
    /// <summary>
    /// Clears run-scoped <c>_attribution</c> tags that MAF cannot AOT-serialize.
    /// </summary>
    public static void StripAgentRequestMessageAttributions(AIAgent agent, AgentSession session)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(session);

        if (agent is ChatClientAgent { ChatHistoryProvider: InMemoryChatHistoryProvider history })
        {
            StripFromMessages(history.GetMessages(session));
            return;
        }

        StripFromStateBag(session);
    }

    private static void StripFromStateBag(AgentSession session)
    {
        // Default InMemoryChatHistoryProvider state key (provider type name).
        if (!session.StateBag.TryGetValue(
                "InMemoryChatHistoryProvider",
                out InMemoryChatHistoryProvider.State? state,
                AgentAbstractionsJsonUtilities.DefaultOptions)
            || state?.Messages is null)
        {
            return;
        }

        StripFromMessages(state.Messages);
    }

    private static void StripFromMessages(IEnumerable<ChatMessage> messages)
    {
        foreach (ChatMessage message in messages)
        {
            message.AdditionalProperties?.Remove(AgentRequestMessageSourceAttribution.AdditionalPropertiesKey);
        }
    }
}
