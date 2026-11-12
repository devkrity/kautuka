using Kautuka.Gateway.Domain.Connectors;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Infrastructure.Connectors;

using Microsoft.AspNetCore.Mvc;

namespace Kautuka.Gateway.Endpoints.Connectors;

internal static partial class ConnectorOAuthEndpointMappings
{
    private const string BoundMailCookie = "kautuka_bound_mail";

    private static void MapBoundMailEndpoints(WebApplication app)
    {
        app.MapPost("/api/connectors/{connectorId}/oauth/bound-begin", BeginBoundMail)
            .RequireRateLimiting("MailHandoff").WithMetadata(new RequestSizeLimitAttribute(16384));
        app.MapPost("/api/connectors/oauth/bound-bind", BindBoundMail)
            .RequireRateLimiting("MailHandoff").WithMetadata(new RequestSizeLimitAttribute(16384));
        app.MapPost("/api/connectors/oauth/bound-complete", CompleteBoundMail)
            .RequireRateLimiting("MailHandoffPoll").WithMetadata(new RequestSizeLimitAttribute(16384));
        app.MapPost("/api/connectors/oauth/bound-cancel", CancelBoundMail)
            .RequireRateLimiting("MailHandoff").WithMetadata(new RequestSizeLimitAttribute(16384));
    }

    private static async Task<IResult> CancelBoundMail(BoundExternalOperationRequest? body, HttpContext context, IGatewayAuthGuard auth,
        IGatewayCatalogStore catalog, BoundMailSessionStore sessions, BrokerMailCoordinator coordinator, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (body is null || !TryGetUser(auth, context, out GatewayUser? user) || user is null
            || !ConnectorAuthProfile.TryNormalizeReturnOrigin(context.Request.Headers.Origin.ToString(), out string origin)
            || !sessions.TryGetForCancellation(body.State, user.Id, origin, context.Request.Cookies[BoundMailCookie + "_" + body.State], body.TabHandle, DateTimeOffset.UtcNow, out BoundMailSession? session)
            || session is null || !TryAuthorizeConnector(catalog, user, session.CatalogConnectorId, out _)) return Results.Unauthorized();
        if (!sessions.Cancel(body.State, user.Id, origin, context.Request.Cookies[BoundMailCookie + "_" + body.State], body.TabHandle, DateTimeOffset.UtcNow)) return Results.Conflict();
        try
        {
            await coordinator.CancelFlowAsync(session, ct).ConfigureAwait(false);
            sessions.RemoveCancelled(session);
            return Results.NoContent();
        }
        catch (BrokerMailRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            return Results.Conflict();
        }
        catch (HttpRequestException e) { return BoundMailBrokerFailure(context, e); }
    }

    private static async Task<IResult> CompleteBoundMail(BoundExternalOperationRequest? body, HttpContext context,
        IGatewayAuthGuard auth, IGatewayCatalogStore catalog, BoundMailSessionStore sessions, BrokerMailCoordinator coordinator,
        ConnectorAuthCatalog profiles, ConnectorOAuthService oauth, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (body is null || !TryGetUser(auth, context, out GatewayUser? user) || user is null
            || !ConnectorAuthProfile.TryNormalizeReturnOrigin(context.Request.Headers.Origin.ToString(), out string origin)
            || !sessions.TryGet(body.State, user.Id, origin, context.Request.Cookies[BoundMailCookie + "_" + body.State], body.TabHandle, DateTimeOffset.UtcNow, out BoundMailSession? checkedSession)
            || checkedSession is null || !TryAuthorizeConnector(catalog, user, checkedSession.CatalogConnectorId, out _)) return Results.Unauthorized();
        if (checkedSession.Completed)
            return Results.Json(new ConnectorOAuthCompleteResponse("connected", "Signed in."), KautukaJsonContext.Default.ConnectorOAuthCompleteResponse);
        if (!sessions.TryBegin(body.State, user.Id, origin, context.Request.Cookies[BoundMailCookie + "_" + body.State], body.TabHandle,
            DateTimeOffset.UtcNow, out BoundMailSession? session) || session is null)
            return checkedSession.BrowserConfirmed ? Results.StatusCode(StatusCodes.Status202Accepted) : Results.Unauthorized();
        bool completed = false;
        using IDisposable nacScope = Kautuka.NetworkAccess.Client.NetworkAccessContext.BeginScope(
            userId: user.Id,
            connectorId: session.CatalogConnectorId,
            purpose: "connector-oauth",
            service: "connector-auth");
        try
        {
            bool StillAuthorized() => TryGetUser(auth, context, out GatewayUser? current) && current is not null && current.Id == user.Id
                && TryAuthorizeConnector(catalog, current, session.CatalogConnectorId, out _)
                && sessions.TryGet(body.State, current.Id, origin, context.Request.Cookies[BoundMailCookie + "_" + body.State], body.TabHandle,
                    DateTimeOffset.UtcNow, out BoundMailSession? actual) && ReferenceEquals(actual, session);
            bool ready = await coordinator.CompleteBoundAsync(session, StillAuthorized, ct).ConfigureAwait(false);
            if (ready)
            {
                try
                {
                    if (StillAuthorized() && profiles.Find(session.ConnectorId) is { } profile)
                        await oauth.DeleteObsoleteCredentialsAsync(user.Id, profile, ct).ConfigureAwait(false);
                }
                finally
                {
                    // Cleanup can wait on CredentialsHost while the initiating
                    // session is revoked or the user cancels. Compensate only
                    // this flow, even when the HTTP request was cancelled.
                    if (!StillAuthorized())
                        await coordinator.CancelFlowAsync(session, CancellationToken.None).ConfigureAwait(false);
                }
            }
            if (!StillAuthorized()) return Results.Unauthorized();
            completed = ready;
            return completed ? Results.Json(new ConnectorOAuthCompleteResponse("connected", "Signed in."), KautukaJsonContext.Default.ConnectorOAuthCompleteResponse)
                : Results.StatusCode(StatusCodes.Status202Accepted);
        }
        catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
        catch (HttpRequestException e) { return BoundMailBrokerFailure(context, e); }
        catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException or Grpc.Core.RpcException
            || e is OperationCanceledException && !ct.IsCancellationRequested)
        { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
        finally { sessions.End(body.State, session, completed); }
    }

