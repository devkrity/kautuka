using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Kautuka.Telemetry.Correlation;

public static class KautukaRequestCorrelationExtensions
{
    public static IApplicationBuilder UseKautukaRequestCorrelation(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(async (context, next) =>
        {
            ILogger logger = context.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger("Kautuka.RequestCorrelation");

            string correlationId = Guid.NewGuid().ToString("N");
            context.Response.Headers[KautukaCorrelationScopeExtensions.CorrelationHeaderName] = correlationId;

            using IDisposable scope = logger.BeginKautukaCorrelationScope(
                ("CorrelationId", correlationId),
                ("HttpMethod", context.Request.Method),
                ("RequestPath", context.Request.Path.Value ?? string.Empty));

            await next().ConfigureAwait(false);
        });
    }
}
