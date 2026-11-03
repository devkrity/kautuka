namespace Kautuka.WebHost;

internal static class WebHostMaintenancePage
{
    private const string Html = """
        <!doctype html>
        <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <meta http-equiv="refresh" content="15"><title>Kautuka is updating</title>
        <style>body{font:16px system-ui,sans-serif;background:#f8f7f3;color:#242424;display:grid;place-items:center;min-height:100vh;margin:0}main{max-width:34rem;padding:2rem}h1{font-size:1.8rem}p{line-height:1.6;color:#555}</style>
        </head><body><main><h1>Kautuka is updating</h1><p>An update is in progress. This page will check again automatically.</p></main></body></html>
        """;

    public static IApplicationBuilder UseWebHostMaintenancePage(this IApplicationBuilder app, Func<bool>? isActive = null)
    {
        isActive ??= static () => KautukaMaintenanceMode.IsActive;
        return app.Use(async (context, next) =>
        {
            if (!isActive() || KautukaMaintenanceMode.IsAllowedHealthRequest(context.Request.Path))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.RetryAfter = "15";
            if (HttpMethods.IsGet(context.Request.Method)
                && (context.Request.Path == "/" || context.Request.GetTypedHeaders().Accept?.Any(
                    static value => value.MediaType.Value?.Equals("text/html", StringComparison.OrdinalIgnoreCase) == true) == true))
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync(Html, context.RequestAborted).ConfigureAwait(false);
            }
        });
    }
}
