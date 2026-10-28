namespace Kautuka.Gateway.Domain.Auth;

internal sealed record ExternalLoginLink(
    string Provider,
    string Subject,
    string UserId,
    string Email,
    string? Issuer = null,
    string? TenantId = null,
    string? ObjectId = null)
{
    public static string Key(string provider, string subject) => provider + "\n" + subject;
}

internal sealed record ExternalIdentity(
    string Provider,
    string Subject,
    string Email,
    string? DisplayName,
    string? Issuer = null,
    string? TenantId = null,
    string? ObjectId = null,
    string? InviteCode = null,
    string? LinkUserId = null,
    bool EmailVerified = false);
