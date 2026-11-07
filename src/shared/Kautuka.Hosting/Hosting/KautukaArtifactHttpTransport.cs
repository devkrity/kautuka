using System.Net;
using System.Net.Sockets;

using Microsoft.Extensions.Configuration;

namespace Kautuka.Hosting.Hosting;

/// <summary>Host artifact HTTPS through the appliance's fixed Unix-socket CONNECT proxy.</summary>
public static class KautukaArtifactHttpTransport
{
    private static readonly Uri proxyAddress = new("http://kautuka-host-egress.invalid:8080");

    public static SocketsHttpHandler CreateForConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        string? configured = configuration["ContainerEgress:ArtifactSocketPath"];
        bool managed = bool.TryParse(configuration["ContainerEgress:Enabled"], out bool enabled) && enabled;
        // Legacy host-service installations retain their existing transport until the installer selects managed egress.
        if (!managed && string.IsNullOrWhiteSpace(configured))
            return new SocketsHttpHandler();
        string path = configured ?? Path.Combine(KautukaHostPathPolicy.ResolveServiceStateRoot(configuration, "manager"), "egress", "host", "connect.sock");
        return Create(path);
    }

    public static SocketsHttpHandler Create(string socketPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        if (!Path.IsPathFullyQualified(socketPath)) throw new ArgumentException("The artifact proxy socket must be an absolute path.", nameof(socketPath));
        string path = Path.GetFullPath(socketPath);
        return new SocketsHttpHandler
        {
            UseProxy = true,
            Proxy = new FixedProxy(),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = async (context, cancellationToken) =>
            {
                if (context.DnsEndPoint.Host != proxyAddress.Host || context.DnsEndPoint.Port != proxyAddress.Port)
                    throw new IOException("Artifact HTTP attempted a connection outside its fixed proxy.");
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            },
        };
    }

    private sealed class FixedProxy : IWebProxy
    {
        public ICredentials? Credentials { get; set; }
        public Uri GetProxy(Uri destination) => proxyAddress;
        public bool IsBypassed(Uri host) => false;
    }
}
