namespace Kautuka.Gateway.Infrastructure.Configuration;

/// <summary>
/// Authoritative store for agent session metadata and ordered messages.
/// GatewayState is a reconstructible in-process projection of this repository.
/// </summary>
internal interface IAgentRuntimeSessionRepository
{
    IReadOnlyList<GatewaySession> ListRuntimeSessions(string storageLocatorKey);

    void CreateRuntimeSession(string storageLocatorKey, GatewaySession session);

    void UpdateRuntimeSession(string storageLocatorKey, GatewaySession session);

    bool DeleteRuntimeSession(string storageLocatorKey, string sessionId);

    void AppendRuntimeSessionMessages(string storageLocatorKey, GatewaySession session, IReadOnlyList<GatewayMessage> appendedMessages);
}
