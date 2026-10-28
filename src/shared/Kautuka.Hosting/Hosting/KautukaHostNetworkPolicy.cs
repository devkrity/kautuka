using System.Net.Sockets;

using Microsoft.Extensions.Configuration;

namespace Kautuka.Hosting.Hosting;

public enum KautukaTransport
{
    /// <summary>
    /// A socket file inside the appliance's own filesystem. The default, because a
    /// WSL2 distro does not get its own network namespace - every running distro shares
    /// one, so a loopback port is reachable from the user's other distros and no bind
    /// address can make it private. A socket file is not, since distros have separate
    /// filesystems.
    /// </summary>
    UnixSocket,

    /// <summary>
    /// TCP loopback. Retained as a deliberate escape hatch for debugging and for any
    /// future deployment that splits services across machines, which sockets cannot do.
    /// </summary>
    Tcp,
}

public sealed record KautukaEndpoint
{
    public required string Name { get; init; }

    public required KautukaTransport Transport { get; init; }

    /// <summary>
    /// Meaningful only for <see cref="KautukaTransport.Tcp"/>. Retained for socket
    /// transports so diagnostics, docs and scripts can still name the service by the
    /// port it historically used.
    /// </summary>
    public required int Port { get; init; }

    /// <summary>Set only for <see cref="KautukaTransport.UnixSocket"/>.</summary>
    public string? SocketPath { get; init; }

    /// <summary>
    /// For TCP this is the real address. For a socket it is a placeholder whose host is
    /// still load-bearing: TLS validates the hostname even though no network is involved,
    /// and the generated server certificates carry a <c>localhost</c> SAN.
    /// </summary>
    public required Uri BaseUri { get; init; }

    public bool IsUnixSocket => Transport == KautukaTransport.UnixSocket;
}

public sealed record KautukaHostNetworkLayout(
    int GatewayPublicPort,
    int ManagerGrpcPort,
    int UpdateServiceGrpcPort,
    int CredentialsGrpcPort,
    int ChannelHostGrpcPort,
    int WebHostPort,
    KautukaEndpoint Gateway,
    KautukaEndpoint Manager,
    KautukaEndpoint UpdateService,
    KautukaEndpoint Credentials,
    KautukaEndpoint ChannelHost)
{
    public Uri GatewayPublicBaseUri => Gateway.BaseUri;

    public Uri ManagerGrpcBaseUri => Manager.BaseUri;

    public Uri UpdateServiceGrpcBaseUri => UpdateService.BaseUri;

    public Uri CredentialsGrpcBaseUri => Credentials.BaseUri;

    public Uri ChannelHostGrpcBaseUri => ChannelHost.BaseUri;
}

/// <summary>
/// The single place that decides how Kautuka's services address each other.
/// <para>
/// It is deliberately the only seam. Per-service transport branching is what produced
/// the divergence this class replaces, where Manager alone bound <c>IPAddress.Any</c> in
/// prod and loopback in dev while its siblings did neither.
/// </para>
/// </summary>
public static class KautukaHostNetworkPolicy
{
    public const int GatewayPublicPort = 20000;
    public const int ManagerGrpcPort = 20001;
    public const int UpdateServiceGrpcPort = 20004;
    public const int CredentialsGrpcPort = 20002;
    public const int ChannelHostGrpcPort = 20003;

    /// <summary>
    /// WebHost is the one intended entry point and stays on TCP unconditionally: a
    /// browser cannot open a Unix socket.
    /// </summary>
    public const int WebHostPort = 20229;

    public const string TransportConfigurationKey = "Kautuka:Transport";
    public const string SocketRootConfigurationKey = "Kautuka:SocketRoot";

    /// <summary>
    /// <c>sockaddr_un.sun_path</c> is 108 bytes on Linux including the terminator. Exceeding
    /// it fails at bind with a message that does not mention length, so it is checked here.
    /// </summary>
    private const int maxUnixSocketPathBytes = 107;

