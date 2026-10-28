using System.Security.Claims;
using System.Text.Encodings.Web;

using Kautuka.Gateway.Domain.Stores;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Kautuka.Gateway.Domain.Auth;

internal sealed class GatewayBearerAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IGatewayTokenStore tokenStore,
    IGatewayUserStore userStore)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string AuthenticationSchemeName = "GatewayBearer";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string authorization = Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        string token = authorization[7..].Trim();
        if (string.IsNullOrWhiteSpace(token)
            || !tokenStore.TryResolveAccessToken(token, out string? userId)
            || userId is null
            || !userStore.TryGetUserById(userId, out GatewayUser? user)
            || user is null
            || !user.IsActive)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid or expired access credential."));
        }

        Claim[] claims =
        [
            new(ClaimTypes.NameIdentifier, user.Id),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role),
        ];
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationSchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, AuthenticationSchemeName)));
    }
}
