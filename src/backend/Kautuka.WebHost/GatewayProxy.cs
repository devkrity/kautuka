using System.Net;

using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

namespace Kautuka.WebHost;

internal static class GatewayProxy
{
    private static readonly HashSet<string> fixedHopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Trailers",
        "Transfer-Encoding",
        "Upgrade",
    };

    private static readonly HashSet<string> untrustedForwardingHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Forwarded",
        "X-Forwarded-For",
        "X-Forwarded-Host",
        "X-Forwarded-Port",
        "X-Forwarded-Prefix",
        "X-Forwarded-Proto",
        "X-Original-For",
        "X-Original-Host",
        "X-Original-Proto",
        "X-Real-IP",
    };

    private static readonly Uri gatewayPathBase = new("http://gateway.invalid/");

    public static bool TargetsInternalRoute(string requestPath)
    {
        if (string.IsNullOrEmpty(requestPath))
        {
            return false;
        }

        // HttpClient removes dot segments while combining this path with the gateway address.
        Uri combined;
        try
        {
            combined = new Uri(gatewayPathBase, requestPath);
        }
        catch (UriFormatException)
        {
            return true;
        }

        if (!string.Equals(combined.Host, gatewayPathBase.Host, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        const string apiPrefix = "/api/";
        string absolutePath = combined.AbsolutePath;
        if (!absolutePath.StartsWith(apiPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string remainder = absolutePath[apiPrefix.Length..];
        return remainder.Equals("internal", StringComparison.OrdinalIgnoreCase)
            || remainder.StartsWith("internal/", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task ForwardAsync(HttpContext context, HttpClient httpClient, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);

        string requestUri = context.Request.Path + context.Request.QueryString;
        using var proxyRequest = new HttpRequestMessage(new HttpMethod(context.Request.Method), requestUri);
        bool canHaveBody = context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody
            ?? context.Request.ContentLength > 0
            || context.Request.Headers.ContainsKey("Transfer-Encoding");
        if (canHaveBody)
            proxyRequest.Content = new StreamContent(context.Request.Body);

        HashSet<string> requestConnectionHeaders = ReadConnectionHeaderTokens(context.Request.Headers.Connection);
        foreach ((string key, StringValues values) in context.Request.Headers)
        {
            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase)
                || fixedHopByHopHeaders.Contains(key)
                || requestConnectionHeaders.Contains(key)
                || untrustedForwardingHeaders.Contains(key))
            {
                continue;
            }

            if (!proxyRequest.Headers.TryAddWithoutValidation(key, (IEnumerable<string?>)values))
                proxyRequest.Content?.Headers.TryAddWithoutValidation(key, (IEnumerable<string?>)values);
        }

        proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-Proto", context.Request.Scheme);
        if (context.Connection.RemoteIpAddress is IPAddress remoteAddress)
            proxyRequest.Headers.TryAddWithoutValidation("X-Forwarded-For", remoteAddress.ToString());

        try
        {
            using HttpResponseMessage response = await httpClient.SendAsync(
                proxyRequest,
                HttpCompletionOption.ResponseHeadersRead,
                context.RequestAborted).ConfigureAwait(false);

            context.Response.StatusCode = (int)response.StatusCode;
            HashSet<string> responseConnectionHeaders = response.Headers.TryGetValues("Connection", out IEnumerable<string>? values)
                ? ReadConnectionHeaderTokens(new StringValues(values.ToArray()))
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CopyResponseHeaders(response.Headers, context.Response.Headers, responseConnectionHeaders);
            CopyResponseHeaders(response.Content.Headers, context.Response.Headers, responseConnectionHeaders);

            using Stream responseStream = await response.Content.ReadAsStreamAsync(context.RequestAborted).ConfigureAwait(false);
            await responseStream.CopyToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!context.RequestAborted.IsCancellationRequested)
        {
            await WriteProxyErrorAsync(context, logger, StatusCodes.Status504GatewayTimeout, ex).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            GatewayProxyLog.RequestAborted(logger, context.Request.Method, context.Request.Path.Value ?? string.Empty);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            await WriteProxyErrorAsync(context, logger, StatusCodes.Status502BadGateway, ex).ConfigureAwait(false);
        }
    }

    private static void CopyResponseHeaders(
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> source,
        IHeaderDictionary destination,
        HashSet<string> connectionHeaders)
    {
        foreach ((string key, IEnumerable<string> values) in source)
        {
            if (fixedHopByHopHeaders.Contains(key) || connectionHeaders.Contains(key))
                continue;

            destination[key] = new StringValues(values.ToArray());
        }
    }

    private static HashSet<string> ReadConnectionHeaderTokens(StringValues values)
        => values
            .SelectMany(static value => (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static async Task WriteProxyErrorAsync(HttpContext context, ILogger logger, int statusCode, Exception exception)
    {
        string requestPath = context.Request.Path.Value ?? string.Empty;
        if (context.Response.HasStarted)
        {
            GatewayProxyLog.ResponseStartedFailure(logger, context.Request.Method, requestPath, exception);
            return;
        }

        GatewayProxyLog.TransportFailure(logger, context.Request.Method, requestPath, statusCode, exception);
        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync("{\"error\":\"gateway_unavailable\"}", context.RequestAborted).ConfigureAwait(false);
    }
}

internal static partial class GatewayProxyLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Proxy request aborted by client for {Method} {Path}.")]
    public static partial void RequestAborted(ILogger logger, string method, string path);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Proxy request failed after response started for {Method} {Path}.")]
    public static partial void ResponseStartedFailure(ILogger logger, string method, string path, Exception exception);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Proxy transport failed for {Method} {Path} with downstream status {StatusCode}.")]
    public static partial void TransportFailure(ILogger logger, string method, string path, int statusCode, Exception exception);
}
