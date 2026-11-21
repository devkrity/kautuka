using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Kautuka.Gateway.Infrastructure.Egress;

/// <summary>Identity attached to every CONNECT in a partitioned connection pool.</summary>
internal sealed record GatewayEgressScope(string User, string? Agent, string? Connector, string Purpose, string? Correlation);

/// <summary>HTTP transport through an authenticated, scope-bound Envoy CONNECT listener.</summary>
internal sealed class GatewayConnectTransport : IDisposable
{
    private const int MaximumResponseHeaderBytes = 16 * 1024;
    private readonly IPAddress envoyAddress;
    private readonly int envoyPort;
    private readonly string envoyServerName;
    private readonly X509Certificate2 trustedRoot;
    private readonly X509Certificate2 clientCertificate;
    private readonly int maximumScopes;
    private readonly Dictionary<GatewayEgressScope, Pool> pools = [];
    private readonly Lock poolLock = new();
    private bool disposed;

    internal GatewayConnectTransport(string envoyAddress, int envoyPort, string envoyServerName,
        X509Certificate2 trustedRoot, X509Certificate2 clientCertificate, int maximumScopes = 128)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(envoyAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(envoyServerName);
        ArgumentNullException.ThrowIfNull(trustedRoot);
        ArgumentNullException.ThrowIfNull(clientCertificate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumScopes);
        if (envoyPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(envoyPort));
        if (!clientCertificate.HasPrivateKey) throw new ArgumentException("A client certificate with private key is required.", nameof(clientCertificate));
        if (!IPAddress.TryParse(envoyAddress, out IPAddress? parsedAddress) || parsedAddress.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("Envoy address must be an IPv4 literal.", nameof(envoyAddress));
        this.envoyAddress = parsedAddress;
        this.envoyPort = envoyPort;
        this.envoyServerName = envoyServerName;
        this.trustedRoot = trustedRoot;
        this.clientCertificate = clientCertificate;
        this.maximumScopes = maximumScopes;
    }

#pragma warning disable CA2000 // Handlers are owned by pool entries and disposed on eviction or transport disposal.
    internal async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, GatewayEgressScope scope, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(scope);
        if (request.RequestUri is not { IsAbsoluteUri: true } uri ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) || !string.IsNullOrEmpty(uri.UserInfo) ||
            request.Headers.Host is not null || uri.HostNameType == UriHostNameType.IPv6 || uri.Host.EndsWith('.'))
            throw new InvalidOperationException("Gateway egress requires an HTTP or HTTPS URI with a canonical host and no user info or Host override.");
        ValidateScope(scope);
        // HTTP/3 uses QUIC and can bypass ConnectCallback. Preserve 1.1/2
        // requests unless their policy could negotiate a higher protocol.
        if (request.Version.Major >= 3 || request.VersionPolicy == HttpVersionPolicy.RequestVersionOrHigher)
        {
            request.Version = HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        }
        Pool pool;
        lock (poolLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!pools.TryGetValue(scope, out pool!))
            {
                if (pools.Count >= maximumScopes)
                {
                    KeyValuePair<GatewayEgressScope, Pool>? idle = pools.Where(static entry => entry.Value.Active == 0)
                        .OrderBy(static entry => entry.Value.LastUsed).Cast<KeyValuePair<GatewayEgressScope, Pool>?>().FirstOrDefault();
                    if (idle is null) throw new InvalidOperationException("Gateway egress scope pool limit reached.");
                    pools.Remove(idle.Value.Key);
                    idle.Value.Value.Invoker.Dispose();
                }
                // A record copy captures the explicit caller scope before pool selection.
                GatewayEgressScope snapshot = scope with { };
                var handler = new SocketsHttpHandler
                {
                    UseProxy = false,
                    AllowAutoRedirect = false,
                    UseCookies = false,
                    ConnectTimeout = TimeSpan.FromSeconds(15),
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
                    SslOptions = new SslClientAuthenticationOptions
                    {
                        CertificateChainPolicy = new X509ChainPolicy
                        {
                            DisableCertificateDownloads = true,
                            RevocationMode = X509RevocationMode.NoCheck,
                        },
                    },
                    ConnectCallback = (context, token) => ConnectAsync(context.DnsEndPoint, snapshot, token),
                };
                pool = new Pool(new HttpMessageInvoker(handler, disposeHandler: true));
                pools.Add(snapshot, pool);
            }
            pool.Active++;
        }
        try
        {
            HttpResponseMessage response = await pool.Invoker.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.Content = new TrackedContent(response.Content, () => Release(pool));
            return response;
        }
        catch { Release(pool); throw; }
    }

