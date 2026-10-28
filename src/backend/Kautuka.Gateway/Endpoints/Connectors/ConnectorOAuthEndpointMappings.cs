using System.Security.Cryptography;
using System.Text;

using Kautuka.Gateway.Domain.Connectors;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Infrastructure.Connectors;

namespace Kautuka.Gateway.Endpoints.Connectors;

internal static class ConnectorOAuthEndpointMappings
{
    private const string connectorOAuthCookie = "kautuka_connector_oauth";
    public static WebApplication MapConnectorOAuthEndpoints(this WebApplication app)
    {
        app.MapGet("/api/connectors/oauth", async (
            ConnectorOAuthService oauth,
            IGatewayAuthGuard auth,
            HttpContext context,
            CancellationToken ct) =>
        {
            if (!TryGetUser(auth, context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            ConnectorOAuthCatalogResponse status = await oauth.ListStatusAsync(user.Id, ct).ConfigureAwait(false);
            return Results.Json(status, KautukaJsonContext.Default.ConnectorOAuthCatalogResponse);
        });

        app.MapPost("/api/connectors/{connectorId}/oauth/begin", async (
            string connectorId,
            ConnectorOAuthService oauth,
            IGatewayCatalogStore catalog,
            IGatewayAuthGuard auth,
            HttpContext context,
            CancellationToken ct) =>
        {
            if (!TryGetUser(auth, context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            if (!TryAuthorizeConnector(catalog, user, connectorId, out IResult? denied))
            {
                return denied!;
            }

            ConnectorOAuthBeginRequest? request = await context.Request.ReadFromJsonAsync(
                KautukaJsonContext.Default.ConnectorOAuthBeginRequest,
                ct).ConfigureAwait(false);
            ConnectorOAuthBeginResponse begin = oauth.Begin(user.Id, connectorId, request?.Origin);
            if (string.Equals(begin.Status, "redirect", StringComparison.Ordinal)
                && TryReadOAuthState(begin.StartUrl, out string? state))
            {
                context.Response.Cookies.Append(connectorOAuthCookie, state, new CookieOptions
                {
                    HttpOnly = true,
                    IsEssential = true,
                    Secure = RefreshCookieSecurity.IsSecure(context.Request),
                    SameSite = SameSiteMode.Lax,
                    Path = "/api/connectors/oauth",
                    MaxAge = TimeSpan.FromMinutes(10),
                });
            }

            return Results.Json(begin, KautukaJsonContext.Default.ConnectorOAuthBeginResponse);
        });

        app.MapGet("/api/connectors/oauth/callback", async (
            ConnectorOAuthService oauth,
            ConnectorOAuthSessionStore sessions,
            HttpContext context,
            CancellationToken ct) =>
        {
            string? code = context.Request.Query["code"];
            string? state = context.Request.Query["state"];
            string? error = context.Request.Query["error_description"].FirstOrDefault()
                ?? context.Request.Query["error"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(error) || string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
            {
                return ConnectorSignInRedirect(error ?? "Sign-in did not complete.");
            }

            if (!BrowserOwnsOAuthState(context, state))
            {
                return ConnectorSignInRedirect("This sign-in session has expired. Start again from the Connectors page.");
            }

            if (!sessions.TryGet(state, out ConnectorOAuthSession? session)
                || session is null
                || !ConnectorAuthEndpoint.IsLoopbackCallback(session.RedirectUri))
            {
                return ConnectorSignInRedirect("This sign-in session has expired. Start again from the Connectors page.");
            }

            ConnectorOAuthCompleteResponse complete = await oauth.CompleteAsync(session.UserId, code, state, ct).ConfigureAwait(false);
            if (string.Equals(complete.Status, "connected", StringComparison.Ordinal))
            {
                return Results.Redirect("/connectors#signed_in=1");
            }

            return ConnectorSignInRedirect(complete.Message);
        }).AllowAnonymous();

        app.MapPost("/api/connectors/oauth/complete", async (
            ConnectorOAuthService oauth,
            ConnectorOAuthSessionStore sessions,
            IGatewayAuthGuard auth,
            HttpContext context,
            CancellationToken ct) =>
        {
            if (!TryGetUser(auth, context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            ConnectorOAuthCompleteRequest? request = await context.Request.ReadFromJsonAsync(
                KautukaJsonContext.Default.ConnectorOAuthCompleteRequest,
                ct).ConfigureAwait(false);
            string? state = request?.State;
            if (string.IsNullOrWhiteSpace(state)
                || !sessions.TryGet(state, out ConnectorOAuthSession? session)
                || session is null
                || !BrowserOwnsOAuthState(context, state)
                || !ConnectorAuthProfile.TryNormalizeReturnOrigin(context.Request.Headers.Origin.ToString(), out string origin)
                || !string.Equals(origin, session.ReturnOrigin, StringComparison.Ordinal))
            {
                return Results.Json(
                    new ConnectorOAuthCompleteResponse("rejected", "This sign-in session has expired. Start again from the Connectors page."),
                    KautukaJsonContext.Default.ConnectorOAuthCompleteResponse);
            }

            ConnectorOAuthCompleteResponse complete = await oauth.CompleteAsync(
                user.Id,
                request?.Code,
                state,
                ct,
                request?.BrokerTicket).ConfigureAwait(false);
            return Results.Json(complete, KautukaJsonContext.Default.ConnectorOAuthCompleteResponse);
        });

        app.MapDelete("/api/connectors/{connectorId}/oauth", async (
            string connectorId,
            ConnectorOAuthService oauth,
            IGatewayCatalogStore catalog,
            IGatewayAuthGuard auth,
            HttpContext context,
            CancellationToken ct) =>
        {
            if (!TryGetUser(auth, context, out GatewayUser? user) || user is null)
            {
                return Results.Unauthorized();
            }

            if (!TryAuthorizeConnector(catalog, user, connectorId, out IResult? denied))
            {
                return denied!;
            }

            ConnectorOAuthDisconnectResponse disconnect = await oauth.DisconnectAsync(user.Id, connectorId, ct).ConfigureAwait(false);
            return Results.Json(disconnect, KautukaJsonContext.Default.ConnectorOAuthDisconnectResponse);
        });

        return app;
    }

    private static bool TryReadOAuthState(string? startUrl, out string state)
    {
        state = string.Empty;
        if (!Uri.TryCreate(startUrl, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        if (TryReadStateParameter(uri.Query, out state) || TryReadStateParameter(uri.Fragment, out state))
        {
            return true;
        }

        state = string.Empty;
        return false;
    }

    private static bool TryReadStateParameter(string text, out string state)
    {
        state = string.Empty;
        string query = text.TrimStart('?', '#');
        if (query.Length == 0)
        {
            return false;
        }

        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0 || !string.Equals(pair[..separator], "state", StringComparison.Ordinal))
            {
                continue;
            }

            state = Uri.UnescapeDataString(pair[(separator + 1)..]);
            return state.Length > 0;
        }

        return false;
    }

    private static bool BrowserOwnsOAuthState(HttpContext context, string state)
    {
        if (!context.Request.Cookies.TryGetValue(connectorOAuthCookie, out string? cookie) || string.IsNullOrEmpty(cookie))
        {
            return false;
        }

        byte[] expected = Encoding.UTF8.GetBytes(state);
        byte[] actual = Encoding.UTF8.GetBytes(cookie);
        return expected.Length == actual.Length && CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static IResult ConnectorSignInRedirect(string message)
    {
        string text = message.Length > 200 ? message[..200] : message;
        return Results.Redirect("/connectors#error_description=" + Uri.EscapeDataString(text));
    }

    private static bool TryGetUser(IGatewayAuthGuard auth, HttpContext context, out GatewayUser? user)
        => auth.TryAuthorize(context, out user);

    private static bool TryAuthorizeConnector(
        IGatewayCatalogStore catalog,
        GatewayUser user,
        string connectorId,
        out IResult? denied)
    {
        if (!catalog.TryGetMcpServer(connectorId, out GatewayMcpServer? server) || server is null)
        {
            denied = Results.Json(
                new GatewayErrorResponse("Connector was not found."),
                KautukaJsonContext.Default.GatewayErrorResponse,
                statusCode: StatusCodes.Status404NotFound);
            return false;
        }

        if (!GatewayOwnershipAccess.CanAccessConnector(user, server))
        {
            denied = Results.Json(
                new GatewayErrorResponse("Forbidden."),
                KautukaJsonContext.Default.GatewayErrorResponse,
                statusCode: StatusCodes.Status403Forbidden);
            return false;
        }

        denied = null;
        return true;
    }
}
