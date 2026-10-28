namespace Kautuka.Gateway.Domain.Auth;

internal interface IGatewayAuthGuard
{
    bool TryAuthorize(HttpContext context, out GatewayUser? user);
    IResult? RequireAuthenticated(HttpContext context, out GatewayUser? user);
    IResult? RequireAdmin(HttpContext context, out GatewayUser? user);
}