    private static async Task<IResult> BeginBoundMail(string connectorId, HttpContext context, IGatewayAuthGuard auth,
        IGatewayCatalogStore catalog, ConnectorAuthCatalog profiles, ConnectorAuthEndpoint endpoint,
        BoundMailSessionStore sessions, BrokerMailCredentialStore credentials, BrokerHandoffClient broker,
        BrokerInstallationIdentityProvider installations, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!TryGetUser(auth, context, out GatewayUser? user) || user is null) return Results.Unauthorized();
        if (!sessions.AllowAttempt(user.Id, DateTimeOffset.UtcNow)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        if (!TryAuthorizeConnector(catalog, user, connectorId, out IResult? denied)) return denied!;
        ConnectorAuthProfile? profile = profiles.Find(connectorId);
        string? provider = profile?.Id switch { "kautuka-google-mail" => "google", "kautuka-m365-mail" => "microsoft", _ => null };
        if (provider is null || !endpoint.IsHosted || endpoint.Host is null
            || !ConnectorAuthProfile.TryNormalizeReturnOrigin(context.Request.Headers.Origin.ToString(), out string origin)) return Results.BadRequest();
        BoundMailSessionStore.StartIntent? intent = sessions.BeginStart(user.Id, profile!.Id);
        if (intent is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        using IDisposable nacScope = Kautuka.NetworkAccess.Client.NetworkAccessContext.BeginScope(
            userId: user.Id,
            connectorId: profile!.Id,
            purpose: "connector-oauth",
            service: "connector-auth");
        try
        {
            BrokerMailStoredState saved = await credentials.ReadAsync(user.Id, profile!.Id, ct).ConfigureAwait(false);
            if (saved.PendingActivation is not null || saved.PendingRefresh is not null || saved.DisconnectPending) return Results.Conflict();
            string connection = saved.Active?.ConnectionId ?? ConnectorAuthProfile.CreateState();
            long generation = saved.Active?.Generation ?? 0;
            string state = ConnectorAuthProfile.CreateState(), tab = ConnectorAuthProfile.CreateState(), nonce = ConnectorAuthProfile.CreateState();
            string verifier = ConnectorAuthProfile.CreateCodeVerifier(), mailKey = ConnectorDpop.CreatePrivateJwk();
            string cookie = ConnectorAuthProfile.CreateState();
            if (!BoundMailSessionStore.IsToken(cookie)) return Results.BadRequest();
            Uri host = new(endpoint.Host);
            string flow;
            BrokerInstallationIdentity installation;
            try
            {
                flow = await broker.StartAsync(host, new BrokerHandoffStart(provider, "mail", state,
                    ConnectorAuthProfile.CreateCodeChallenge(verifier), nonce, origin, connection, ConnectorDpop.Thumbprint(mailKey), generation), ct).ConfigureAwait(false);
                installation = await installations.GetOrCreateAsync(ct).ConfigureAwait(false);
            }
            catch (HttpRequestException e) { return BoundMailBrokerFailure(context, e); }
            catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException or Grpc.Core.RpcException
                || e is OperationCanceledException && !ct.IsCancellationRequested)
            { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            if (!TryGetUser(auth, context, out GatewayUser? current) || current?.Id != user.Id
                || !TryAuthorizeConnector(catalog, current, connectorId, out _))
            {
                try { await broker.CancelMailAsync(host, flow, mailKey, ct).ConfigureAwait(false); } catch (HttpRequestException) { /* Broker flow expires. */ }
                return Results.Unauthorized();
            }
            if (!sessions.SaveStarted(intent, state, new BoundMailSession
            {
                UserId = user.Id,
                ConnectorId = profile.Id,
                CatalogConnectorId = connectorId,
                Provider = provider,
                FlowId = flow,
                ConnectionId = connection,
                Verifier = verifier,
                DpopPrivateJwk = mailKey,
                Origin = origin,
                BrokerOrigin = endpoint.Host,
                ExpectedGeneration = generation,
                CookieHash = BoundMailSessionStore.Hash(cookie),
                TabHash = BoundMailSessionStore.Hash(tab),
                ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5)
            }, DateTimeOffset.UtcNow))
            {
                try { await broker.CancelMailAsync(host, flow, mailKey, ct).ConfigureAwait(false); } catch (HttpRequestException) { /* Broker flow expires. */ }
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
            context.Response.Cookies.Append(BoundMailCookie + "_" + state, cookie, new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                Secure = RefreshCookieSecurity.IsSecure(context.Request),
                SameSite = SameSiteMode.Lax,
                Path = "/api/connectors",
                MaxAge = TimeSpan.FromMinutes(5)
            });
            string url = endpoint.Host + "/handoff#id=" + Uri.EscapeDataString(installation.Id) + "&flow=" + Uri.EscapeDataString(flow);
            return Results.Json(new BoundExternalStartResponse(url, endpoint.Host, state, flow, tab), KautukaJsonContext.Default.BoundExternalStartResponse);
        }
        catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException or Grpc.Core.RpcException
            || e is OperationCanceledException && !ct.IsCancellationRequested)
        { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
        finally { sessions.EndStart(intent); }
    }

