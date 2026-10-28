using System.Net;

namespace Kautuka.Gateway.Endpoints.Internal;

internal static class GatewayManagementEndpointMappings
{
    public static WebApplication MapGatewayManagementEndpoints(this WebApplication app)
    {
        // Graceful shutdown — loopback only, called by the desktop host on quit.
        app.MapPost("/api/internal/shutdown", (IHostApplicationLifetime lifetime, HttpContext context) =>
        {
            if (!IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? IPAddress.None))
                return Results.StatusCode(403);

            lifetime.StopApplication();
            return Results.Accepted();
        }).RequireAuthorization("AdminManagement");

        app.MapPost("/api/internal/runtime/desired-state/rehydrate", async (
            IWorkspaceDesiredStatePublisher desiredStatePublisher,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            if (!IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? IPAddress.None))
                return Results.StatusCode(403);


            bool published = (await desiredStatePublisher.TryPublishCurrentAsync("gateway-rehydrate-response", cancellationToken).ConfigureAwait(false)).Accepted;
            return published
                ? Results.Accepted()
                : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }).RequireAuthorization("AdminManagement");

        return app;
    }
}
