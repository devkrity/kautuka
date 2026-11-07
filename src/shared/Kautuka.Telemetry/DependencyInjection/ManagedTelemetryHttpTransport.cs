using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;

using OpenTelemetry.Exporter;

namespace Kautuka.Telemetry.DependencyInjection;

/// <summary>Uses the appliance's private ingestion route without a TCP fallback.</summary>
public static class ManagedTelemetryHttpTransport
{
    public static void Configure(OtlpExporterOptions options, Uri endpoint, string? socketPath)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (string.IsNullOrWhiteSpace(socketPath)) return;
        if (endpoint.Scheme != Uri.UriSchemeHttp || endpoint.Port != 80
            || !endpoint.Host.Equals(Telemetry.KautukaTelemetryDefaults.ManagedHostAuthority, StringComparison.OrdinalIgnoreCase)
            || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0
            || endpoint.AbsolutePath is not ("/api/default/v1/logs" or "/api/default/v1/metrics" or "/api/default/v1/traces")
            || options.Protocol != OtlpExportProtocol.HttpProtobuf)
            throw new InvalidOperationException("Managed telemetry requires its fixed HTTP/protobuf ingestion endpoint.");
        if (!Path.IsPathFullyQualified(socketPath))
            throw new InvalidOperationException("Managed telemetry requires an absolute Unix socket path.");
        options.HttpClientFactory = () => CreateClient(socketPath);
    }

    [SuppressMessage("Reliability", "CA2000", Justification = "HttpClient owns the handler through disposeHandler:true; construction failure disposes it here.")]
    public static HttpClient CreateClient(string socketPath)
    {
        if (!Path.IsPathFullyQualified(socketPath))
            throw new ArgumentException("An absolute Unix socket path is required.", nameof(socketPath));
        var handler = new SocketsHttpHandler();
        try
        {
            handler.UseProxy = false;
            handler.AllowAutoRedirect = false;
            handler.ConnectCallback = async (context, cancellationToken) =>
            {
                if (context.DnsEndPoint.Port != 80 || !context.DnsEndPoint.Host.Equals(
                    Telemetry.KautukaTelemetryDefaults.ManagedHostAuthority, StringComparison.OrdinalIgnoreCase))
                    throw new HttpRequestException("Managed telemetry cannot connect to another authority.");
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };
            return new HttpClient(handler, disposeHandler: true);
        }
        catch
        {
            handler.Dispose();
            throw;
        }
    }
}
