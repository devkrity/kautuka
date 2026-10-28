namespace Kautuka.Gateway.Domain.State;

internal sealed partial class GatewayState
{
    internal void CreateAgentRuntimeSession(GatewaySession session)
        => agentRuntimeSessionRepository.CreateRuntimeSession(ResolveAgentRuntimeStorageLocator(session), session);

    internal void UpdateAgentRuntimeSession(GatewaySession session)
        => agentRuntimeSessionRepository.UpdateRuntimeSession(ResolveAgentRuntimeStorageLocator(session), session);

    internal bool DeleteAgentRuntimeSession(GatewaySession session)
        => agentRuntimeSessionRepository.DeleteRuntimeSession(ResolveAgentRuntimeStorageLocator(session), session.Id);

    internal void AppendAgentRuntimeSessionMessages(GatewaySession session, IReadOnlyList<GatewayMessage> appendedMessages)
        => agentRuntimeSessionRepository.AppendRuntimeSessionMessages(ResolveAgentRuntimeStorageLocator(session), session, appendedMessages);

    internal void CreateAgentRuntimeRun(GatewayRun run)
        => agentRuntimeRunRepository.CreateRuntimeRun(ResolveAgentRuntimeStorageLocator(run.TargetType, run.TargetId), run);

    internal void UpdateAgentRuntimeRun(GatewayRun run)
        => agentRuntimeRunRepository.UpdateRuntimeRun(ResolveAgentRuntimeStorageLocator(run.TargetType, run.TargetId), run);

    internal void UpsertAgentMemoryFacts(string targetType, string targetId, IReadOnlyList<GatewayMemoryFact> facts)
    {
        string storageLocatorKey = ResolveAgentRuntimeStorageLocator(targetType, targetId);
        agentMemoryFactRepository.UpsertMemoryFacts(storageLocatorKey, facts);
        memoryStore.AddRange(targetType, targetId, facts);
    }

    internal bool DeleteAgentMemoryFact(GatewayMemoryFact fact)
        => agentMemoryFactRepository.DeleteMemoryFact(ResolveAgentRuntimeStorageLocator(fact.TargetType, fact.TargetId), fact.Id);

    private string ResolveAgentRuntimeStorageLocator(GatewaySession session)
        => ResolveAgentRuntimeStorageLocator(session.TargetType, session.TargetId);

    private string ResolveAgentRuntimeStorageLocator(string targetType, string targetId)
    {
        if (!string.Equals(targetType, "agent", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Only agent runtime data is stored in per-agent databases.");
        }

        string storageLocatorKey = AgentStorageLocators.TryGetValue(targetId, out string? registeredStorageLocatorKey)
            ? registeredStorageLocatorKey
            : agentDefinitionStore.GetStorageLocatorKey(targetId);
        return storageLocatorKey;
    }

    internal IReadOnlyList<string> RemoveAgentRuntimeState(string agentId)
    {
        List<string> removedSessionIds = [.. sessions.Values
            .Where(session => string.Equals(session.TargetType, "agent", StringComparison.OrdinalIgnoreCase)
                && string.Equals(session.TargetId, agentId, StringComparison.Ordinal))
            .Select(session => session.Id)];

        foreach (string sessionId in removedSessionIds)
        {
            _ = sessions.TryRemove(sessionId, out _);
        }

        lock (runLock)
        {
            _ = runs.RemoveAll(run => string.Equals(run.TargetType, "agent", StringComparison.OrdinalIgnoreCase)
                && string.Equals(run.TargetId, agentId, StringComparison.Ordinal));
        }

        IReadOnlyList<GatewayMemoryFact> remainingFacts = [.. memoryStore.Snapshot()
            .Where(fact => !string.Equals(fact.TargetType, "agent", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(fact.TargetId, agentId, StringComparison.Ordinal))];
        memoryStore.Restore(remainingFacts);

        return removedSessionIds;
    }
}
