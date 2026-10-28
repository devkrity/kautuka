using System.Runtime.CompilerServices;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Execution;

internal sealed partial class AgentChatService
{
    private AgentExecutionEventWriter CreateExecutionEventWriter()
        => new(GetRuntimeEventPublisher(), GetRuntimeEventHistoryReader());

    /// <summary>
    /// Domain → Writer → journal. Sole early-lifecycle emit path (no direct Narad invent).
    /// </summary>
    private static async IAsyncEnumerable<NaradPersistedEvent> EmitDomainAsync(
        AgentExecutionEventWriter eventWriter,
        GatewayChatStreamEvent domainEvent,
        NaradEventContext context,
        AssistantMessageLifecycle messageLifecycle,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        IReadOnlyList<NaradPersistedEvent> events = await eventWriter
            .MapAsync(domainEvent, context, messageLifecycle, cancellationToken)
            .ConfigureAwait(false);
        foreach (NaradPersistedEvent persisted in events)
        {
            yield return persisted;
        }
    }
}
