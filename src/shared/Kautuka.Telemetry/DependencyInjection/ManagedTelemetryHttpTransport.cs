using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

using OpenTelemetry.Exporter;

namespace Kautuka.Telemetry.DependencyInjection;

/// <summary>Uses the appliance's private ingestion route without a TCP fallback.</summary>
public static class ManagedTelemetryHttpTransport
{
    public static void Configure(OtlpExporterOptions options, Uri endpoint, string? socketPath, string? authorizationHeaderFilePath = null)
        => ConfigureCore(options, endpoint, socketPath, authorizationHeaderFilePath, null);

    internal static void ConfigureForTesting(OtlpExporterOptions options, Uri endpoint, string authorizationHeaderFilePath,
        Func<HttpMessageHandler> coreTransportHandlerFactory)
        => ConfigureCore(options, endpoint, null, authorizationHeaderFilePath, coreTransportHandlerFactory);

    private static void ConfigureCore(OtlpExporterOptions options, Uri endpoint, string? socketPath,
        string? authorizationHeaderFilePath, Func<HttpMessageHandler>? coreTransportHandlerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(endpoint);
        bool managedEndpoint = endpoint.Host.Equals(Telemetry.KautukaTelemetryDefaults.ManagedHostAuthority, StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(socketPath))
        {
            if (managedEndpoint)
                throw new InvalidOperationException("Managed telemetry requires its fixed Unix socket route; TCP fallback is disabled.");
            if (!string.IsNullOrWhiteSpace(authorizationHeaderFilePath))
            {
                if (!Path.IsPathFullyQualified(authorizationHeaderFilePath)
                    || options.Protocol != OtlpExportProtocol.HttpProtobuf
                    || !IsCoreTelemetryEndpoint(endpoint))
                    throw new InvalidOperationException("Credentialed core telemetry requires its fixed private IPv4 HTTP/protobuf endpoint.");
                options.HttpClientFactory = () => CreateCoreClient(endpoint, authorizationHeaderFilePath, coreTransportHandlerFactory?.Invoke());
            }
            return;
        }
        if (coreTransportHandlerFactory is not null)
            throw new InvalidOperationException("The core telemetry test transport cannot be used for Unix socket routes.");
        if (endpoint.Scheme != Uri.UriSchemeHttp || endpoint.Port != 80
            || !managedEndpoint
            || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0
            || endpoint.AbsolutePath is not ("/api/default/v1/logs" or "/api/default/v1/metrics" or "/api/default/v1/traces")
            || options.Protocol != OtlpExportProtocol.HttpProtobuf)
            throw new InvalidOperationException("Managed telemetry requires its fixed HTTP/protobuf ingestion endpoint.");
        if (!Path.IsPathFullyQualified(socketPath))
            throw new InvalidOperationException("Managed telemetry requires an absolute Unix socket path.");
        if (!string.IsNullOrWhiteSpace(authorizationHeaderFilePath) && !Path.IsPathFullyQualified(authorizationHeaderFilePath))
            throw new InvalidOperationException("Managed telemetry credentials require an absolute protected file path.");
        options.HttpClientFactory = () => CreateClient(socketPath, authorizationHeaderFilePath);
    }

    private static bool IsCoreTelemetryEndpoint(Uri endpoint)
    {
        if (endpoint.Scheme != Uri.UriSchemeHttp || endpoint.Port != 5080
            || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0
            || endpoint.AbsolutePath is not ("/api/default/v1/logs" or "/api/default/v1/metrics" or "/api/default/v1/traces")
            || !IPAddress.TryParse(endpoint.Host, out IPAddress? address) || address.AddressFamily != AddressFamily.InterNetwork)
            return false;
        byte[] bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
            || bytes[0] == 192 && bytes[1] == 168;
    }

    [SuppressMessage("Reliability", "CA2000", Justification = "HttpClient owns the composed handlers through disposeHandler:true; construction failure disposes the inner handler.")]
    private static HttpClient CreateCoreClient(Uri endpoint, string authorizationHeaderFilePath, HttpMessageHandler? innerHandler)
    {
        innerHandler ??= new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        try
        {
            return new HttpClient(new CoreIngestionAuthorizationHandler(endpoint, authorizationHeaderFilePath, innerHandler), disposeHandler: true);
        }
        catch
        {
            innerHandler.Dispose();
            throw;
        }
    }

    [SuppressMessage("Reliability", "CA2000", Justification = "HttpClient owns the handler through disposeHandler:true; construction failure disposes it here.")]
    public static HttpClient CreateClient(string socketPath, string? authorizationHeaderFilePath = null)
    {
        if (!Path.IsPathFullyQualified(socketPath))
            throw new ArgumentException("An absolute Unix socket path is required.", nameof(socketPath));
        if (!string.IsNullOrWhiteSpace(authorizationHeaderFilePath) && !Path.IsPathFullyQualified(authorizationHeaderFilePath))
            throw new ArgumentException("An absolute protected file path is required.", nameof(authorizationHeaderFilePath));
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
            HttpMessageHandler pipeline = string.IsNullOrWhiteSpace(authorizationHeaderFilePath)
                ? handler
                : new ManagedIngestionAuthorizationHandler(authorizationHeaderFilePath, handler);
            return new HttpClient(pipeline, disposeHandler: true);
        }
        catch
        {
            handler.Dispose();
            throw;
        }
    }

    private sealed class ManagedIngestionAuthorizationHandler(string path, HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
            => SendAsync(request, cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri? endpoint = request.RequestUri;
            if (request.Method != HttpMethod.Post || endpoint is null || endpoint.Scheme != Uri.UriSchemeHttp || endpoint.Port != 80
                || !endpoint.Host.Equals(Telemetry.KautukaTelemetryDefaults.ManagedHostAuthority, StringComparison.OrdinalIgnoreCase)
                || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0
                || endpoint.AbsolutePath is not ("/api/default/v1/logs" or "/api/default/v1/metrics" or "/api/default/v1/traces"))
                throw new HttpRequestException("Managed telemetry credentials cannot be sent to another endpoint.");

            string value;
            try
            {
                value = (await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)).Trim();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new HttpRequestException("Managed telemetry credentials are unavailable; no request was sent.");
            }
            if (!AuthenticationHeaderValue.TryParse(value, out AuthenticationHeaderValue? authorization)
                || !string.Equals(authorization.Scheme, "Basic", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(authorization.Parameter))
                throw new HttpRequestException("Managed telemetry credentials are unavailable or invalid; no request was sent.");

            request.Headers.Authorization = authorization;
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class CoreIngestionAuthorizationHandler(Uri endpoint, string path, HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
            => SendAsync(request, cancellationToken).ConfigureAwait(false).GetAwaiter().GetResult();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri? target = request.RequestUri;
            if (request.Method != HttpMethod.Post || target is null
                || target.Scheme != endpoint.Scheme || target.Authority != endpoint.Authority
                || target.AbsolutePath != endpoint.AbsolutePath || target.Query.Length != 0 || target.Fragment.Length != 0)
                throw new HttpRequestException("Core telemetry credentials cannot be sent to another endpoint.");

            string value;
            try
            {
                value = (await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)).Trim();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new HttpRequestException("Core telemetry credentials are unavailable; no request was sent.");
            }
            if (!AuthenticationHeaderValue.TryParse(value, out AuthenticationHeaderValue? authorization)
                || !string.Equals(authorization.Scheme, "Basic", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(authorization.Parameter))
                throw new HttpRequestException("Core telemetry credentials are unavailable or invalid; no request was sent.");

            request.Headers.Authorization = authorization;
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}
