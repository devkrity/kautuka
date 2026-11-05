using Kautuka.Gateway.Domain.Connectors;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Endpoints.Common;
using Kautuka.Gateway.Infrastructure.Connectors;

using Microsoft.AspNetCore.Mvc;

namespace Kautuka.Gateway.Endpoints.Auth;

internal static partial class GatewayAuthEndpointMappings
{
    [LoggerMessage(EventId = 8211, Level = LogLevel.Warning, Message = "External login failed at {Stage}: {Reason}.")]
    private static partial void LogBoundLoginFailure(ILogger logger, string stage, string reason);
    private static void MapBoundExternalLoginEndpoints(WebApplication app)
    {
        app.MapPost("/api/auth/external/bound-start", StartBoundExternalLogin).AllowAnonymous()
            .RequireRateLimiting("AuthExternal").WithMetadata(new RequestSizeLimitAttribute(16384)).WithTags("Auth");
        app.MapPost("/api/auth/external/bind-browser", BindExternalBrowser).AllowAnonymous()
            .RequireRateLimiting("AuthExternal").WithMetadata(new RequestSizeLimitAttribute(16384)).WithTags("Auth");
        app.MapPost("/api/auth/external/bound-complete", CompleteBoundExternalLogin).AllowAnonymous()
            .RequireRateLimiting("AuthExternalPoll").WithMetadata(new RequestSizeLimitAttribute(16384)).WithTags("Auth");
    }

    private static async Task<IResult> StartBoundExternalLogin(BoundExternalStartRequest? body, HttpContext context,
        ExternalLoginProviders providers, ExternalLoginSessionStore sessions, IGatewayAuthGuard auth,
        BrokerHandoffClient broker, BrokerInstallationIdentityProvider installations, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        string provider = NormalizeProvider(body?.Provider ?? string.Empty);
        string intent = (body?.Intent ?? "login").Trim().ToUpperInvariant();
        if (body is null || intent is not ("LOGIN" or "LINK") || !providers.IsEnabled(provider) || providers.Host is null
            || !Uri.TryCreate(providers.Host, UriKind.Absolute, out Uri? host)
            || !ConnectorAuthProfile.TryNormalizeReturnOrigin(context.Request.Headers.Origin.ToString(), out string origin)
            || body.MicrosoftAccountType is not null && (provider != "microsoft" || body.MicrosoftAccountType is not ("organizations" or "consumers")))
            return Results.BadRequest();
        string? actorId = null;
        if (intent == "LINK")
        {
            if (!auth.TryAuthorize(context, out GatewayUser? actor) || actor is null) return Results.Unauthorized();
            actorId = actor.Id;
        }

        string verifier = ConnectorAuthProfile.CreateCodeVerifier();
        string state = ConnectorAuthProfile.CreateState();
        string nonce = ConnectorAuthProfile.CreateState();
        string tab = ExternalLoginSessionStore.CreateCookieBinding();
        string cookie = ExternalLoginSessionStore.CreateCookieBinding();
        string flow;
        BrokerInstallationIdentity installation;
        try
        {
            flow = await broker.StartAsync(host, new BrokerHandoffStart(provider, intent == "LINK" ? "link" : "login", state,
                ConnectorAuthProfile.CreateCodeChallenge(verifier), nonce, origin, MicrosoftAccountType: body.MicrosoftAccountType), ct).ConfigureAwait(false);
            installation = await installations.GetOrCreateAsync(ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) { return BrokerFailure(context, ex); }
        catch (Exception ex) when (IsRetryableBrokerFailure(ex, ct)) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
        context.Response.Cookies.Append(externalLoginCookieName + "_" + state, cookie, new CookieOptions
        {
            HttpOnly = true,
            Secure = RefreshCookieSecurity.IsSecure(context.Request),
            SameSite = SameSiteMode.Lax,
            Path = "/api/auth/external",
            MaxAge = externalLoginLifetime,
        });
        if (!sessions.TrySaveBound(state, new ExternalLoginSession
        {
            Provider = provider,
            CodeVerifier = verifier,
            Nonce = nonce,
            ReturnOrigin = origin,
            CookieBindingHash = ExternalLoginSessionStore.HashBinding(cookie),
            TabBindingHash = ExternalLoginSessionStore.HashBinding(tab),
            BrokerFlowId = flow,
            BrokerOrigin = host.GetLeftPart(UriPartial.Authority),
            Intent = intent,
            LinkUserId = actorId,
            ExpiresAt = DateTimeOffset.UtcNow.Add(externalLoginLifetime),
        })) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        string brokerOrigin = host.GetLeftPart(UriPartial.Authority);
        string url = brokerOrigin + "/handoff#id=" + Uri.EscapeDataString(installation.Id) + "&flow=" + Uri.EscapeDataString(flow);
        return Results.Json(new BoundExternalStartResponse(url, brokerOrigin, state, flow, tab), KautukaJsonContext.Default.BoundExternalStartResponse);
    }

    private static async Task<IResult> BindExternalBrowser(BoundExternalOperationRequest? body, HttpContext context,
        ExternalLoginSessionStore sessions, IGatewayAuthGuard auth, BrokerHandoffClient broker, CancellationToken ct)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (body is null || !IsBoundToken(body.Nonce)
            || !TryBoundSession(body, context, sessions, auth, out ExternalLoginSession? session) || session is null
            || session.BrokerFlowId is null || session.BrokerOrigin is null) return Results.Unauthorized();
        try { await broker.BindBrowserAsync(new Uri(session.BrokerOrigin), session.BrokerFlowId, body.Nonce!, ct).ConfigureAwait(false); }
        catch (HttpRequestException ex) { return BrokerFailure(context, ex); }
        catch (Exception ex) when (IsRetryableBrokerFailure(ex, ct)) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
        return sessions.TryConfirmBrowser(body.State, context.Request.Cookies[externalLoginCookieName + "_" + body.State], body.TabHandle, DateTimeOffset.UtcNow)
            ? Results.NoContent() : Results.Unauthorized();
    }