#pragma warning restore CA2000
    private void Release(Pool pool)
    {
        lock (poolLock)
        {
            pool.Active--;
            pool.LastUsed = Environment.TickCount64;
        }
    }

    private sealed class Pool(HttpMessageInvoker invoker)
    {
        internal HttpMessageInvoker Invoker { get; } = invoker;
        internal int Active { get; set; }
        internal long LastUsed { get; set; } = Environment.TickCount64;
    }

    private sealed class TrackedContent : HttpContent
    {
        private readonly HttpContent inner;
        private readonly Action release;
        private int released;
        internal TrackedContent(HttpContent inner, Action release)
        {
            this.inner = inner;
            this.release = release;
            foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => inner.CopyToAsync(stream);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) => inner.CopyToAsync(stream, cancellationToken);
        protected override Task<Stream> CreateContentReadStreamAsync() => inner.ReadAsStreamAsync();
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => inner.ReadAsStreamAsync(cancellationToken);
        protected override bool TryComputeLength(out long length)
        {
            if (inner.Headers.ContentLength is long known) { length = known; return true; }
            length = 0; return false;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref released, 1) == 0)
            {
                try { inner.Dispose(); }
                finally { release(); }
            }
            base.Dispose(disposing);
        }
    }

#pragma warning disable CA2000 // The returned SslStream owns the socket; failures dispose it.
    private async ValueTask<Stream> ConnectAsync(DnsEndPoint target, GatewayEgressScope scope, CancellationToken token)
    {
        string authority = FormatAuthority(target);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(envoyAddress, envoyPort, token).ConfigureAwait(false);
            var tls = new SslStream(new NetworkStream(socket, ownsSocket: true), leaveInnerStreamOpen: false, ValidateServer);
            try
            {
                var tlsOptions = new SslClientAuthenticationOptions
                {
                    TargetHost = envoyServerName,
                    ClientCertificates = new X509CertificateCollection { clientCertificate },
                    ApplicationProtocols = [SslApplicationProtocol.Http11],
                    CertificateChainPolicy = CreateGatewayChainPolicy(),
                };
                await tls.AuthenticateAsClientAsync(tlsOptions, token).ConfigureAwait(false);
                string message = $"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n" +
                    $"x-kautuka-user: {scope.User}\r\n" + Header("x-kautuka-agent", scope.Agent) +
                    Header("x-kautuka-connector", scope.Connector) + $"x-kautuka-purpose: {scope.Purpose}\r\n" +
                    Header("x-kautuka-correlation", scope.Correlation) + "\r\n";
                await tls.WriteAsync(Encoding.ASCII.GetBytes(message), token).ConfigureAwait(false);
                await ReadConnectResponseAsync(tls, token).ConfigureAwait(false);
                return tls;
            }
            catch { await tls.DisposeAsync().ConfigureAwait(false); throw; }
        }
        catch { socket.Dispose(); throw; }
    }

#pragma warning restore CA2000
    private bool ValidateServer(object sender, X509Certificate? certificate, X509Chain? suppliedChain, SslPolicyErrors errors)
    {
        if (certificate is null || errors != SslPolicyErrors.None) return false;
        using var leaf = new X509Certificate2(certificate);
        X509SubjectAlternativeNameExtension? san = leaf.Extensions.OfType<X509SubjectAlternativeNameExtension>().SingleOrDefault();
        return san is not null && san.EnumerateDnsNames().Any(name => string.Equals(name, envoyServerName, StringComparison.OrdinalIgnoreCase));
    }

    private X509ChainPolicy CreateGatewayChainPolicy()
    {
        var policy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            DisableCertificateDownloads = true,
            RevocationMode = X509RevocationMode.NoCheck,
        };
        policy.CustomTrustStore.Add(trustedRoot);
        policy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        return policy;
    }