    private const string prodSocketRoot = "/run/kautuka";
    private const string devSocketFolderName = "sockets";

    public static KautukaHostNetworkLayout Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        KautukaTransport transport = ResolveTransport(configuration);
        string socketRoot = ResolveSocketRoot(configuration);

        return new KautukaHostNetworkLayout(
            GatewayPublicPort,
            ManagerGrpcPort,
            UpdateServiceGrpcPort,
            CredentialsGrpcPort,
            ChannelHostGrpcPort,
            WebHostPort,
            CreateEndpoint("gateway", GatewayPublicPort, Uri.UriSchemeHttp, transport, socketRoot),
            CreateEndpoint("manager", ManagerGrpcPort, Uri.UriSchemeHttps, transport, socketRoot),
            CreateEndpoint("updateservice", UpdateServiceGrpcPort, Uri.UriSchemeHttps, transport, socketRoot),
            CreateEndpoint("credentialshost", CredentialsGrpcPort, Uri.UriSchemeHttps, transport, socketRoot),
            CreateEndpoint("channelhost", ChannelHostGrpcPort, Uri.UriSchemeHttps, transport, socketRoot));
    }

    public static KautukaTransport ResolveTransport(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        string? configured = configuration[TransportConfigurationKey];

        if (string.Equals(configured, "tcp", StringComparison.OrdinalIgnoreCase))
            return KautukaTransport.Tcp;

        if (string.Equals(configured, "unix", StringComparison.OrdinalIgnoreCase)
            || string.Equals(configured, "unixsocket", StringComparison.OrdinalIgnoreCase))
            return RequireUnixSocketSupport();

        // Unset means sockets. A platform without AF_UNIX falls back rather than failing
        // to start, because the transport is a hardening measure and not a correctness
        // requirement - every endpoint is still behind mTLS on either transport.
        return Socket.OSSupportsUnixDomainSockets ? KautukaTransport.UnixSocket : KautukaTransport.Tcp;
    }

    /// <summary>
    /// The directory holding the socket files. On a service host this must already exist
    /// with the right ownership; <c>systemd-tmpfiles</c> provides that for the appliance
    /// and for Linux installs, and <see cref="EnsureSocketDirectory"/> covers dev.
    /// </summary>
    public static string ResolveSocketRoot(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        string? configured = configuration[SocketRootConfigurationKey];
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);

        KautukaHostPathLayout paths = KautukaHostPathPolicy.Resolve(configuration);

        // Prod puts sockets on tmpfs rather than beside persistent state, so a socket
        // cannot outlive the boot that created it.
        return paths.Mode == KautukaHostMode.Prod && !OperatingSystem.IsWindows()
            ? prodSocketRoot
            : Path.Combine(paths.InternalStateRoot, devSocketFolderName);
    }

    /// <summary>
    /// Creates the socket directory when it is missing. A service host normally has this
    /// provisioned already; this covers dev and tests, where nothing else would create it.
    /// </summary>
    public static void EnsureSocketDirectory(KautukaEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (!endpoint.IsUnixSocket)
            return;

        string? directory = Path.GetDirectoryName(endpoint.SocketPath!);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
    }

    /// <summary>
    /// Removes a socket file left behind by an unclean exit. Binding over one fails with
    /// <c>EADDRINUSE</c>, which reads like a port conflict and is not one.
    /// </summary>
    public static void ClearStaleSocket(KautukaEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (!endpoint.IsUnixSocket || !File.Exists(endpoint.SocketPath!))
            return;

        try
        {
            File.Delete(endpoint.SocketPath!);
        }
        catch (IOException)
        {
            // A live holder means the real bind should fail loudly rather than here.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Widens a freshly bound socket file to <c>0660</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Connecting to a Unix socket requires <em>write</em> permission on the socket file,
    /// not merely traverse on its directory. The socket is created under the service's
    /// <c>UMask=</c>, which the units set for state and log protection (<c>0077</c> for
    /// Gateway and ChannelHost, <c>0027</c> for CredentialsHost), so every peer was
    /// refused: measured as `502` on `/api` in the appliance, with every cross-service hop
    /// denied. Windows does not enforce these bits, which is why it only appears on Linux.
    /// </para>
    /// <para>
    /// This does not widen access on its own. The socket directory is setgid, so the
    /// socket inherits that directory's dedicated <c>-client</c> group rather than the
    /// service's own, and the directory stays <c>0750</c>, which remains the real gate:
    /// only the owning service and members of its client group can reach the path at all.
    /// </para>
    /// </remarks>
    public static void GrantSocketGroupAccess(KautukaEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (!endpoint.IsUnixSocket || OperatingSystem.IsWindows())
            return;

        if (!File.Exists(endpoint.SocketPath!))
            return;

        File.SetUnixFileMode(
            endpoint.SocketPath!,
            UnixFileMode.UserRead | UnixFileMode.UserWrite
            | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
    }

    /// <summary>
    /// Produces the <see cref="System.Net.Http.SocketsHttpHandler.ConnectCallback"/> that
    /// redirects a client from its placeholder URI to the socket. Returns
    /// <see langword="null"/> for TCP so callers can assign unconditionally.
    /// </summary>
    public static Func<System.Net.Http.SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>? CreateConnectCallback(
        KautukaEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (!endpoint.IsUnixSocket)
            return null;

        string socketPath = endpoint.SocketPath!;

        return async (_, cancellationToken) =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken)
                    .ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };
    }

    private static KautukaEndpoint CreateEndpoint(
        string name,
        int port,
        string scheme,
        KautukaTransport transport,
        string socketRoot)
    {
        if (transport == KautukaTransport.Tcp)
        {
            return new KautukaEndpoint
            {
                Name = name,
                Transport = KautukaTransport.Tcp,
                Port = port,
                BaseUri = new Uri($"{scheme}://127.0.0.1:{port}", UriKind.Absolute),
            };
        }

        // One directory per service: a root-owned parent with a child owned by that service
        // and setgid to the service's dedicated client group, so the directory is the access
        // gate. RuntimeDirectory= cannot provision this, because manager, updateservice and
        // webhost are user-scope units whose RuntimeDirectory lands under /run/user/<uid> at
        // mode 0700, out of reach of the system-scope services. systemd-tmpfiles does it for
        // both scopes uniformly.
        string socketPath = Path.Combine(socketRoot, name, $"{name}.sock");
        ValidateSocketPathLength(socketPath);

        return new KautukaEndpoint
        {
            Name = name,
            Transport = KautukaTransport.UnixSocket,
            Port = port,
            SocketPath = socketPath,

            // No port: the connect callback decides the destination. The host must stay
            // "localhost" so it matches the SAN on the generated server certificates.
            BaseUri = new Uri($"{scheme}://localhost", UriKind.Absolute),
        };
    }

    private static KautukaTransport RequireUnixSocketSupport()
        => Socket.OSSupportsUnixDomainSockets
            ? KautukaTransport.UnixSocket
            : throw new InvalidOperationException(
                $"'{TransportConfigurationKey}' requests Unix sockets, but this platform does not support them. "
                + $"Set '{TransportConfigurationKey}' to 'tcp'.");

    private static void ValidateSocketPathLength(string socketPath)
    {
        int byteCount = System.Text.Encoding.UTF8.GetByteCount(socketPath);
        if (byteCount <= maxUnixSocketPathBytes)
            return;

        throw new InvalidOperationException(
            $"Unix socket path '{socketPath}' is {byteCount} bytes, over the {maxUnixSocketPathBytes}-byte limit. "
            + $"Set '{SocketRootConfigurationKey}' to a shorter directory.");
    }
}
