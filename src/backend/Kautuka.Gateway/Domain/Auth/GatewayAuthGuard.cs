using Kautuka.Gateway.Domain.Stores;

namespace Kautuka.Gateway.Domain.Auth;

internal sealed class GatewayAuthGuard : IGatewayAuthGuard
{
    private readonly IGatewayTokenStore tokenStore;
    private readonly IGatewayUserStore userStore;

    public GatewayAuthGuard(IGatewayTokenStore tokenStore, IGatewayUserStore userStore)
    {
        this.tokenStore = tokenStore;
        this.userStore = userStore;
    }

    public bool TryAuthorize(HttpContext context, out GatewayUser? user)
    {
        user = null;
        string authHeader = context.Request.Headers.Authorization.ToString();
        if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string token = authHeader[7..].Trim();
        if (string.IsNullOrWhiteSpace(token)
            || !tokenStore.TryResolveAccessToken(token, out string? userId)
            || userId is null
            || !userStore.TryGetUserById(userId, out user))
        {
            return false;
        }

        return user is not null && user.IsActive;
    }

    public IResult? RequireAuthenticated(HttpContext context, out GatewayUser? user)
    {
        user = null;
        if (!TryAuthorize(context, out GatewayUser? resolved) || resolved is null)
        {
            return Results.Json(new GatewayErrorResponse("Not authenticated."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status401Unauthorized);
        }

        user = resolved;
        return null;
    }

    public IResult? RequireAdmin(HttpContext context, out GatewayUser? user)
    {
        IResult? unauthorized = RequireAuthenticated(context, out user);
        if (unauthorized is not null || user is null)
        {
            return unauthorized;
        }

        if (GatewayOwnershipAccess.IsAdmin(user))
        {
            return null;
        }

        return Results.Json(new GatewayErrorResponse("Forbidden."), KautukaJsonContext.Default.GatewayErrorResponse, statusCode: StatusCodes.Status403Forbidden);
    }
}
