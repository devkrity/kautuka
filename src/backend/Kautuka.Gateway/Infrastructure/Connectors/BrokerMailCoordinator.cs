using System.Net;
using System.Security.Cryptography;

namespace Kautuka.Gateway.Infrastructure.Connectors;

// Serializes one Gateway process's broker transitions. CredentialsHost stores
// each complete active/pending state in one encrypted value.
internal sealed class BrokerMailCoordinator(BrokerMailCredentialStore store, BrokerHandoffClient broker, BoundMailSessionStore? sessions = null) : IDisposable
{
    // Bounded stripes prevent one slow provider request from blocking every
    // mailbox without retaining an unbounded lock per user-controlled key.
    private readonly SemaphoreSlim[] gates = Enumerable.Range(0, 128).Select(static _ => new SemaphoreSlim(1, 1)).ToArray();

    public Task<BrokerMailStoredState> ReadStateAsync(string userId, string connectorId, CancellationToken ct)
        => store.ReadAsync(userId, connectorId, ct);

    public async Task<bool> CompleteBoundAsync(BoundMailSession session, Func<bool> stillAuthorized, CancellationToken ct)
    {
        SemaphoreSlim gate = Gate(session.UserId, session.ConnectorId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!stillAuthorized()) throw new UnauthorizedAccessException();
            Uri origin = new(session.BrokerOrigin);
            BrokerMailStoredState state = await store.ReadAsync(session.UserId, session.ConnectorId, ct).ConfigureAwait(false);
            EnsureOrigin(origin, state);
            if (state.Active?.FlowId == session.FlowId)
            {
                if (!stillAuthorized()) throw new UnauthorizedAccessException();
                return true;
            }
            if (state.PendingActivation?.Credential.FlowId == session.FlowId)
            {
                if (!stillAuthorized()) throw new UnauthorizedAccessException();
                await RecoverActivationCoreAsync(origin, session.UserId, session.ConnectorId, ct, stillAuthorized).ConfigureAwait(false);
                return true;
            }
            BrokerMailResult? result = await broker.ExchangeMailAsync(origin, session.Provider, session.FlowId, session.Verifier, session.DpopPrivateJwk, ct).ConfigureAwait(false);
            if (result is null) return false;
            if (!stillAuthorized()) throw new UnauthorizedAccessException();
            var credential = new BrokerMailCredential(session.Provider, session.ConnectionId, session.FlowId, result.Generation, result.RefreshToken, session.DpopPrivateJwk, session.BrokerOrigin);
            await store.StageActivationAsync(session.UserId, session.ConnectorId, new BrokerMailPendingActivation(credential, session.Verifier), ct).ConfigureAwait(false);
            if (!stillAuthorized()) throw new UnauthorizedAccessException();
            await RecoverActivationCoreAsync(origin, session.UserId, session.ConnectorId, ct, stillAuthorized).ConfigureAwait(false);
            return true;
        }
        finally { gate.Release(); }
    }

    public async Task<BrokerMailCredential> ActivateAsync(Uri origin, string userId, string connectorId,
        BrokerMailPendingActivation pending, CancellationToken ct)
    {
        SemaphoreSlim gate = Gate(userId, connectorId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            BrokerMailStoredState state = await store.ReadAsync(userId, connectorId, ct).ConfigureAwait(false);
            EnsureOrigin(origin, state);
            EnsureOrigin(origin, pending.Credential);
            BrokerMailCredential? active = state.Active;
            if (active is not null && active.FlowId == pending.Credential.FlowId && active.Generation == pending.Credential.Generation)
            {
                if (active.Provider != pending.Credential.Provider || active.ConnectionId != pending.Credential.ConnectionId
                    || active.DpopPrivateJwk != pending.Credential.DpopPrivateJwk)
                    throw new InvalidOperationException("The mail activation does not match the active connection.");
                return active;
            }
            await store.StageActivationAsync(userId, connectorId, pending, ct).ConfigureAwait(false);
            return await RecoverActivationCoreAsync(origin, userId, connectorId, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task<BrokerMailCredential> RecoverActivationAsync(Uri origin, string userId, string connectorId, CancellationToken ct)
    {
        SemaphoreSlim gate = Gate(userId, connectorId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await RecoverActivationCoreAsync(origin, userId, connectorId, ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task<BrokerMailCredential> RecoverActivationCoreAsync(Uri origin, string userId, string connectorId, CancellationToken ct, Func<bool>? stillAuthorized = null)
    {
        BrokerMailStoredState state = await store.ReadAsync(userId, connectorId, ct).ConfigureAwait(false);
        EnsureOrigin(origin, state);
        BrokerMailPendingActivation pending = state.PendingActivation ?? throw new InvalidOperationException("There is no pending mail activation.");
        BrokerMailCredential credential = pending.Credential;
        bool active = await broker.IsMailActiveAsync(origin, credential.Provider, credential.ConnectionId, credential.Generation,
            credential.FlowId, credential.DpopPrivateJwk, ct).ConfigureAwait(false);
        if (stillAuthorized is not null && !stillAuthorized()) throw new UnauthorizedAccessException();
        if (!active)
        {
            try { await broker.AcknowledgeMailAsync(origin, credential.FlowId, pending.Verifier, credential.DpopPrivateJwk, ct).ConfigureAwait(false); }
            catch (BrokerMailRequestException e) when (e.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.Gone)
            {
                // Check again before discarding: another successful acknowledgement
                // must not be mistaken for an expired provisional flow.
                active = await broker.IsMailActiveAsync(origin, credential.Provider, credential.ConnectionId, credential.Generation,
                    credential.FlowId, credential.DpopPrivateJwk, ct).ConfigureAwait(false);
                if (!active)
                {
                    await store.DiscardActivationAsync(userId, connectorId, credential.FlowId, ct).ConfigureAwait(false);
                    throw;
                }
            }
        }
        if (stillAuthorized is not null && !stillAuthorized())
        {
            // The provider may have committed while the browser session was
            // revoked. Revoke the newly activated generation before returning.
            await broker.DisconnectMailAsync(origin, credential, CancellationToken.None).ConfigureAwait(false);
            await store.DiscardCompensatedActivationAsync(userId, connectorId, credential.FlowId, CancellationToken.None).ConfigureAwait(false);
            throw new UnauthorizedAccessException();
        }
        await store.PromoteActivationAsync(userId, connectorId, credential.FlowId, ct).ConfigureAwait(false);
        if (stillAuthorized is not null && !stillAuthorized())
        {
            // Persist the unusable state before compensating, so a failed
            // network revocation never leaves the credential usable locally.
            await store.MarkActiveUnavailableAsync(userId, connectorId, credential.FlowId, CancellationToken.None).ConfigureAwait(false);
            await broker.DisconnectMailAsync(origin, credential, CancellationToken.None).ConfigureAwait(false);
            BrokerMailStoredState revoked = await store.ReadAsync(userId, connectorId, CancellationToken.None).ConfigureAwait(false);
            await store.ClearConnectionAsync(userId, connectorId, revoked, CancellationToken.None).ConfigureAwait(false);
            throw new UnauthorizedAccessException();
        }
        return credential;
    }

    public async Task<BrokerMailRefreshResult?> RefreshAsync(Uri origin, string userId, string connectorId, CancellationToken ct)
    {
        SemaphoreSlim gate = Gate(userId, connectorId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            BrokerMailStoredState state = await store.ReadAsync(userId, connectorId, ct).ConfigureAwait(false);
            EnsureOrigin(origin, state);
            if (state.RequiresReconnect || state.DisconnectPending) throw new InvalidOperationException("Reconnect the mail account before using it.");
            if (state.PendingActivation is not null) throw new InvalidOperationException("Mail activation must complete before refresh.");
            BrokerMailCredential active = state.Active ?? throw new InvalidOperationException("The mail account is not connected.");
            if (state.PendingRefresh?.ReplacementRefreshToken is not null)
            {
                await AcknowledgeRefreshCoreAsync(origin, userId, connectorId, active, state.PendingRefresh, ct).ConfigureAwait(false);
                state = await store.ReadAsync(userId, connectorId, ct).ConfigureAwait(false);
                active = state.Active ?? throw new InvalidOperationException("The mail account is not connected.");
            }
            BrokerMailPendingRefresh? pending = state.PendingRefresh;
            if (pending is null)
            {
                string operationId = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                await store.StageRefreshAsync(userId, connectorId, operationId, ct).ConfigureAwait(false);
                pending = (await store.ReadAsync(userId, connectorId, ct).ConfigureAwait(false)).PendingRefresh
                    ?? throw new InvalidOperationException("The mail refresh could not be staged.");
            }
            BrokerMailRefreshResult? result;
            try
            {
                result = await broker.RefreshMailAsync(origin, active.Provider, active.ConnectionId, active.Generation,
                    active.FlowId, pending.OperationId, pending.InputRefreshToken, active.DpopPrivateJwk, ct).ConfigureAwait(false);
            }
            catch (BrokerMailRequestException e) when (e.StatusCode is HttpStatusCode.Gone or HttpStatusCode.Conflict
                || e.MailError == "insufficient_scope")
            {
                await store.MarkRefreshUnknownAsync(userId, connectorId, pending.OperationId, ct).ConfigureAwait(false);
                throw;
            }
            if (result is null) return null;
            await store.SaveRefreshResultAsync(userId, connectorId, pending.OperationId, result.RefreshToken, ct).ConfigureAwait(false);
            BrokerMailStoredState saved = await store.ReadAsync(userId, connectorId, ct).ConfigureAwait(false);
            await AcknowledgeRefreshCoreAsync(origin, userId, connectorId, saved.Active!, saved.PendingRefresh!, ct).ConfigureAwait(false);
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (result.ExpiresAt is long expiry && expiry <= now || result.ExpiresAt is null && (result.IssuedAt > now + 30_000 || result.IssuedAt < now - 30_000))
                return null;
            return result;
        }
        finally { gate.Release(); }
    }

    private async Task AcknowledgeRefreshCoreAsync(Uri origin, string userId, string connectorId, BrokerMailCredential active,
        BrokerMailPendingRefresh pending, CancellationToken ct)
    {
        try
        {
            await broker.AcknowledgeMailRefreshAsync(origin, active.Provider, active.ConnectionId, active.Generation,
                active.FlowId, pending.OperationId, active.DpopPrivateJwk, ct).ConfigureAwait(false);
        }
        catch (BrokerMailRequestException e) when (e.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.Gone)
        {
            await store.MarkConnectionUnavailableAsync(userId, connectorId, pending.OperationId, ct).ConfigureAwait(false);
            throw;
        }
        await store.ClearRefreshAsync(userId, connectorId, pending.OperationId, ct).ConfigureAwait(false);
    }

    public async Task DisconnectAsync(Uri origin, string userId, string connectorId, CancellationToken ct)
    {
        SemaphoreSlim gate = Gate(userId, connectorId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureOrigin(origin, await store.ReadAsync(userId, connectorId, ct).ConfigureAwait(false));
            await store.MarkDisconnectPendingAsync(userId, connectorId, ct).ConfigureAwait(false);
            if (sessions is not null)
            {
                foreach (BoundMailSession session in sessions.CancelForConnector(userId, connectorId, DateTimeOffset.UtcNow))
                {
                    try { await broker.CancelMailAsync(new Uri(session.BrokerOrigin), session.FlowId, session.DpopPrivateJwk, ct).ConfigureAwait(false); }
                    catch (BrokerMailRequestException e) when (e.StatusCode == HttpStatusCode.Conflict)
                    {
                        BrokerMailStoredState current = await store.ReadAsync(userId, connectorId, ct).ConfigureAwait(false);
                        if (current.PendingActivation?.Credential.FlowId != session.FlowId && current.Active?.FlowId != session.FlowId) throw;
                    }
                    sessions.RemoveCancelled(session);
                }
            }
            BrokerMailStoredState state = await store.ReadAsync(userId, connectorId, ct).ConfigureAwait(false);
            EnsureOrigin(origin, state);
            if (state.PendingActivation is not null)
            {
                BrokerMailCredential pending = state.PendingActivation.Credential;
                try
                {
                    await broker.CancelMailAsync(origin, pending.FlowId, pending.DpopPrivateJwk, ct).ConfigureAwait(false);
                    await store.DiscardActivationAsync(userId, connectorId, pending.FlowId, ct).ConfigureAwait(false);
                }
                catch (BrokerMailRequestException e) when (e.StatusCode == HttpStatusCode.Conflict)
                {
                    if (await broker.IsMailActiveAsync(origin, pending.Provider, pending.ConnectionId, pending.Generation,
                        pending.FlowId, pending.DpopPrivateJwk, ct).ConfigureAwait(false))
                        await store.PromoteActivationAsync(userId, connectorId, pending.FlowId, ct, disconnectPending: true).ConfigureAwait(false);
                    else
                        await store.DiscardCompensatedActivationAsync(userId, connectorId, pending.FlowId, ct).ConfigureAwait(false);
                }
                state = await store.ReadAsync(userId, connectorId, ct).ConfigureAwait(false);
            }
            if (state.Active is not null)
            {
                try { await broker.DisconnectMailAsync(origin, state.Active, ct).ConfigureAwait(false); }
                catch (BrokerMailRequestException e) when (e.StatusCode == HttpStatusCode.Conflict)
                {
                    // The original disconnect may have succeeded long enough ago
                    // that its broker retry marker has expired. Only clear locally
                    // after confirming this exact generation is no longer active.
                    if (await broker.IsMailActiveAsync(origin, state.Active.Provider, state.Active.ConnectionId, state.Active.Generation,
                        state.Active.FlowId, state.Active.DpopPrivateJwk, ct).ConfigureAwait(false)) throw;
                }
            }
            await store.ClearConnectionAsync(userId, connectorId, state, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    // A cancellation belongs to one browser flow. It must never revoke a later
    // activation for the same user and connector.
    public async Task CancelFlowAsync(BoundMailSession session, CancellationToken ct)
    {
        SemaphoreSlim gate = Gate(session.UserId, session.ConnectorId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Uri origin = new(session.BrokerOrigin);
            BrokerMailStoredState state = await store.ReadAsync(session.UserId, session.ConnectorId, ct).ConfigureAwait(false);
            EnsureOrigin(origin, state);
            if (state.PendingActivation?.Credential.FlowId == session.FlowId)
            {
                BrokerMailCredential pending = state.PendingActivation.Credential;
                try
                {
                    await broker.CancelMailAsync(origin, session.FlowId, session.DpopPrivateJwk, ct).ConfigureAwait(false);
                    // A previous compensation may have succeeded at the broker even
                    // if its response was lost. Confirm the old generation remains
                    // active before presenting it as connected again.
                    BrokerMailCredential? previous = state.Active;
                    if (previous is not null && !await broker.IsMailActiveAsync(origin, previous.Provider, previous.ConnectionId,
                        previous.Generation, previous.FlowId, previous.DpopPrivateJwk, ct).ConfigureAwait(false))
                        await store.DiscardCompensatedActivationAsync(session.UserId, session.ConnectorId, session.FlowId, ct).ConfigureAwait(false);
                    else
                        await store.DiscardActivationAsync(session.UserId, session.ConnectorId, session.FlowId, ct).ConfigureAwait(false);
                }
                catch (BrokerMailRequestException e) when (e.StatusCode == HttpStatusCode.Conflict)
                {
                    if (await broker.IsMailActiveAsync(origin, pending.Provider, pending.ConnectionId, pending.Generation, pending.FlowId, pending.DpopPrivateJwk, ct).ConfigureAwait(false))
                    {
                        try { await broker.DisconnectMailAsync(origin, pending, ct).ConfigureAwait(false); }
                        catch (BrokerMailRequestException disconnect) when (disconnect.StatusCode == HttpStatusCode.Conflict)
                        {
                            if (await broker.IsMailActiveAsync(origin, pending.Provider, pending.ConnectionId, pending.Generation,
                                pending.FlowId, pending.DpopPrivateJwk, ct).ConfigureAwait(false)) throw;
                        }
                    }
                    await store.DiscardCompensatedActivationAsync(session.UserId, session.ConnectorId, session.FlowId, ct).ConfigureAwait(false);
                }
                return;
            }
            if (state.Active?.FlowId == session.FlowId)
            {
                if (state.PendingActivation is not null) throw new InvalidOperationException("A newer mail activation is pending.");
                await store.MarkDisconnectPendingAsync(session.UserId, session.ConnectorId, ct).ConfigureAwait(false);
                state = await store.ReadAsync(session.UserId, session.ConnectorId, ct).ConfigureAwait(false);
                BrokerMailCredential active = state.Active ?? throw new InvalidOperationException("The mail connection changed during cancellation.");
                try { await broker.DisconnectMailAsync(origin, active, ct).ConfigureAwait(false); }
                catch (BrokerMailRequestException e) when (e.StatusCode == HttpStatusCode.Conflict)
                {
                    if (await broker.IsMailActiveAsync(origin, active.Provider, active.ConnectionId, active.Generation,
                        active.FlowId, active.DpopPrivateJwk, ct).ConfigureAwait(false)) throw;
                }
                await store.ClearConnectionAsync(session.UserId, session.ConnectorId, state, ct).ConfigureAwait(false);
                return;
            }
            // Cancel only this flow at the broker; another local active flow may
            // be the older connection being replaced, and must not be revoked.
            await broker.CancelMailAsync(origin, session.FlowId, session.DpopPrivateJwk, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private static void EnsureOrigin(Uri origin, BrokerMailStoredState state)
    {
        if (state.Active is not null) EnsureOrigin(origin, state.Active);
        if (state.PendingActivation is not null) EnsureOrigin(origin, state.PendingActivation.Credential);
    }

    private static void EnsureOrigin(Uri origin, BrokerMailCredential credential)
    {
        if (!string.Equals(origin.GetLeftPart(UriPartial.Authority), credential.BrokerOrigin, StringComparison.Ordinal))
            throw new InvalidOperationException("This mail connection belongs to a different authentication broker.");
    }

    private SemaphoreSlim Gate(string userId, string connectorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);
        byte[] hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{userId.Length}:{userId}{connectorId}"));
        return gates[hash[0] % gates.Length];
    }

    public void Dispose()
    {
        foreach (SemaphoreSlim gate in gates) gate.Dispose();
    }
}
