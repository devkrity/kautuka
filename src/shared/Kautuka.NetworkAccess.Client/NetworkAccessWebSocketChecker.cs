using System.Net.WebSockets;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.NetworkAccess.Client;

/// <summary>
/// Enforces Network Access Control before opening a ClientWebSocket connection.
/// </summary>
public sealed partial class NetworkAccessWebSocketChecker(
    INetworkAccessChecker checker,
    ILogger<NetworkAccessWebSocketChecker>? logger = null,
    string defaultExecutionContext = "app")
{
    private readonly INetworkAccessChecker checker = checker ?? throw new ArgumentNullException(nameof(checker));
    private readonly ILogger logger = logger ?? NullLogger<NetworkAccessWebSocketChecker>.Instance;
    private readonly string defaultExecutionContext = defaultExecutionContext;

    public async Task CheckAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri)
        {
            throw new ArgumentException("URI must be absolute.", nameof(uri));
        }

        string host = uri.IdnHost;
        int port = uri.Port > 0
            ? uri.Port
            : (string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase) || string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) ? 443 : 80);

        NetworkAccessScope? ambient = NetworkAccessContext.Current;

        var target = new OutboundTarget(
            Host: host,
            Port: port,
            Scheme: uri.Scheme,
            Service: ambient?.Service ?? "websocket",
            Purpose: ambient?.Purpose ?? "socket-connect",
            UserId: ambient?.UserId,
            AgentId: ambient?.AgentId,
            ConnectorId: ambient?.ConnectorId,
            Systemwide: ambient?.Systemwide ?? false,
            ExecutionContext: ambient?.ExecutionContext ?? defaultExecutionContext);

        NetworkAccessCheckResult result = await checker.CheckAsync(target, cancellationToken).ConfigureAwait(false);
        if (!result.Allowed)
        {
            LogNetworkAccessDenied(logger, host, port, result.Reason, result.RequestId);
            result.ThrowIfDenied(target);
        }
    }

    public async Task ConnectAsync(ClientWebSocket webSocket, Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(webSocket);
        await CheckAsync(uri, cancellationToken).ConfigureAwait(false);
        await webSocket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Network access denied for WebSocket connection to {Host}:{Port} (Reason: {Reason}, RequestId: {RequestId})")]
    private static partial void LogNetworkAccessDenied(ILogger logger, string host, int port, string reason, string? requestId);
}
