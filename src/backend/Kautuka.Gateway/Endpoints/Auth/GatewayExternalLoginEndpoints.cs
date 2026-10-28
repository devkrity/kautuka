using Kautuka.Gateway.Domain.Connectors;
using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Endpoints.Common;

using Microsoft.AspNetCore.Mvc;

namespace Kautuka.Gateway.Endpoints.Auth;

internal static partial class GatewayAuthEndpointMappings
{
    private const string externalLoginCookieName = "kautuka_external_login";
    private static readonly TimeSpan externalLoginLifetime = TimeSpan.FromMinutes(5);
    private const string rejectedDetail = "Sign-in was not accepted.";
    private const string notConfiguredDetail = "Sign-in is not configured.";

    [LoggerMessage(EventId = 110, Level = LogLevel.Warning, Message = "External sign-in rejected from {RemoteAddress} with reason {Reason}")]
    private static partial void LogExternalSignInRejected(ILogger logger, string remoteAddress, string reason);

    private static void MapExternalLoginEndpoints(WebApplication app)
    {
        app.MapGet("/api/auth/external/providers", (ExternalLoginProviders providers) =>
            Results.Json(
                new ExternalLoginProvidersResponse(providers.Google, providers.Microsoft),
                KautukaJsonContext.Default.ExternalLoginProvidersResponse))
            .AllowAnonymous()
            .WithTags("Auth");

        app.MapPost("/api/auth/external/start", StartExternalLogin)
            .AllowAnonymous()
            .RequireRateLimiting("AuthExternal")
            .WithMetadata(new RequestSizeLimitAttribute(16 * 1024))
            .WithTags("Auth");

        app.MapPost("/api/auth/external/complete", CompleteExternalLogin)
            .AllowAnonymous()
            .RequireRateLimiting("AuthExternal")
            .WithMetadata(new RequestSizeLimitAttribute(16 * 1024))
            .WithTags("Auth");
    }