    private static async Task<IResult> CompleteBoundExternalLogin(BoundExternalOperationRequest? body, HttpContext context,
        ExternalLoginSessionStore sessions, IGatewayAuthGuard auth, ExternalLoginProviders providers, BrokerHandoffClient broker,
        IExternalIdentityClient identities, IGatewayUserStore users, IGatewayTokenStore tokens, SetupPossession setupToken, ILoggerFactory loggerFactory, CancellationToken ct)
    {
        ILogger logger = loggerFactory.CreateLogger("Kautuka.Gateway.ExternalLogin");
        context.Response.Headers.CacheControl = "no-store";
        if (body is null || !TryBoundSession(body, context, sessions, auth, out ExternalLoginSession? checkedSession) || checkedSession is null)
            return Results.Unauthorized();
        if (!sessions.TryBeginCompletion(body.State, context.Request.Cookies[externalLoginCookieName + "_" + body.State], body.TabHandle,
            DateTimeOffset.UtcNow, out ExternalLoginSession? session) || session is null)
            return checkedSession.BrowserConfirmed ? Results.StatusCode(StatusCodes.Status202Accepted) : Results.Unauthorized();
        try
        {
            string fingerprint = ExternalLoginSessionStore.HashBinding($"{body.InviteCode?.Length ?? -1}:{body.InviteCode}{body.SetupToken?.Length ?? -1}:{body.SetupToken}");
            if (session.CompletionFingerprint is not null && !string.Equals(session.CompletionFingerprint, fingerprint, StringComparison.Ordinal)) return Results.Unauthorized();
            session.CompletionFingerprint = fingerprint;
            if (session.CompletedResponse is not null && session.CompletedRefreshToken is not null)
                return BoundLoginResult(context, session, tokens, users, isReplay: true);
            if (session.BrokerOrigin is null || session.BrokerFlowId is null) return Results.Unauthorized();
            string? token;
            try { token = await broker.ExchangeSocialAsync(new Uri(session.BrokerOrigin), session.BrokerFlowId, session.CodeVerifier, ct).ConfigureAwait(false); }
            catch (HttpRequestException ex) { LogBoundLoginFailure(logger, "broker_exchange", ex.StatusCode?.ToString() ?? "unavailable"); return BrokerFailure(context, ex); }
            catch (Exception ex) when (IsRetryableBrokerFailure(ex, ct)) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
            if (token is null) return Results.StatusCode(StatusCodes.Status202Accepted);
            if (!ExternalIdentityToken.TryReadKeyId(token, out string? keyId, out _) || keyId is null) { LogBoundLoginFailure(logger, "token_header", "invalid"); return Results.Unauthorized(); }
            System.Security.Cryptography.RSA? key = await identities.FindKeyAsync(session.Provider, keyId, ct).ConfigureAwait(false);
            string? audience = providers.ClientId(session.Provider);
            if (key is null || audience is null) { LogBoundLoginFailure(logger, "verification", "key_or_audience_unavailable"); return Results.Unauthorized(); }
            if (!ExternalIdentityToken.TryValidate(token, key, session.Provider, audience,
                session.Nonce, DateTimeOffset.UtcNow, out ExternalIdentity? identity, out string validationReason) || identity is null)
            { LogBoundLoginFailure(logger, "token_validation", validationReason); return Results.Unauthorized(); }
            if (!TryBoundSession(body, context, sessions, auth, out ExternalLoginSession? current) || !ReferenceEquals(current, session)) return Results.Unauthorized();
            identity = identity with { InviteCode = body.InviteCode, LinkUserId = session.Intent == "LINK" ? session.LinkUserId : null };
            if (!users.TryCompleteExternalSignIn(identity, body.SetupToken, setupToken, out GatewayUser? user, out string accountReason) || user is null)
            { LogBoundLoginFailure(logger, "account_resolution", accountReason); return Results.Unauthorized(); }
            string access = tokens.IssueAccessToken(user.Id);
            string refresh = tokens.IssueRefreshToken(user.Id);
            session.CompletedResponse = new AuthLoginResponse(access, "bearer", checked((int)GatewayTokenStore.AccessTokenLifetime.TotalSeconds),
                GatewayEndpointUtilities.ToUserPayload(user, users.ExternalProvidersFor(user.Id)));
            session.CompletedRefreshToken = refresh;
            session.CompletedAccessExpiresAt = DateTimeOffset.UtcNow.Add(GatewayTokenStore.AccessTokenLifetime);
            session.CompletedRefreshExpiresAt = DateTimeOffset.UtcNow.Add(GatewayTokenStore.RefreshTokenLifetime);
            return BoundLoginResult(context, session, tokens, users, isReplay: false);
        }
        finally { sessions.EndCompletion(session); }
    }

