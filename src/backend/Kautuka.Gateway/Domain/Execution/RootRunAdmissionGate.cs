namespace Kautuka.Gateway.Domain.Execution;

internal interface IRootRunAdmissionGate
{
    bool IsAdmissionClosed(string agentId, string? rootRunId);
}

internal sealed class RootRunAdmissionGate(IAgentDefinitionStore agentDefinitionStore) : IRootRunAdmissionGate
{
    public bool IsAdmissionClosed(string agentId, string? rootRunId)
    {
        if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(rootRunId))
        {
            return false;
        }

        string storageLocatorKey = agentDefinitionStore.GetStorageLocatorKey(agentId);
        return agentDefinitionStore.IsNaradRootRunAdmissionClosed(storageLocatorKey, rootRunId);
    }
}

internal sealed class RootRunAdmissionClosedException : InvalidOperationException
{
    public RootRunAdmissionClosedException(string rootRunId)
        : base($"Root run '{rootRunId}' is no longer accepting approval requests.")
    {
    }

    public RootRunAdmissionClosedException()
    {
    }

    public RootRunAdmissionClosedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
