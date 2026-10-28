using Kautuka.Control.Core.Config;

using Microsoft.Extensions.FileProviders;

namespace Kautuka.Control.Desktop;

/// <summary>
/// Local Kestrel server — the single UI entry point in all environments (dev and prod).
///  1. Serves static files from web/dist at /  (SPA with index.html fallback)
///  2. Proxies /api/** → Gateway
/// Built with CreateSlimBuilder — AOT-safe minimal API.
/// </summary>
internal static class DesktopServer
{
    public static void Run(DesktopConfig cfg, string webDist,
        ManualResetEventSlim? serverReady, CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(cfg.UiUrl().AbsoluteUri);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services.AddHttpClient("gateway", c =>
        {
            c.BaseAddress = cfg.GatewayUrl();
            c.Timeout = TimeSpan.FromSeconds(120);
        });

        var app = builder.Build();

        // Serve web/dist — SPA with fallback to index.html
        var fileProvider = new PhysicalFileProvider(webDist);
        app.UseDefaultFiles(new DefaultFilesOptions
        {
            FileProvider = fileProvider,
            RequestPath = string.Empty,
        });
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = fileProvider,
            RequestPath = string.Empty,
        });

        // /api/** reverse proxy
        app.Map("/api/{**path}", async (HttpContext ctx, IHttpClientFactory factory) =>
        {
            var client = factory.CreateClient("gateway");
            var path = ctx.Request.Path + ctx.Request.QueryString;
            var proxyReq = new HttpRequestMessage(
                new HttpMethod(ctx.Request.Method), path);

            // Copy request body
            if (ctx.Request.ContentLength > 0 || ctx.Request.Headers.ContainsKey("Transfer-Encoding"))
                proxyReq.Content = new StreamContent(ctx.Request.Body);

            // Copy request headers (except Host)
            foreach (var (key, values) in ctx.Request.Headers)
            {
                if (key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
                if (!proxyReq.Headers.TryAddWithoutValidation(key, (IEnumerable<string?>)values))
                    proxyReq.Content?.Headers.TryAddWithoutValidation(key, (IEnumerable<string?>)values);
            }

            try
            {
                using var resp = await client.SendAsync(proxyReq,
                    HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted).ConfigureAwait(false);

                ctx.Response.StatusCode = (int)resp.StatusCode;

                // Copy response headers — skip hop-by-hop headers that must not be forwarded
                static bool IsHopByHop(string name) =>
                    name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Connection", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("TE", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Trailers", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("Upgrade", StringComparison.OrdinalIgnoreCase);

                foreach (var (key, values) in resp.Headers)
                    if (!IsHopByHop(key))
                        ctx.Response.Headers.TryAdd(key, new Microsoft.Extensions.Primitives.StringValues(values.ToArray()));
                foreach (var (key, values) in resp.Content.Headers)
                    if (!IsHopByHop(key))
                        ctx.Response.Headers.TryAdd(key, new Microsoft.Extensions.Primitives.StringValues(values.ToArray()));

                await resp.Content.CopyToAsync(ctx.Response.Body, ctx.RequestAborted).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                ctx.Response.StatusCode = 502;
                await ctx.Response.WriteAsync($"Gateway proxy error: {ex.Message}",
                    ctx.RequestAborted).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                ctx.Response.StatusCode = 502;
                await ctx.Response.WriteAsync($"Gateway proxy error: {ex.Message}",
                    ctx.RequestAborted).ConfigureAwait(false);
            }
            catch (TaskCanceledException ex)
            {
                ctx.Response.StatusCode = 502;
                await ctx.Response.WriteAsync($"Gateway proxy error: {ex.Message}",
                    ctx.RequestAborted).ConfigureAwait(false);
            }
        });

        // SPA catch-all: serve index.html for all unmatched routes (client-side routing)
        app.MapFallbackToFile("index.html", new StaticFileOptions { FileProvider = fileProvider });

        var lifetime = app.Services.GetService(typeof(IHostApplicationLifetime))
                       as IHostApplicationLifetime;
        lifetime?.ApplicationStarted.Register(() => serverReady?.Set());

        // Register CancellationToken to stop the host gracefully
        ct.Register(() => lifetime?.StopApplication());
        app.Run();
    }
}
