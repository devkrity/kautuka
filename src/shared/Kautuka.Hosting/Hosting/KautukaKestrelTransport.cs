using System.Net;

using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Kautuka.Hosting.Hosting;

/// <summary>
/// Binds a Kautuka service to whichever transport <see cref="KautukaHostNetworkPolicy"/>
/// resolved, so no service decides for itself.
/// </summary>
public static class KautukaKestrelTransport
{
    /// <summary>
    /// Binds <paramref name="endpoint"/> and applies <paramref name="configure"/> to the
    /// resulting listener. The caller keeps ownership of protocol and TLS configuration:
    /// mTLS is unchanged by the transport and still authenticates every connection.
    /// </summary>
    public static void Listen(
        this KestrelServerOptions options,
        KautukaEndpoint endpoint,
        Action<ListenOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(configure);

        if (endpoint.IsUnixSocket)
        {
            KautukaHostNetworkPolicy.EnsureSocketDirectory(endpoint);
            KautukaHostNetworkPolicy.ClearStaleSocket(endpoint);
            options.ListenUnixSocket(endpoint.SocketPath!, configure);
            return;
        }

        // Loopback, never a wildcard. A wildcard bind was reachable from the user's other
        // WSL distros, which all share one network namespace with the appliance.
        options.Listen(IPAddress.Loopback, endpoint.Port, configure);
    }

    /// <summary>
    /// Applies the socket file's group permission once the server has bound it.
    /// </summary>
    /// <remarks>
    /// The socket file does not exist until Kestrel binds, so the mode cannot be set while
    /// configuring the listener. <see cref="IHostApplicationLifetime.ApplicationStarted"/>
    /// runs after every hosted service has started, Kestrel included, which is the first
    /// point the file is guaranteed to be there.
    /// </remarks>
    public static void GrantSocketGroupAccessWhenStarted(this IHost host, KautukaEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(endpoint);

        if (!endpoint.IsUnixSocket)
            return;

        host.Services
            .GetRequiredService<IHostApplicationLifetime>()
            .ApplicationStarted
            .Register(() => KautukaHostNetworkPolicy.GrantSocketGroupAccess(endpoint));
    }
}
