using System.Net;

using Kautuka.Gateway.Domain.Connectors;
using Kautuka.Gateway.Infrastructure.Connectors;

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

        // Operator maintenance action; never accept a broker URL from the caller.
        app.MapPost("/api/internal/broker/rotate-installation-key", async (
            ConnectorAuthEndpoint endpoint, BrokerInstallationRegistration registration, BoundMailSessionStore sessions, HttpContext context, CancellationToken ct) =>
        {
            if (!IPAddress.IsLoopback(context.Connection.RemoteIpAddress ?? IPAddress.None)) return Results.StatusCode(403);
            if (!Uri.TryCreate(endpoint.Host, UriKind.Absolute, out Uri? origin)) return Results.StatusCode(503);
            if (!sessions.TryBeginBrokerRotation()) return Results.Conflict();
            try
            {
                await registration.RegisterAsync(origin, ct).ConfigureAwait(false);
                await registration.RotateAsync(origin, ct).ConfigureAwait(false);
                return Results.NoContent();
            }
            catch (HttpRequestException) { return Results.StatusCode(503); }
            catch (InvalidOperationException) { return Results.Conflict(); }
            finally { sessions.EndBrokerRotation(); }
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
