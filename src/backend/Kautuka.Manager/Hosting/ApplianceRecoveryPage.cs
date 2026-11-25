using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace Kautuka.Manager.Hosting;

internal sealed record ApplianceRecoveryMessage(string Reason, DateTimeOffset? RetryUtc = null, string? NextStep = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ApplianceRecoveryMessage))]
internal sealed partial class ApplianceRecoveryJsonContext : JsonSerializerContext;

internal sealed class ApplianceRecoveryPage(int port)
{
    private static readonly string healthProbe = """
        async function checkWebHost() {
            try {
                const response = await fetch('/health', {
                    headers: { Accept: 'application/json' },
                    cache: 'no-store',
                    credentials: 'omit',
                    redirect: 'error',
                    referrerPolicy: 'no-referrer',
                    signal: AbortSignal.timeout(3000)
                });
                if (response.headers.get('Content-Type')?.startsWith('application/json')) {
                    const status = await response.json();
                    if (response.status === 200 && status.status === 'ok') {
                        location.replace('/');
                        return;
                    }
                    if (response.status === 503 && typeof status.reason === 'string') {
                        document.getElementById('reason').textContent = status.reason;
                        document.getElementById('next').textContent = status.retryUtc
                            ? 'Kautuka will retry at ' + new Date(status.retryUtc).toISOString().slice(11, 19) + ' UTC.'
                            : (status.nextStep ?? '');
                    }
                }
            } catch {
                // Both listeners can be unavailable briefly during port handback.
            }
            setTimeout(checkWebHost, 5000);
        }
        setTimeout(checkWebHost, 5000);
        """.ReplaceLineEndings("\n");

    private static readonly string contentSecurityPolicy =
        $"default-src 'none'; script-src 'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(healthProbe)))}'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'";

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
        if (path == "/health" && !head && context.Request.Headers.Accept == "application/json")
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return context.Response.WriteAsJsonAsync(readStatus(), ApplianceRecoveryJsonContext.Default.ApplianceRecoveryMessage,
                cancellationToken: context.RequestAborted);
        }
        if (path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/alive", StringComparison.OrdinalIgnoreCase)
            || (path != "/" && !context.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase)))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return Task.CompletedTask;
        }

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.ContentSecurityPolicy = contentSecurityPolicy;
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.ContentType = "text/html; charset=utf-8";
        if (head) return Task.CompletedTask;

        ApplianceRecoveryMessage status = readStatus();
        string next = status.RetryUtc is { } retry
            ? $"Kautuka will retry at {retry.ToUniversalTime().ToString("HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)}."
            : HtmlEncoder.Default.Encode(status.NextStep ?? string.Empty);
        return context.Response.WriteAsync(
            $"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>Kautuka recovery</title></head><body><main><h1>Kautuka</h1><p id=\"reason\">{HtmlEncoder.Default.Encode(status.Reason)}</p><p id=\"next\">{next}</p></main><script>{healthProbe}</script></body></html>",
            context.RequestAborted);
    }
}
