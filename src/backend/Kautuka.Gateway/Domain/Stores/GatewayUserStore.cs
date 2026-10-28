using System.Security.Cryptography;
using System.Text.Json;

namespace Kautuka.Gateway.Domain.Stores;

internal sealed partial class GatewayUserStore : IGatewayUserStore
{
    private readonly GatewayState state;
    private readonly IGatewayTokenStore tokenStore;
    private readonly IGatewayUserCommandRepository commandRepository;
    private readonly object mutationLock = new();
    private readonly Dictionary<string, string> issuedInviteCodes = new(StringComparer.Ordinal);

    public GatewayUserStore(GatewayState state, IGatewayTokenStore tokenStore, IGatewayUserCommandRepository commandRepository)
    {
        this.state = state;
        this.tokenStore = tokenStore;
        this.commandRepository = commandRepository;
    }

    public string? IssuedInviteCode(string userId)
    {
        lock (mutationLock)
        {
            return issuedInviteCodes.TryGetValue(userId, out string? code) ? code : null;
        }
    }

    public bool TryGetUserById(string userId, out GatewayUser? user)
    {
        user = state.Users.Values.FirstOrDefault(item => string.Equals(item.Id, userId, StringComparison.Ordinal));
        return user is not null;
    }

    public bool TryGetUserByUsername(string username, out GatewayUser? user) =>
        state.Users.TryGetValue(username, out user);

    public IReadOnlyList<GatewayUserListItem> ListUsers() => [.. state.Users.Values.Select(ToUserPayload)];

    public bool CanSetup => !state.SetupCompleted;

    public GatewayUserListItem? CreateFirstAdmin(string username, string password, out string? error)
    {
        error = null;

        string trimmedUsername = username.Trim();
        if (string.IsNullOrWhiteSpace(trimmedUsername))
        {
            error = "Username is required.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
        {
            error = "Password must be at least 8 characters.";
            return null;
        }

        // All checks and writes happen inside the persist lock + a single DB transaction
        // so concurrent requests serialize at both the C# and SQLite layer.
        lock (mutationLock)
        {
            // Re-check the persisted flag inside the lock — authoritative source of truth.
            if (state.SetupCompleted)
            {
                error = "Setup has already been completed.";
                return null;
            }

            string userId = $"user-{Guid.NewGuid():N}";
            string passwordHash = GatewayPasswordHasher.Hash(password);
            var user = new GatewayUser(
                userId, trimmedUsername, passwordHash, "admin", true,
                null, null, true, true);

            if (!commandRepository.TryCreateFirstAdmin(user))
            {
                error = "Setup has already been completed.";
                return null;
            }

            state.Users[trimmedUsername] = user;
            state.SetupCompleted = true;

            GatewayStateLog.FirstAdminCreated(state.Logger, trimmedUsername);
            return ToUserPayload(user);
        }
    }

    public GatewayUserListItem? InviteUser(JsonElement body, out string? error)
    {
        lock (mutationLock)
        {
            return InviteUserCore(body, out error);
        }
    }

    private GatewayUserListItem? InviteUserCore(JsonElement body, out string? error)
    {
        error = null;
        string username = body.GetStringProperty("username", "").Trim();
        string rawEmail = body.GetStringProperty("email", "").Trim();
        string? email = CanonicalEmail.Normalize(rawEmail);
        if (rawEmail.Length > 0 && email is null)
        {
            error = "Enter one email address.";
            return null;
        }

        string role = body.GetStringProperty("role", "user").Trim();
        string password = body.GetStringProperty("password", "").Trim();
        bool localPassword = password.Length > 0;
        if (localPassword && password.Length < 8)
        {
            error = "Secret phrase must be at least 8 characters.";
            return null;
        }

        if (!localPassword && email is null)
        {
            error = "Enter the email they use with Google or Microsoft, or set a secret phrase.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            username = email is null ? $"user-{Guid.NewGuid():N}" : UsernameFromEmail(email, "user");
        }

        if (state.Users.Values.Any(item => string.Equals(item.Username, username, StringComparison.OrdinalIgnoreCase)))
        {
            error = "Username already exists.";
            return null;
        }

        bool canImportConnectors = string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase);
        bool canImportSkills = string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase);
        string hash = localPassword
            ? GatewayPasswordHasher.Hash(password)
            : GatewayPasswordHasher.Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var user = new GatewayUser(
            $"user-{Guid.NewGuid():N}",
            username,
            hash,
            role,
            true,
            email,
            null,
            canImportConnectors,
            canImportSkills,
            localPassword);

        commandRepository.Upsert(user, passwordless: !localPassword);
        if (!localPassword)
        {
            state.PasswordlessUserIds[user.Id] = 1;
        }

