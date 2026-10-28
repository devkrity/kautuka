namespace Kautuka.Gateway.Infrastructure.Connectors;

internal sealed record ConnectorOAuthBeginRequest(string? Origin);

internal sealed record ConnectorOAuthBeginResponse(string Status, string Message, string? StartUrl);

internal sealed record ConnectorOAuthCompleteRequest(string? Code, string? State, string? BrokerTicket = null);

internal sealed record ConnectorOAuthCompleteResponse(string Status, string Message);

internal sealed record ConnectorOAuthStatusItem(
    string ConnectorId,
    bool Connected,
    bool HasSavedCredential,
    bool Configured,
    string Message,
    string SignInLabel,
    string RevokeUrl);

internal sealed record ConnectorOAuthCatalogResponse(ConnectorOAuthStatusItem[] Connectors);

internal sealed record ConnectorOAuthDisconnectResponse(bool Disconnected, string Message, string? RevokeUrl);
