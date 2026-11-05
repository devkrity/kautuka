using Kautuka.Gateway.Domain.Connectors;

namespace Kautuka.Gateway.Infrastructure.Connectors;

// Changes are persisted as one bundle and take effect only after restart. The
// in-process fence prevents a new mail flow racing a queued origin change.
internal sealed class BrokerSettingsCoordinator(
    GatewayState state, ConnectorAuthEndpoint endpoint, ExternalLoginProviders providers,
    BoundMailSessionStore sessions, BrokerMailCredentialStore mail, BrokerInstallationIdentityProvider identities) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private AuthenticationBrokerSettings? pending;

    private AuthenticationBrokerSettings Active => new(endpoint.Host ?? string.Empty, providers.GoogleClientId, providers.MicrosoftClientId);

    public async Task<AuthenticationBrokerSettingsResponse> ReadAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return new(Active, pending, sessions.IsBrokerSwitchPending); }
        finally { gate.Release(); }
    }

    public async Task<AuthenticationBrokerSettingsResponse> SaveAsync(AuthenticationBrokerSettings value, CancellationToken ct)
    {
        ConnectorAuthEndpoint parsed = ConnectorAuthEndpoint.Parse(value.Origin);
        bool restoringLocal = value.Origin is { Length: 0 } && Active.Origin.Length == 0;
        if ((!parsed.IsHosted || parsed.Host != value.Origin) && !restoringLocal || !ValidClientId(value.GoogleSocialClientId) || !ValidClientId(value.MicrosoftSocialClientId))
            throw new ArgumentException("Enter an HTTPS broker origin and valid social sign-in client IDs.");
        var next = new AuthenticationBrokerSettings(restoringLocal ? string.Empty : parsed.Host!, Normalize(value.GoogleSocialClientId), Normalize(value.MicrosoftSocialClientId));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        bool alreadyFenced = sessions.IsBrokerSwitchPending;
        bool writeAttempted = false;
        try
        {
            if (next != Active && !state.Users.Values.Any(user => user.IsActive && GatewayOwnershipAccess.IsAdmin(user)
                && user.HasPassword && !state.PasswordlessUserIds.ContainsKey(user.Id)))
                throw new InvalidOperationException("Set a local password for an active administrator before changing the authentication broker.");
            if (!sessions.TryFenceBrokerSwitch(DateTimeOffset.UtcNow)) throw new InvalidOperationException("Finish or cancel active mail sign-ins before changing the broker.");
            if (!string.Equals(next.Origin, Active.Origin, StringComparison.Ordinal)
                && (await mail.HasRetainedStateAsync(ct).ConfigureAwait(false) || await identities.HasPendingRotationAsync(ct).ConfigureAwait(false)))
                throw new InvalidOperationException("Remove saved mail connections and finish any key rotation before changing the broker.");
            writeAttempted = true;
            state.PersistRawSettingsAuthoritatively(new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["auth.broker.origin"] = next.Origin,
                ["auth.broker.google_client_id"] = next.GoogleSocialClientId ?? string.Empty,
                ["auth.broker.microsoft_client_id"] = next.MicrosoftSocialClientId ?? string.Empty,
            });
            pending = next == Active ? null : next;
            if (pending is null) sessions.ClearBrokerSwitchFence();
            return new(Active, pending, sessions.IsBrokerSwitchPending);
        }
        catch
        {
            // If the database outcome is uncertain, retain the fence until a
            // restart or explicit restore reads the authoritative configuration.
            // A previous pending value is no longer trustworthy after a write
            // was attempted, even when it was valid before this save.
            if (writeAttempted) pending = null;
            if (!alreadyFenced && !writeAttempted) sessions.ClearBrokerSwitchFence();
            throw;
        }
        finally { gate.Release(); }
    }

    private static bool ValidClientId(string? value) => value is null || value.Length <= 256
        && value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');
    private static string? Normalize(string? value) => string.IsNullOrEmpty(value) ? null : value;
    public void Dispose() => gate.Dispose();
}
