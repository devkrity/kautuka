using System.Text.Json;

namespace Kautuka.Gateway.Domain.Stores;

internal interface IGatewayUserStore
{
    bool TryGetUserById(string userId, out GatewayUser? user);
    bool TryGetUserByUsername(string username, out GatewayUser? user);
    IReadOnlyList<GatewayUserListItem> ListUsers();
    bool CanSetup { get; }
    GatewayUserListItem? CreateFirstAdmin(string username, string password, out string? error);
    GatewayUserListItem? InviteUser(JsonElement body, out string? error);

    string? IssuedInviteCode(string userId) => null;
    bool UpdateRole(string userId, GatewayUserRoleUpdateRequest request, out string? error);
    bool UpdateIsActive(string userId, bool isActive, out string? error);
    bool UpdateImportPermissions(string userId, GatewayUserImportPermissionsUpdateRequest request);
    bool UpdateProfile(string userId, string? email, string? displayName);
    bool UpdatePassword(string userId, string currentPassword, string newPassword, out string? error);
    bool TryCompleteExternalSignIn(ExternalIdentity identity, string? setupSecret, SetupPossession setupPossession, out GatewayUser? user, out string reason)
    {
        user = null;
        reason = "rejected";
        return false;
    }
    bool TryClearExternalLogin(string userId, string provider, string actorId, out string? error);
    string[] ExternalProvidersFor(string userId);
}

internal sealed record ChannelSettingsUpdateResult(
    GatewayUserListItem User,
    bool ProvisioningRequired,
    string? RegistrationId,
    string? ExternalUserId,
    string? DefaultTargetType,
    string? DefaultTargetId);
