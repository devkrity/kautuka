using Kautuka.Gateway.Domain.Execution;

using Narad.Contracts;

namespace Kautuka.Gateway.Domain.Channels;

internal sealed partial class ChannelRuntimeEventProjector
{
    private (string SessionId, string RootRunId) ResolvePromptContext(NaradPersistedEvent evt, GatewayChannelRegistration registration, string? surfaceAgentId)
    {
        string root = evt.RootRunId ?? evt.RunId ?? string.Empty;
        if (evt.EventType is not (NaradEventTypes.InterruptRequested or NaradEventTypes.InterruptResolved)
            || state.AgentDefinitionStore is not { } definitions || string.IsNullOrWhiteSpace(evt.RunId) || string.IsNullOrWhiteSpace(surfaceAgentId))
            return (evt.SessionId, root);
        // Source agent journals can contain only a fragment of the root sequence.
        // Resolve the exact run id without requesting a complete root-history stream.
        string storage = definitions.GetStorageLocatorKey(NaradPersistedEventReader.String(evt, "agentId") ?? surfaceAgentId);
        string rootStorage = definitions.GetStorageLocatorKey(surfaceAgentId);
        root = definitions.FindNaradRootRunIdForRun(storage, evt.RunId)
            ?? definitions.FindNaradRootRunIdForRun(rootStorage, evt.RunId) ?? root;
        string? outerSessionId = NaradPersistedEventReader.String(evt, "outerSessionId");
        if (!string.IsNullOrWhiteSpace(outerSessionId) && sessionStore.TryGetSession(outerSessionId, out GatewaySession? session)
            && session is not null && session.OwnerUserId == registration.OwnerUserId && session.TargetId == surfaceAgentId)
            return (session.Id, root);
        return (evt.SessionId, root);
    }
}