    private static IResult StartExternalLogin(
        ExternalLoginStartRequest? body,
        HttpContext context,
        ExternalLoginProviders providers,
        ExternalLoginSessionStore sessions,
        IGatewayAuthGuard auth)
    {
        if (body is null)
        {
            return Results.Json(new AuthErrorResponse(rejectedDetail), KautukaJsonContext.Default.AuthErrorResponse, statusCode: StatusCodes.Status400BadRequest);
        }

        if (string.IsNullOrWhiteSpace(body.Provider))
        {
            return Results.Json(new AuthErrorResponse(rejectedDetail), KautukaJsonContext.Default.AuthErrorResponse, statusCode: StatusCodes.Status400BadRequest);
        }

        string provider = NormalizeProvider(body.Provider);
        string intent = (string.IsNullOrWhiteSpace(body.Intent) ? "login" : body.Intent).Trim().ToUpperInvariant();
        if (intent is not ("LOGIN" or "LINK"))
        {
            return Results.Json(new AuthErrorResponse(rejectedDetail), KautukaJsonContext.Default.AuthErrorResponse, statusCode: StatusCodes.Status400BadRequest);
        }

        string? linkUserId = null;
        if (intent == "LINK")
        {
            if (!auth.TryAuthorize(context, out GatewayUser? actor) || actor is null)
            {
                return Results.Json(new AuthErrorResponse(rejectedDetail), KautukaJsonContext.Default.AuthErrorResponse, statusCode: StatusCodes.Status401Unauthorized);
            }

            linkUserId = actor.Id;
        }
        if (body.MicrosoftAccountType is not null
            && (provider != "microsoft" || body.MicrosoftAccountType is not ("organizations" or "consumers")))
        {
            return Results.Json(new AuthErrorResponse(rejectedDetail), KautukaJsonContext.Default.AuthErrorResponse, statusCode: StatusCodes.Status400BadRequest);
        }

        if (!providers.IsEnabled(provider) || providers.Host is null || ExternalLoginProviders.ConnectorId(provider) is not string connectorId)
        {
            return Results.Json(new AuthErrorResponse(notConfiguredDetail), KautukaJsonContext.Default.AuthErrorResponse, statusCode: StatusCodes.Status404NotFound);
        }

        if (!ConnectorAuthProfile.TryNormalizeReturnOrigin(context.Request.Headers.Origin.ToString(), out string origin))
        {
            return Results.Json(new AuthErrorResponse(rejectedDetail), KautukaJsonContext.Default.AuthErrorResponse, statusCode: StatusCodes.Status400BadRequest);
        }

        string verifier = ConnectorAuthProfile.CreateCodeVerifier();
        string state = ConnectorAuthProfile.CreateState();
        string nonce = ConnectorAuthProfile.CreateState();
        string? cookieBinding = context.Request.Cookies[externalLoginCookieName];
        if (string.IsNullOrWhiteSpace(cookieBinding) || cookieBinding.Length > 128)
        {
            cookieBinding = ExternalLoginSessionStore.CreateCookieBinding();
        }

        context.Response.Cookies.Append(externalLoginCookieName, cookieBinding, new CookieOptions
        {
            HttpOnly = true,
            Secure = RefreshCookieSecurity.IsSecure(context.Request),
            SameSite = SameSiteMode.Lax,
            Path = "/api/auth/external",
            MaxAge = externalLoginLifetime,
        });

        sessions.Save(state, new ExternalLoginSession
        {
            Provider = provider,
            CodeVerifier = verifier,
            Nonce = nonce,
            ReturnOrigin = origin,
            CookieBindingHash = ExternalLoginSessionStore.HashBinding(cookieBinding),
            Intent = intent,
            LinkUserId = linkUserId,
            ExpiresAt = DateTimeOffset.UtcNow.Add(externalLoginLifetime),
        });
        string challenge = ConnectorAuthProfile.CreateCodeChallenge(verifier);
        string hash = "return=" + Uri.EscapeDataString(origin + "/login/callback")
            + "&connector=" + Uri.EscapeDataString(connectorId)
            + "&state=" + Uri.EscapeDataString(state)
            + "&challenge=" + Uri.EscapeDataString(challenge)
            + "&nonce=" + Uri.EscapeDataString(nonce);
        if (body.MicrosoftAccountType is not null)
        {
            hash += "&microsoftAccountType=" + Uri.EscapeDataString(body.MicrosoftAccountType);
        }
        return Results.Json(
            new ExternalLoginStartResponse(providers.Host + "/start#" + hash),
            KautukaJsonContext.Default.ExternalLoginStartResponse);
    }

