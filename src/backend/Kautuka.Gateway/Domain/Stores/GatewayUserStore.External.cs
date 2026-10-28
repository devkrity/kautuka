using System.Security.Cryptography;

namespace Kautuka.Gateway.Domain.Stores;

internal sealed partial class GatewayUserStore
{
    public bool TryCompleteExternalSignIn(ExternalIdentity identity, string? setupSecret, SetupPossession setupPossession, out GatewayUser? user, out string reason)
    {
        ArgumentNullException.ThrowIfNull(identity);
        user = null;
        lock (mutationLock)
        {
            if (identity.LinkUserId is not null)
            {
                GatewayUser? actor = state.Users.Values.FirstOrDefault(item => item.IsActive && item.Id == identity.LinkUserId);
                if (actor is null) { reason = "link_actor_mismatch"; return false; }
                bool alreadyLinked = TryLinkedUser(identity.Provider, identity.Subject, out GatewayUser? linked, out reason)
                    || TryLinkedTenant(identity, out linked, out reason);
                if (alreadyLinked)
                {
                    if (linked?.Id == actor.Id) { user = actor; return true; }
                    user = null; reason = "subject_taken"; return false;
                }

                return TryInsertLinkForUser(identity, actor, null, null, out user, out reason);
            }

            if (identity.InviteCode is null
                && (TryLinkedUser(identity.Provider, identity.Subject, out user, out reason)
                    || TryLinkedTenant(identity, out user, out reason)))
            {
                return user is not null;
            }

            if (!state.SetupCompleted && setupPossession.IsValid(setupSecret))
            {
                GatewayUser created = NewSocialAdmin(identity);
                ExternalLoginLink link = LinkFor(identity, created.Id);
                if (commandRepository.TryCreateSocialAdmin(created, link))
                {
                    setupPossession.TryRedeem(setupSecret);
                    state.Users[created.Username] = created;
                    state.ExternalLogins[ExternalLoginLink.Key(identity.Provider, identity.Subject)] = link;
                    state.PasswordlessUserIds[created.Id] = 1;
                    state.SetupCompleted = true;
                    user = created;
                    reason = "created";
                    return true;
                }

                if (TryLinkedUser(identity.Provider, identity.Subject, out user, out reason))
                {
                    return user is not null;
                }
            }

            if (TryLinkConfirmedUser(identity, out user, out reason))
            {
                return user is not null;
            }

            reason = "unlinked";
            return false;
        }
    }

    public bool TryClearExternalLogin(string userId, string provider, string actorId, out string? error)
    {
        error = null;
        if (provider is not ("google" or "microsoft"))
        {
            error = "Sign-in link was not found.";
            return false;
        }

        lock (mutationLock)
        {
            ExternalLoginLink? link = state.ExternalLogins.Values.FirstOrDefault(item =>
                string.Equals(item.UserId, userId, StringComparison.Ordinal)
                && string.Equals(item.Provider, provider, StringComparison.Ordinal));
            if (link is null)
            {
                error = "Sign-in link was not found.";
                return false;
            }

            bool otherProvider = state.ExternalLogins.Values.Any(item =>
                string.Equals(item.UserId, userId, StringComparison.Ordinal)
                && !string.Equals(item.Provider, provider, StringComparison.Ordinal));
            bool password = !state.PasswordlessUserIds.ContainsKey(userId);
            if (!otherProvider && !password)
            {
                error = "Set a password or keep another sign-in provider before removing this one.";
                return false;
            }

            commandRepository.DeleteExternalLogin(provider, link.Subject);
            state.ExternalLogins.TryRemove(ExternalLoginLink.Key(provider, link.Subject), out _);
            GatewayStateLog.ExternalLoginCleared(state.Logger, actorId, userId, provider);
            return true;
        }
    }

    public string[] ExternalProvidersFor(string userId)
        => [.. state.ExternalLogins.Values
            .Where(item => string.Equals(item.UserId, userId, StringComparison.Ordinal))
            .Select(item => item.Provider)
            .OrderBy(item => item, StringComparer.Ordinal)];

    private bool TryLinkedUser(string provider, string subject, out GatewayUser? user, out string reason)
    {
        user = null;
        reason = "rejected";
        if (!state.ExternalLogins.TryGetValue(ExternalLoginLink.Key(provider, subject), out ExternalLoginLink? link))
        {
            return false;
        }

        user = state.Users.Values.FirstOrDefault(item => string.Equals(item.Id, link.UserId, StringComparison.Ordinal));
        if (user is null || !user.IsActive)
        {
            user = null;
            reason = "inactive";
            return true;
        }

        reason = "linked";
        return true;
    }

    private bool TryLinkedTenant(ExternalIdentity identity, out GatewayUser? user, out string reason)
    {
        user = null;
        reason = "rejected";
        if (string.IsNullOrWhiteSpace(identity.TenantId) || string.IsNullOrWhiteSpace(identity.ObjectId))
        {
            return false;
        }

        ExternalLoginLink? link = state.ExternalLogins.Values.FirstOrDefault(item =>
            string.Equals(item.Provider, identity.Provider, StringComparison.Ordinal)
            && string.Equals(item.TenantId, identity.TenantId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.ObjectId, identity.ObjectId, StringComparison.OrdinalIgnoreCase));
        if (link is null)
        {
            return false;
        }

        return TryLinkedUser(link.Provider, link.Subject, out user, out reason);
    }