    private static bool TryBoundSession(BoundExternalOperationRequest body, HttpContext context, ExternalLoginSessionStore sessions,
        IGatewayAuthGuard auth, out ExternalLoginSession? session)
    {
        session = null;
        if (!IsBoundToken(body.State) || !IsBoundToken(body.TabHandle)
            || !sessions.TryGetBound(body.State, context.Request.Cookies[externalLoginCookieName + "_" + body.State], body.TabHandle, DateTimeOffset.UtcNow, out session)
            || session is null || !string.Equals(context.Request.Headers.Origin.ToString(), session.ReturnOrigin, StringComparison.Ordinal)) return false;
        if (session.Intent != "LINK") return true;
        return body.InviteCode is null && body.SetupToken is null && auth.TryAuthorize(context, out GatewayUser? actor)
            && actor is not null && string.Equals(actor.Id, session.LinkUserId, StringComparison.Ordinal);
    }

    private static bool IsBoundToken(string? value)
        => value is { Length: 43 } && value.All(static c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-');

    private static bool IsRetryableBrokerFailure(Exception exception, CancellationToken ct)
        => exception is InvalidDataException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException or Grpc.Core.RpcException
            || exception is OperationCanceledException && !ct.IsCancellationRequested;

    private static IResult BrokerFailure(HttpContext context, HttpRequestException exception)
    {
        if (exception.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            context.Response.Headers.RetryAfter = "60";
            return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        }
        if (exception.StatusCode is System.Net.HttpStatusCode.Gone or System.Net.HttpStatusCode.Conflict)
            return Results.StatusCode(StatusCodes.Status410Gone);
        if (exception.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Forbidden)
            return Results.Unauthorized();
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    private static IResult BoundLoginResult(HttpContext context, ExternalLoginSession session, IGatewayTokenStore tokens, IGatewayUserStore users, bool isReplay)
    {
        int remaining = (int)Math.Max(0, Math.Floor((session.CompletedAccessExpiresAt - DateTimeOffset.UtcNow).TotalSeconds));
        if (remaining == 0 || session.CompletedResponse is null || session.CompletedRefreshToken is null) return Results.Unauthorized();
        string expectedUser = session.CompletedResponse.User.Id;
        if (!users.TryGetUserById(expectedUser, out GatewayUser? user) || user is null || !user.IsActive) return Results.Unauthorized();
        if (!tokens.TryResolveAccessToken(session.CompletedResponse.AccessToken, out string? tokenUser)
            || !string.Equals(tokenUser, expectedUser, StringComparison.Ordinal)) return Results.Unauthorized();
        if (isReplay && context.Request.Cookies.TryGetValue(refreshCookieName, out string? currentRefresh)
            && tokens.TryResolveRefreshToken(currentRefresh, out string? currentUser))
        {
            return string.Equals(currentUser, expectedUser, StringComparison.Ordinal)
                ? Results.Json(session.CompletedResponse with { ExpiresInSeconds = remaining }, KautukaJsonContext.Default.AuthLoginResponse)
                : Results.Unauthorized();
        }
        if (!tokens.TryResolveRefreshToken(session.CompletedRefreshToken, out string? refreshUser)
            || !string.Equals(refreshUser, expectedUser, StringComparison.Ordinal)) return Results.Unauthorized();
        context.Response.Cookies.Append(refreshCookieName, session.CompletedRefreshToken,
            CreateRefreshCookieOptions(context, session.CompletedRefreshExpiresAt));
        return Results.Json(session.CompletedResponse with { ExpiresInSeconds = remaining }, KautukaJsonContext.Default.AuthLoginResponse);
    }
}