        state.Users[username] = user;
        if (email is not null)
        {
            string inviteCode = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            string inviteHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(inviteCode)));
            issuedInviteCodes[user.Id] = inviteCode;
            commandRepository.InsertInviteCode(inviteHash, user.Id, email, DateTimeOffset.UtcNow.AddDays(7));
        }
        return ToUserPayload(user);
    }

    public bool UpdateRole(string userId, GatewayUserRoleUpdateRequest request, out string? error)
    {
        lock (mutationLock)
        {
            return UpdateRoleCore(userId, request, out error);
        }
    }

    private bool UpdateRoleCore(string userId, GatewayUserRoleUpdateRequest request, out string? error)
    {
        error = null;
        GatewayUser? user = state.Users.Values.FirstOrDefault(item => string.Equals(item.Id, userId, StringComparison.Ordinal));
        if (user is null)
        {
            error = "User not found.";
            return false;
        }

        string role = request.IsAdmin ? "admin" : "user";
        if (!request.IsAdmin
            && string.Equals(user.Role, "admin", StringComparison.OrdinalIgnoreCase)
            && user.IsActive
            && !HasAnotherActiveAdmin(user.Id))
        {
            error = "At least one active admin must remain in the system.";
            return false;
        }

        GatewayUser updated = user with
        {
            Role = role,
        };
        commandRepository.Upsert(updated);
        state.Users[user.Username] = updated;
        return true;
    }

    public bool UpdateIsActive(string userId, bool isActive, out string? error)
    {
        lock (mutationLock)
        {
            return UpdateIsActiveCore(userId, isActive, out error);
        }
    }

    private bool UpdateIsActiveCore(string userId, bool isActive, out string? error)
    {
        error = null;
        GatewayUser? user = state.Users.Values.FirstOrDefault(item => string.Equals(item.Id, userId, StringComparison.Ordinal));
        if (user is null)
        {
            error = "User not found.";
            return false;
        }

        if (!isActive
            && user.IsActive
            && string.Equals(user.Role, "admin", StringComparison.OrdinalIgnoreCase)
            && !HasAnotherActiveAdmin(user.Id))
        {
            error = "At least one active admin must remain in the system.";
            return false;
        }

        GatewayUser updated = user with { IsActive = isActive };
        commandRepository.Upsert(updated);
        state.Users[user.Username] = updated;
        if (!isActive)
        {
            tokenStore.RevokeUserSessions(user.Id);
        }
        return true;
    }

    public bool UpdateImportPermissions(string userId, GatewayUserImportPermissionsUpdateRequest request)
    {
        lock (mutationLock)
        {
            return UpdateImportPermissionsCore(userId, request);
        }
    }

    private bool UpdateImportPermissionsCore(string userId, GatewayUserImportPermissionsUpdateRequest request)
    {
        GatewayUser? user = state.Users.Values.FirstOrDefault(item => string.Equals(item.Id, userId, StringComparison.Ordinal));
        if (user is null)
        {
            return false;
        }

        GatewayUser updated = user with
        {
            CanImportConnectors = request.CanImportConnectors,
            CanImportSkills = request.CanImportSkills,
        };
        commandRepository.Upsert(updated);
        state.Users[user.Username] = updated;
        return true;
    }

    public bool UpdateProfile(string userId, string? email, string? displayName)
    {
        lock (mutationLock)
        {
            return UpdateProfileCore(userId, email, displayName);
        }
    }

    private bool UpdateProfileCore(string userId, string? email, string? displayName)
    {
        GatewayUser? user = state.Users.Values.FirstOrDefault(item => string.Equals(item.Id, userId, StringComparison.Ordinal));
        if (user is null)
        {
            return false;
        }

        GatewayUser updated = user with
        {
            Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
        };
        commandRepository.Upsert(updated);
        state.Users[user.Username] = updated;
        return true;
    }

    public bool UpdatePassword(string userId, string currentPassword, string newPassword, out string? error)
    {
        lock (mutationLock)
        {
            return UpdatePasswordCore(userId, currentPassword, newPassword, out error);
        }
    }

    private bool UpdatePasswordCore(string userId, string currentPassword, string newPassword, out string? error)
    {
        error = null;
        GatewayUser? user = state.Users.Values.FirstOrDefault(item => string.Equals(item.Id, userId, StringComparison.Ordinal));
        if (user is null)
        {
            error = "User not found.";
            return false;
        }

        if (user.HasPassword && !GatewayPasswordHasher.Verify(user.Password, currentPassword))
        {
            error = "Current password is incorrect.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < 8)
        {
            error = "New password must be at least 8 characters.";
            return false;
        }

        GatewayUser updated = user with { Password = GatewayPasswordHasher.Hash(newPassword), HasPassword = true };
        commandRepository.Upsert(updated);
        if (!user.HasPassword)
        {
            commandRepository.SetPasswordless(user.Id, passwordless: false);
            state.PasswordlessUserIds.TryRemove(user.Id, out _);
        }

        state.Users[user.Username] = updated;
        tokenStore.RevokeUserSessions(user.Id);
        return true;
    }

    private bool HasAnotherActiveAdmin(string excludedUserId)
        => state.Users.Values.Any(user =>
            !string.Equals(user.Id, excludedUserId, StringComparison.Ordinal)
            && user.IsActive
            && string.Equals(user.Role, "admin", StringComparison.OrdinalIgnoreCase));

    private GatewayUserListItem ToUserPayload(GatewayUser user) => new(
        user.Id,
        user.Username,
        user.Email,
        user.DisplayName,
        user.Role,
        user.IsActive,
        user.CanImportConnectors,
        user.CanImportSkills,
        null,
        false,
        null,
        null,
        [],
        [],
        [],
        ExternalProvidersFor(user.Id));

}
