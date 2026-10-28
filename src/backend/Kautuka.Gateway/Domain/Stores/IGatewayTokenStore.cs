namespace Kautuka.Gateway.Domain.Stores;

internal interface IGatewayTokenStore
{
    string IssueAccessToken(string userId);
    string IssueRefreshToken(string userId);
    bool TryResolveAccessToken(string token, out string? userId);
    bool TryResolveRefreshToken(string token, out string? userId);
    void RevokeRefreshToken(string token);
    bool TryRotateRefreshToken(string token, out string? userId, out string? replacementToken);
    void RevokeUserSessions(string userId);
}
