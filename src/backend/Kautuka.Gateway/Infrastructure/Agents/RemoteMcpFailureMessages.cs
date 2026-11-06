using System.Net.Sockets;
using System.Security.Authentication;

namespace Kautuka.Gateway.Infrastructure.Agents;

internal static class RemoteMcpFailureMessages
{
    public static string Describe(Exception exception, bool keylessExa = false)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is RemoteMcpConfigurationException configured) return configured.Message;

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