#pragma warning disable CA1308 // DNS authority is canonically lower-case.
    private static string FormatAuthority(DnsEndPoint target)
    {
        if (target.Port is < 1 or > 65535) throw new InvalidOperationException("Invalid destination port.");
        string host;
        if (IPAddress.TryParse(target.Host, out IPAddress? ip))
        {
            if (ip.AddressFamily != AddressFamily.InterNetwork) throw new InvalidOperationException("IPv6 destinations are not supported.");
            host = ip.ToString();
        }
        else
        {
            host = new IdnMapping().GetAscii(target.Host).ToLowerInvariant();
            if (host.EndsWith('.') || Uri.CheckHostName(host) != UriHostNameType.Dns) throw new InvalidOperationException("Invalid destination hostname.");
        }
        return string.Create(CultureInfo.InvariantCulture, $"{host}:{target.Port}");
    }

#pragma warning restore CA1308
    private static string Header(string name, string? value) => value is null ? string.Empty : $"{name}: {value}\r\n";
    private static void ValidateScope(GatewayEgressScope scope)
    {
        ValidateValue(scope.User, required: true);
        ValidateValue(scope.Agent, required: false);
        ValidateValue(scope.Connector, required: false);
        ValidateValue(scope.Purpose, required: true);
        ValidateValue(scope.Correlation, required: false);
    }
    private static void ValidateValue(string? value, bool required)
    {
        if (value is null && !required) return;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(static c => c is <= ' ' or > '~' or ','))
            throw new InvalidOperationException("Invalid gateway egress scope value.");
    }

    private static async Task ReadConnectResponseAsync(Stream stream, CancellationToken token)
    {
        byte[] bytes = new byte[MaximumResponseHeaderBytes];
        int length = 0;
        while (length < bytes.Length)
        {
            int read = await stream.ReadAsync(bytes.AsMemory(length, 1), token).ConfigureAwait(false);
            if (read == 0) throw new IOException("CONNECT response ended before headers.");
            length += read;
            if (length >= 4 && bytes.AsSpan(length - 4, 4).SequenceEqual("\r\n\r\n"u8)) break;
        }
        if (length == bytes.Length && !bytes.AsSpan(length - 4, 4).SequenceEqual("\r\n\r\n"u8)) throw new IOException("CONNECT response headers exceed limit.");
        if (bytes.AsSpan(0, length).ContainsAnyExceptInRange((byte)0x20, (byte)0x7e))
        {
            for (int i = 0; i < length; i++)
                if (bytes[i] is < 0x20 or > 0x7e && bytes[i] is not (byte)'\r' and not (byte)'\n' and not (byte)'\t') throw new IOException("Invalid CONNECT response headers.");
        }
        string[] lines = Encoding.ASCII.GetString(bytes, 0, length - 4).Split("\r\n", StringSplitOptions.None);
        if (!lines[0].StartsWith("HTTP/1.1 200 ", StringComparison.Ordinal) && lines[0] != "HTTP/1.1 200") throw new IOException("CONNECT was refused.");
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0 || char.IsWhiteSpace(line[0])) throw new IOException("Malformed CONNECT response header.");
            string name = line[..colon];
            string value = line[(colon + 1)..].Trim();
            if (name.Equals("transfer-encoding", StringComparison.OrdinalIgnoreCase) ||
                (name.Equals("content-length", StringComparison.OrdinalIgnoreCase) && value != "0")) throw new IOException("Unexpected CONNECT response body.");
        }
    }

    public void Dispose()
    {
        lock (poolLock)
        {
            if (disposed) return;
            disposed = true;
            foreach (Pool pool in pools.Values) pool.Invoker.Dispose();
            pools.Clear();
        }
    }
}
