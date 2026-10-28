using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Execution;

internal interface IRuntimeEventHistoryReader
{
    IReadOnlyList<NaradPersistedEvent> ReadRootHistory(string rootRunId);

    IReadOnlyList<NaradPersistedEvent> ReadSessionHistory(string rootAgentId, string sessionId);
}
