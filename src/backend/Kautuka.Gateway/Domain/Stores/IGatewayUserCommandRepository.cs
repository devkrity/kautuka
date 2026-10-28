namespace Kautuka.Gateway.Domain.Stores;

internal interface IGatewayUserCommandRepository
{
    bool TryCreateFirstAdmin(GatewayUser user);

    bool TryCreateSocialAdmin(GatewayUser user, ExternalLoginLink link);

    bool TryInsertExternalLogin(ExternalLoginLink link);

    bool TryConsumeInviteAndInsertExternalLogin(string codeHash, string canonicalEmail, ExternalLoginLink link, DateTimeOffset now, out string? userId)
    {
        userId = null;
        return false;
    }

    bool TryGetInviteTarget(string codeHash, string canonicalEmail, DateTimeOffset now, out string? userId)
    {
        userId = null;
        return false;
    }

    void DeleteExternalLogin(string provider, string subject);

    void SetPasswordless(string userId, bool passwordless);

    void Upsert(GatewayUser user, bool passwordless = false);

    void InsertInviteCode(string codeHash, string userId, string canonicalEmail, DateTimeOffset expiresAt)
    {
    }

    void ConsumeInviteCode(string codeHash)
    {
    }

}
