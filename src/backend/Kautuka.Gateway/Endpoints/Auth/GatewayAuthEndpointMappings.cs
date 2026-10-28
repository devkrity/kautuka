using System.Security.Claims;

using Kautuka.Gateway.Domain.Stores;
using Kautuka.Gateway.Endpoints.Common;

using Microsoft.AspNetCore.Mvc;

namespace Kautuka.Gateway.Endpoints.Auth;

internal static partial class GatewayAuthEndpointMappings
{
    private const string refreshCookieName = "kautuka_refresh_token";

    [LoggerMessage(EventId = 100, Level = LogLevel.Error,
        Message = "CreateFirstAdmin threw unexpectedly for user '{Username}'")]
    private static partial void LogCreateFirstAdminFailed(ILogger logger, Exception exception, string username);

    [LoggerMessage(EventId = 101, Level = LogLevel.Warning,
        Message = "Authentication attempt rejected from {RemoteAddress} with reason {Reason}")]
    private static partial void LogAuthenticationRejected(ILogger logger, string remoteAddress, string reason);

    [LoggerMessage(EventId = 102, Level = LogLevel.Debug,
        Message = "Refresh requested without an active browser session")]
    private static partial void LogRefreshSessionMissing(ILogger logger);

    public static WebApplication MapGatewayAuthEndpoints(this WebApplication app)
    {
        // ── Setup (first-run only) ─────────────────────────────────────────────
        RouteHandlerBuilder setupStatus = app.MapGet("/api/auth/setup/status", (IGatewayUserStore users) =>
            Results.Json(
                new AuthSetupStatusResponse(users.CanSetup),
                KautukaJsonContext.Default.AuthSetupStatusResponse));
        setupStatus.WithTags("Auth")
            .AllowAnonymous()
            .WithSummary("Returns whether initial admin setup is still required")
            .Produces<AuthSetupStatusResponse>(StatusCodes.Status200OK);

        RouteHandlerBuilder setup = app.MapPost("/api/auth/setup", (AuthSetupRequest? body, IGatewayUserStore users, SetupPossession setupToken, ILoggerFactory loggerFactory) =>
        {
            ILogger logger = loggerFactory.CreateLogger("Kautuka.Gateway.Auth.Setup");
            if (body is null)
            {
                return Results.Json(
                    new AuthErrorResponse("Username and a password of at least 8 characters are required."),
                    KautukaJsonContext.Default.AuthErrorResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (!users.CanSetup)
            {
                return Results.Json(
                    new AuthErrorResponse("Setup has already been completed."),
                    KautukaJsonContext.Default.AuthErrorResponse,
                    statusCode: StatusCodes.Status409Conflict);
            }

            if (body.Username is null || body.Password is null || string.IsNullOrWhiteSpace(body.Username) || body.Password.Length < 8)
            {
                return Results.Json(
                    new AuthErrorResponse("Username and a password of at least 8 characters are required."),
                    KautukaJsonContext.Default.AuthErrorResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (!setupToken.TryRedeem(body.SetupToken))
            {
                return Results.Json(
                    new AuthErrorResponse("Setup token was not accepted."),
                    KautukaJsonContext.Default.AuthErrorResponse,
                    statusCode: StatusCodes.Status403Forbidden);
            }

            try
            {
                GatewayUserListItem? created = users.CreateFirstAdmin(body.Username, body.Password, out string? error);
                if (created is null)
                {
                    return Results.Json(
                        new AuthErrorResponse(error ?? "Failed to create admin account."),
                        KautukaJsonContext.Default.AuthErrorResponse,
                        statusCode: StatusCodes.Status400BadRequest);
                }

                return Results.Json(new GatewayBooleanSuccessResponse(true), KautukaJsonContext.Default.GatewayBooleanSuccessResponse);
            }
            catch (InvalidOperationException ex)
            {
                LogCreateFirstAdminFailed(logger, ex, body.Username);
                return Results.Json(
                    new AuthErrorResponse("Setup failed. Review Gateway logs using the request correlation ID."),
                    KautukaJsonContext.Default.AuthErrorResponse,
                    statusCode: StatusCodes.Status500InternalServerError);
            }
            catch (IOException ex)
            {
                LogCreateFirstAdminFailed(logger, ex, body.Username);
                return Results.Json(
                    new AuthErrorResponse("Setup failed. Review Gateway logs using the request correlation ID."),
                    KautukaJsonContext.Default.AuthErrorResponse,
                    statusCode: StatusCodes.Status500InternalServerError);
            }
            catch (UnauthorizedAccessException ex)
            {
                LogCreateFirstAdminFailed(logger, ex, body.Username);
                return Results.Json(
                    new AuthErrorResponse("Setup failed. Review Gateway logs using the request correlation ID."),
                    KautukaJsonContext.Default.AuthErrorResponse,
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });
        setup.WithTags("Auth")
            .AllowAnonymous()
            .RequireRateLimiting("AuthSetup")
            .WithMetadata(new RequestSizeLimitAttribute(16 * 1024))
            .WithSummary("Create the initial admin account (only allowed when no users exist)")
            .Accepts<AuthSetupRequest>("application/json")
            .Produces<GatewayBooleanSuccessResponse>(StatusCodes.Status200OK)
            .Produces<AuthErrorResponse>(StatusCodes.Status400BadRequest)
            .Produces<AuthErrorResponse>(StatusCodes.Status409Conflict);

        // ── Login ─────────────────────────────────────────────────────────────
        RouteHandlerBuilder login = app.MapPost("/api/auth/login", (AuthLoginRequest? body, HttpContext context, IGatewayUserStore users, IGatewayTokenStore tokens, IGatewayCatalogStore catalog, ILoggerFactory loggerFactory) =>
        {
            if (body is null || body.Username is null || body.Password is null)
            {
                return Results.Json(
                    new AuthErrorResponse("Username and password are required."),
                    KautukaJsonContext.Default.AuthErrorResponse,
                    statusCode: StatusCodes.Status400BadRequest);
            }

            string username = body.Username.Trim();
            string password = body.Password;

            bool found = users.TryGetUserByUsername(username, out GatewayUser? user) && user is not null;
            bool passwordOk = GatewayPasswordHasher.VerifyOrEqualize(found ? user!.Password : null, password);
            if (!found || !passwordOk || !user!.IsActive)
            {
                LogAuthenticationRejected(
                    loggerFactory.CreateLogger("Kautuka.Gateway.Auth"),
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    "invalid_credentials");
                return Results.Json(new AuthErrorResponse("Invalid credentials."), KautukaJsonContext.Default.AuthErrorResponse, statusCode: StatusCodes.Status401Unauthorized);
            }

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
                    GatewayEndpointUtilities.ToUserPayload(user, users.ExternalProvidersFor(user.Id))),
                KautukaJsonContext.Default.AuthLoginResponse);
        });
        login.WithTags("Auth")
            .AllowAnonymous()
            .RequireRateLimiting("AuthLogin")
            .WithMetadata(new RequestSizeLimitAttribute(16 * 1024))
            .WithSummary("Authenticate user and issue session tokens")
            .Accepts<AuthLoginRequest>("application/json")
            .Produces<AuthLoginResponse>(StatusCodes.Status200OK)
            .Produces<AuthErrorResponse>(StatusCodes.Status401Unauthorized);

        RouteHandlerBuilder refresh = app.MapPost("/api/auth/refresh", (IGatewayTokenStore tokens, IGatewayUserStore users, HttpContext context, ILoggerFactory loggerFactory) =>
        {
            ILogger authLogger = loggerFactory.CreateLogger("Kautuka.Gateway.Auth");
            if (!context.Request.Cookies.TryGetValue(refreshCookieName, out string? refreshToken)
                || string.IsNullOrWhiteSpace(refreshToken))
            {
                ClearRefreshCookie(context);
                if (authLogger.IsEnabled(LogLevel.Debug))
                {
                    LogRefreshSessionMissing(authLogger);
                }
                return Results.Json(new AuthErrorResponse("Not authenticated."), KautukaJsonContext.Default.AuthErrorResponse, statusCode: StatusCodes.Status401Unauthorized);
            }

            if (!tokens.TryResolveRefreshToken(refreshToken, out string? userId)
                || userId is null
                || !users.TryGetUserById(userId, out GatewayUser? user)
                || user is null
                || !user.IsActive)
            {
                ClearRefreshCookie(context);
                LogAuthenticationRejected(
                    authLogger,
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    "invalid_refresh_session");
                return Results.Json(new AuthErrorResponse("Not authenticated."), KautukaJsonContext.Default.AuthErrorResponse, statusCode: StatusCodes.Status401Unauthorized);
            }

            if (!tokens.TryRotateRefreshToken(refreshToken, out _, out string? replacementToken))
            {
                ClearRefreshCookie(context);
                LogAuthenticationRejected(
                    authLogger,
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    "refresh_replay");
                return Results.Json(new AuthErrorResponse("Not authenticated."), KautukaJsonContext.Default.AuthErrorResponse, statusCode: StatusCodes.Status401Unauthorized);
            }

            context.Response.Cookies.Append(
                refreshCookieName,
                replacementToken!,
                CreateRefreshCookieOptions(context, DateTimeOffset.UtcNow.Add(GatewayTokenStore.RefreshTokenLifetime)));

            string accessToken = tokens.IssueAccessToken(user.Id);
            return Results.Json(
                new AuthRefreshResponse(
                    accessToken,
                    "bearer",
                    checked((int)GatewayTokenStore.AccessTokenLifetime.TotalSeconds)),
                KautukaJsonContext.Default.AuthRefreshResponse);
        });
        refresh.WithTags("Auth")
            .AllowAnonymous()
            .RequireRateLimiting("AuthRefresh")
            .WithSummary("Refresh access token from secure cookie")
            .Produces<AuthRefreshResponse>(StatusCodes.Status200OK)
            .Produces<AuthErrorResponse>(StatusCodes.Status401Unauthorized);

        RouteHandlerBuilder logout = app.MapPost("/api/auth/logout", (IGatewayTokenStore tokens, HttpContext context) =>
        {
            string userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? throw new InvalidOperationException("Authenticated principal has no user identifier.");
            tokens.RevokeUserSessions(userId);
            if (context.Request.Cookies.TryGetValue(refreshCookieName, out string? refreshToken)
                && !string.IsNullOrWhiteSpace(refreshToken))
            {
                tokens.RevokeRefreshToken(refreshToken);
            }

            ClearRefreshCookie(context);
            return Results.Json(new AuthLogoutResponse(true), KautukaJsonContext.Default.AuthLogoutResponse);
        });
        logout.WithTags("Auth")
            .WithSummary("Revoke refresh token and clear auth cookie")
            .Produces<AuthLogoutResponse>(StatusCodes.Status200OK);

        RouteHandlerBuilder me = app.MapGet("/api/auth/me", (IGatewayAuthGuard auth, IGatewayUserStore users, HttpContext context) =>
        {
            if (!auth.TryAuthorize(context, out GatewayUser? user) || user is null)
            {
                return Results.Json(new AuthErrorResponse("Not authenticated."), KautukaJsonContext.Default.AuthErrorResponse, statusCode: StatusCodes.Status401Unauthorized);
            }

            return Results.Json(
                GatewayEndpointUtilities.ToUserPayload(user, users.ExternalProvidersFor(user.Id)),
                KautukaJsonContext.Default.GatewayUserPayload);
        });
        me.WithTags("Auth")
            .WithSummary("Get current authenticated user profile")
            .Produces<GatewayUserPayload>(StatusCodes.Status200OK)
            .Produces<AuthErrorResponse>(StatusCodes.Status401Unauthorized);

        MapExternalLoginEndpoints(app);
        return app;
    }

    private static CookieOptions CreateRefreshCookieOptions(HttpContext context, DateTimeOffset? expires = null) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Secure = RefreshCookieSecurity.IsSecure(context.Request),
        Path = "/",
        Expires = expires,
    };

    private static void ClearRefreshCookie(HttpContext context)
        => context.Response.Cookies.Delete(refreshCookieName, CreateRefreshCookieOptions(context));
}
