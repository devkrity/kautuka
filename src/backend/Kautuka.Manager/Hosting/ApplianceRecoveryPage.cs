using System.Globalization;
using System.Net;
using System.Text.Encodings.Web;

using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Kautuka.Manager.Hosting;

internal sealed record ApplianceRecoveryMessage(string Reason, DateTimeOffset? RetryUtc = null, string? NextStep = null);

internal sealed class ApplianceRecoveryPage(int port)
{
    private WebApplication? listener;

    internal async Task StartAsync(Func<ApplianceRecoveryMessage> readStatus, CancellationToken token)
    {
        if (listener is not null) return;

        // Keep this listener separate from Manager's authenticated gRPC routes and endpoint configuration.
        WebApplicationBuilder builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.UseKestrelCore().UseSockets().ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 0;
            options.Limits.MaxConcurrentConnections = 16;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
            options.Listen(IPAddress.Loopback, port, endpoint => endpoint.Protocols = HttpProtocols.Http1);
        });
        WebApplication app = builder.Build();
        app.Run(context => RespondAsync(context, readStatus));
        try
        {
            await app.StartAsync(token).ConfigureAwait(false);
            listener = app;
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal async Task StopAsync()
    {
        if (listener is not { } app) return;

        try { await app.StopAsync().ConfigureAwait(false); }
        finally
        {
            await app.DisposeAsync().ConfigureAwait(false);
            listener = null;
        }
    }

    private Task RespondAsync(HttpContext context, Func<ApplianceRecoveryMessage> readStatus)
    {
        context.Response.Headers.CacheControl = "no-store";
        HostString host = context.Request.Host;
        if (host.Port != port || !(host.Host == "127.0.0.1" || string.Equals(host.Host, "localhost", StringComparison.OrdinalIgnoreCase)))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return Task.CompletedTask;
        }

        bool head = HttpMethods.IsHead(context.Request.Method);
        if (!head && !HttpMethods.IsGet(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = "GET, HEAD";
            return Task.CompletedTask;
        }

        PathString path = context.Request.Path;
        if (path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/alive", StringComparison.OrdinalIgnoreCase)
            || (path != "/" && !context.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase)))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'; base-uri 'none'";
        context.Response.ContentType = "text/html; charset=utf-8";
        if (head) return Task.CompletedTask;

        ApplianceRecoveryMessage status = readStatus();
        string next = status.RetryUtc is { } retry
            ? $"<p>Kautuka will retry at <time datetime=\"{retry.ToString("O", CultureInfo.InvariantCulture)}\">{retry.ToString("HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)}</time>.</p>"
            : status.NextStep is { } step ? $"<p>{HtmlEncoder.Default.Encode(step)}</p>" : string.Empty;
        return context.Response.WriteAsync(
            $"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><meta http-equiv=\"refresh\" content=\"5\"><title>Kautuka recovery</title></head><body><main><h1>Kautuka</h1><p>{HtmlEncoder.Default.Encode(status.Reason)}</p>{next}</main></body></html>",
            context.RequestAborted);
    }
}
