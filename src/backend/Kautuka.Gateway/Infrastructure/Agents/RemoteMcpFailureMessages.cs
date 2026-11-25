using System.Net.Sockets;
using System.Security.Authentication;

using Kautuka.Gateway.Infrastructure.Egress;
using Kautuka.NetworkAccess.Client;

namespace Kautuka.Gateway.Infrastructure.Agents;

internal static class RemoteMcpFailureMessages
{
    public static string Describe(Exception exception, bool keylessExa = false)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is RemoteMcpConfigurationException configured) return configured.Message;

        const string accessBlocked = "Network Access blocked this connection. Review the rules and pending requests before retrying.";
        if (Find<NetworkAccessDeniedException>(exception) is { } denial)
        {
            return denial.Kind switch
            {
                NetworkAccessDenialKind.Paused => "Network access is paused. Resume it in Network Access before retrying.",
                NetworkAccessDenialKind.Unavailable => "Network access control is unavailable. Try again when it is back online.",
                _ when !string.IsNullOrWhiteSpace(denial.RequestId) => "Network approval is required. Review the request in Network Access, then test again.",
                _ => accessBlocked,
            };
        }
        if (Find<GatewayConnectDeniedException>(exception) is { } connectDenial)
            return connectDenial.Message;

        SocketException? socket = Find<SocketException>(exception);
        if (socket?.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain)
            return "The remote MCP address could not be found.";
        if (Find<AuthenticationException>(exception) is not null)
            return "The secure connection to the remote MCP server failed. Check its TLS certificate and settings.";
        if (exception is HttpRequestException http
            && (http.Message.Contains("not allowed", StringComparison.OrdinalIgnoreCase)
                || http.Message.Contains("isn't allowed", StringComparison.OrdinalIgnoreCase)
                || http.Message.Contains("outside the authorized origin", StringComparison.OrdinalIgnoreCase)))
            return "This destination is not allowed for the connector.";
        if (exception is HttpRequestException ipv4 && ipv4.Message.Contains("direct IPv4", StringComparison.OrdinalIgnoreCase))
            return "Exa free access needs a direct IPv4 connection. Add an Exa API key or use a network with public IPv4 access.";
        if (keylessExa && socket is not null)
            return "Exa free access needs a direct IPv4 connection. Add an Exa API key or use a network with public IPv4 access.";
        return "The remote MCP provider could not be reached. Check the address and try again.";
    }

    public static RemoteMcpConfigurationException AsSafeException(Exception exception, bool keylessExa = false)
        => exception as RemoteMcpConfigurationException ?? new RemoteMcpConfigurationException(Describe(exception, keylessExa));

    private static T? Find<T>(Exception exception) where T : Exception
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is T match) return match;
        return null;
    }
}