    private static async Task<IResult> BindBoundMail(BoundExternalOperationRequest? body, HttpContext context,
        IGatewayAuthGuard auth, IGatewayCatalogStore catalog, BoundMailSessionStore sessions, BrokerHandoffClient broker, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (body is null || !BoundMailSessionStore.IsToken(body.Nonce) || !TryGetUser(auth, context, out GatewayUser? user) || user is null
            || !ConnectorAuthProfile.TryNormalizeReturnOrigin(context.Request.Headers.Origin.ToString(), out string origin)
            || !sessions.TryGet(body.State, user.Id, origin, context.Request.Cookies[BoundMailCookie + "_" + body.State], body.TabHandle, DateTimeOffset.UtcNow, out BoundMailSession? session)
            || session is null || !TryAuthorizeConnector(catalog, user, session.CatalogConnectorId, out _)) return Results.Unauthorized();
        if (!sessions.AllowAttempt(user.Id, DateTimeOffset.UtcNow)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        using IDisposable nacScope = Kautuka.NetworkAccess.Client.NetworkAccessContext.BeginScope(
            userId: user.Id,
            connectorId: session.CatalogConnectorId,
            purpose: "connector-oauth",
            service: "connector-auth");
        try { await broker.BindBrowserAsync(new Uri(session.BrokerOrigin), session.FlowId, body.Nonce!, ct).ConfigureAwait(false); }
        catch (HttpRequestException e) { return BoundMailBrokerFailure(context, e); }
        catch (Exception e) when (e is InvalidDataException or System.Text.Json.JsonException or Grpc.Core.RpcException
            || e is OperationCanceledException && !ct.IsCancellationRequested)
        { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
        if (!TryGetUser(auth, context, out GatewayUser? current) || current?.Id != user.Id
            || !TryAuthorizeConnector(catalog, current, session.CatalogConnectorId, out _)) return Results.Unauthorized();
        return sessions.Confirm(body.State, current.Id, origin, context.Request.Cookies[BoundMailCookie + "_" + body.State], body.TabHandle, DateTimeOffset.UtcNow)
            ? Results.NoContent() : Results.Unauthorized();
    }

    private static IResult BoundMailBrokerFailure(HttpContext context, HttpRequestException exception)
    {
        if (exception is BrokerInstallationSuspendedException)
        {
            context.Response.Headers["Kautuka-Installation-Error"] = "installation_suspended";
            return Results.Text("{\"error\":\"installation_suspended\",\"message\":\"The broker operator suspended this installation. Contact the broker operator; provider grants may need to be revoked.\"}", "application/json", statusCode: StatusCodes.Status403Forbidden);
        }
        if (exception is BrokerMailRequestException { RetryAfter: TimeSpan delay })
            context.Response.Headers.RetryAfter = Math.Ceiling(delay.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (exception is BrokerMailRequestException { AdmissionDenial: not null } admission)
            context.Response.Headers["Kautuka-Admission-Denied"] = admission.AdmissionDenial;
        if (exception is BrokerMailRequestException { MailError: "insufficient_scope" })
        {
            context.Response.Headers["Kautuka-Mail-Error"] = "insufficient_scope";
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }
        int status = exception.StatusCode switch
        {
            System.Net.HttpStatusCode.TooManyRequests => StatusCodes.Status429TooManyRequests,
            System.Net.HttpStatusCode.Conflict => StatusCodes.Status409Conflict,
            System.Net.HttpStatusCode.Gone => StatusCodes.Status410Gone,
            _ => StatusCodes.Status503ServiceUnavailable,
        };
        return Results.StatusCode(status);
    }
}