    private static async Task<IResult> CompleteExternalLogin(
        ExternalLoginCompleteRequest? body,
        HttpContext context,
        ExternalLoginProviders providers,
        ExternalLoginSessionStore sessions,
        IExternalIdentityClient identities,
        IGatewayUserStore users,
        IGatewayTokenStore tokens,
        IGatewayAuthGuard auth,
        SetupPossession setupToken,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        ILogger logger = loggerFactory.CreateLogger("Kautuka.Gateway.Auth");
        string remote = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (body is null)
        {
            LogExternalSignInRejected(logger, remote, "missing_body");
            return Results.Json(new AuthErrorResponse(rejectedDetail), KautukaJsonContext.Default.AuthErrorResponse, statusCode: StatusCodes.Status400BadRequest);
        }

        if (!sessions.TryRemove(body.State ?? string.Empty, context.Request.Cookies[externalLoginCookieName], DateTimeOffset.UtcNow, out ExternalLoginSession? session) || session is null)
        {
            LogExternalSignInRejected(logger, remote, "expired_state");
            return Results.Json(new AuthErrorResponse(rejectedDetail), KautukaJsonContext.Default.AuthErrorResponse, statusCode: StatusCodes.Status401Unauthorized);
        }

        if (session.ExpiresAt < DateTimeOffset.UtcNow || !providers.IsEnabled(session.Provider))
        {
            LogExternalSignInRejected(logger, remote, session.ExpiresAt < DateTimeOffset.UtcNow ? "expired_state" : "not_configured");
            return Results.Json(new AuthErrorResponse(rejectedDetail), KautukaJsonContext.Default.AuthErrorResponse, statusCode: StatusCodes.Status401Unauthorized);
        }

        if (!string.Equals(context.Request.Headers.Origin.ToString(), session.ReturnOrigin, StringComparison.Ordinal))
        {
            LogExternalSignInRejected(logger, remote, "origin_mismatch");
            return Results.Json(new AuthErrorResponse(rejectedDetail), KautukaJsonContext.Default.AuthErrorResponse, statusCode: StatusCodes.Status401Unauthorized);
        }

        if (session.Intent == "LINK")
        {
            if (!auth.TryAuthorize(context, out GatewayUser? actor)
                || actor is null
                || !string.Equals(actor.Id, session.LinkUserId, StringComparison.Ordinal)
                || body.InviteCode is not null
                || body.SetupToken is not null)
            {
                LogExternalSignInRejected(logger, remote, "link_actor_mismatch");
                return Results.Json(new AuthErrorResponse(rejectedDetail), KautukaJsonContext.Default.AuthErrorResponse, statusCode: StatusCodes.Status401Unauthorized);
            }
        }

        string? token = await identities.ExchangeCodeAsync(
            session.Provider,
            body.Code ?? string.Empty,
            session.CodeVerifier,
            body.BrokerTicket ?? string.Empty,
            session.ReturnOrigin,
            cancellationToken).ConfigureAwait(false);
        string keyReason = "invalid_token";
        string? keyId = null;
        if (token is not null)
        {
            ExternalIdentityToken.TryReadKeyId(token, out keyId, out keyReason);
        }

        if (token is null || keyId is null)
        {
            LogExternalSignInRejected(logger, remote, keyReason);
            return Results.Json(new AuthErrorResponse(rejectedDetail), KautukaJsonContext.Default.AuthErrorResponse, statusCode: StatusCodes.Status401Unauthorized);
        }

        System.Security.Cryptography.RSA? key = await identities.FindKeyAsync(session.Provider, keyId, cancellationToken).ConfigureAwait(false);
        string? audience = providers.ClientId(session.Provider);
        string reason = "invalid_token";
        ExternalIdentity? identity = null;
        bool accepted = key is not null
            && audience is not null
            && ExternalIdentityToken.TryValidate(token, key, session.Provider, audience, session.Nonce, DateTimeOffset.UtcNow, out identity, out reason)
            && identity is not null;
        if (!accepted || identity is null)
        {
            LogExternalSignInRejected(logger, remote, key is null ? "unknown_kid" : reason);
            return Results.Json(new AuthErrorResponse(rejectedDetail), KautukaJsonContext.Default.AuthErrorResponse, statusCode: StatusCodes.Status401Unauthorized);
        }

        identity = identity with
        {
            InviteCode = body.InviteCode,
            LinkUserId = session.Intent == "LINK" ? session.LinkUserId : null,
        };
        if (!users.TryCompleteExternalSignIn(identity, body.SetupToken, setupToken, out GatewayUser? user, out string accountReason) || user is null)
        {
            LogExternalSignInRejected(logger, remote, accountReason);
            return Results.Json(new AuthErrorResponse(rejectedDetail), KautukaJsonContext.Default.AuthErrorResponse, statusCode: StatusCodes.Status401Unauthorized);
        }

        return IssueLogin(context, tokens, user, users.ExternalProvidersFor(user.Id));
    }

    private static string NormalizeProvider(string value)
    {
        string provider = value.Trim();
        if (provider.Equals("google", StringComparison.OrdinalIgnoreCase))
        {
            return "google";
        }

        return provider.Equals("microsoft", StringComparison.OrdinalIgnoreCase) ? "microsoft" : string.Empty;
    }

    private static IResult IssueLogin(HttpContext context, IGatewayTokenStore tokens, GatewayUser user, string[] externalProviders)
    {
        string accessToken = tokens.IssueAccessToken(user.Id);
        string refreshToken = tokens.IssueRefreshToken(user.Id);
        context.Response.Cookies.Append(
            refreshCookieName,
            refreshToken,
            CreateRefreshCookieOptions(context, DateTimeOffset.UtcNow.Add(GatewayTokenStore.RefreshTokenLifetime)));
        return Results.Json(
            new AuthLoginResponse(
                accessToken,
                "bearer",
                checked((int)GatewayTokenStore.AccessTokenLifetime.TotalSeconds),
                GatewayEndpointUtilities.ToUserPayload(user, externalProviders)),
            KautukaJsonContext.Default.AuthLoginResponse);
    }
}