    private bool TryLinkConfirmedUser(ExternalIdentity identity, out GatewayUser? user, out string reason)
    {
        user = null;
        string? userId = identity.LinkUserId;
        bool hasInvite = string.IsNullOrWhiteSpace(userId) && !string.IsNullOrWhiteSpace(identity.InviteCode);

        if (string.IsNullOrWhiteSpace(userId) && !hasInvite)
        {
            reason = "unlinked";
            return false;
        }

        user = userId is null ? null : state.Users.Values.FirstOrDefault(item =>
            item.IsActive && string.Equals(item.Id, userId, StringComparison.Ordinal));
        string? verifiedEmail = CanonicalEmail.Normalize(identity.Email);
        bool verifiedForInvite = identity.Provider switch
        {
            "google" => identity.EmailVerified,
            // The signed xms_edov claim verifies the email domain for Microsoft identities.
            "microsoft" => identity.EmailVerified,
            _ => false,
        };
        if (hasInvite && (!verifiedForInvite || verifiedEmail is null))
        {
            reason = "invite_rejected";
            return false;
        }
        if (hasInvite)
        {
            string codeHash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity.InviteCode!.Trim())));
            if (!commandRepository.TryGetInviteTarget(codeHash, verifiedEmail!, DateTimeOffset.UtcNow, out userId)
                || userId is null)
            {
                reason = "invite_rejected";
                return false;
            }
            user = state.Users.Values.FirstOrDefault(item => item.IsActive && string.Equals(item.Id, userId, StringComparison.Ordinal));
        }
        if (user is null)
        {
            reason = "unlinked";
            return false;
        }

        return TryInsertLinkForUser(identity, user, hasInvite ? identity.InviteCode : null, verifiedEmail, out user, out reason);
    }

    private bool TryInsertLinkForUser(ExternalIdentity identity, GatewayUser target, string? inviteCode, string? verifiedEmail, out GatewayUser? user, out string reason)
    {
        user = target;
        GatewayUser matched = target;
        bool taken = state.ExternalLogins.Values.Any(item =>
            string.Equals(item.UserId, matched.Id, StringComparison.Ordinal)
            && string.Equals(item.Provider, identity.Provider, StringComparison.Ordinal)
            && !string.Equals(item.Subject, identity.Subject, StringComparison.Ordinal));
        if (taken)
        {
            user = null;
            reason = "subject_taken";
            return false;
        }

        ExternalLoginLink link = LinkFor(identity, target.Id);
        bool inserted = inviteCode is not null
            ? commandRepository.TryConsumeInviteAndInsertExternalLogin(
                Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(inviteCode.Trim()))), verifiedEmail!, link, DateTimeOffset.UtcNow, out _)
            : commandRepository.TryInsertExternalLogin(link);
        if (!inserted)
        {
            if (inviteCode is not null)
            {
                user = null;
                reason = "invite_rejected";
                return false;
            }

            bool nowLinked = TryLinkedUser(identity.Provider, identity.Subject, out user, out reason)
                || TryLinkedTenant(identity, out user, out reason);
            if (nowLinked)
            {
                if (user?.Id == target.Id) return true;
                user = null;
                reason = "subject_taken";
                return false;
            }

            user = null;
            reason = "rejected";
            return false;
        }

        state.ExternalLogins[ExternalLoginLink.Key(identity.Provider, identity.Subject)] = link;
        reason = "bound";
        return true;
    }

    private static ExternalLoginLink LinkFor(ExternalIdentity identity, string userId)
        => new(identity.Provider, identity.Subject, userId, identity.Email, identity.Issuer, identity.TenantId, identity.ObjectId);

    private GatewayUser NewSocialAdmin(ExternalIdentity identity)
    {
        string username = UsernameFromEmail(identity.Email);
        string password = GatewayPasswordHasher.Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        return new GatewayUser(
            $"user-{Guid.NewGuid():N}",
            username,
            password,
            "admin",
            true,
            identity.Email,
            identity.DisplayName,
            true,
            true,
            false);
    }

    private string UsernameFromEmail(string email, string fallback = "admin")
    {
        string local = email.Split('@')[0];
        var chars = local.Where(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-').ToArray();
        string baseName = chars.Length == 0 ? fallback : new string(chars);
        if (baseName.Length > 32)
        {
            baseName = baseName[..32];
        }

        if (!UsernameTaken(baseName))
        {
            return baseName;
        }

        for (int suffix = 2; suffix < 1000; suffix++)
        {
            string candidate = baseName + "-" + suffix.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!UsernameTaken(candidate))
            {
                return candidate;
            }
        }

        return "user-" + Guid.NewGuid().ToString("N");
    }

    private bool UsernameTaken(string username)
        => state.Users.Keys.Any(key => string.Equals(key, username, StringComparison.OrdinalIgnoreCase));
}
