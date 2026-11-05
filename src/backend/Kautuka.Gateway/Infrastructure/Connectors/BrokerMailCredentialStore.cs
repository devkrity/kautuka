using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Kautuka.Gateway.Infrastructure.Credentials;

namespace Kautuka.Gateway.Infrastructure.Connectors;

// One encrypted CredentialsHost value contains both the active and pending
// state. A crash cannot publish half of a credential bundle.
internal sealed record BrokerMailCredential(string Provider, string ConnectionId, string FlowId, long Generation,
    string RefreshToken, string DpopPrivateJwk, string BrokerOrigin)
{
    public override string ToString() => $"BrokerMailCredential {{ Provider = {Provider}, ConnectionId = {ConnectionId}, FlowId = {FlowId}, Generation = {Generation}, Credentials = [redacted] }}";
}

internal sealed record BrokerMailPendingActivation(BrokerMailCredential Credential, string Verifier)
{
    public override string ToString() => "BrokerMailPendingActivation { Credentials = [redacted] }";
}

internal sealed record BrokerMailPendingRefresh(string OperationId, string InputRefreshToken, string? ReplacementRefreshToken)
{
    public override string ToString() => $"BrokerMailPendingRefresh {{ OperationId = {OperationId}, Credentials = [redacted] }}";
}

internal sealed record BrokerMailStoredState(BrokerMailCredential? Active, BrokerMailPendingActivation? PendingActivation, BrokerMailPendingRefresh? PendingRefresh,
    string? LastCompletedRefresh = null, bool RequiresReconnect = false, string? LastUnknownRefresh = null, bool DisconnectPending = false)
{
    public override string ToString() => "BrokerMailStoredState { Credentials = [redacted] }";
}

[JsonSerializable(typeof(BrokerMailStoredState))]
[JsonSerializable(typeof(string[]))]
internal sealed partial class BrokerMailStateJsonContext : JsonSerializerContext;

internal interface IBrokerMailCredentialStorage
{
    Task<string?> ReadAsync(string key, CancellationToken ct);
    Task WriteAsync(string key, string value, CancellationToken ct);
    Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken ct) => throw new NotSupportedException("Credential enumeration is unavailable.");
}

internal sealed class CredentialsHostBrokerMailCredentialStorage(CredentialResolverService resolver) : IBrokerMailCredentialStorage
{
    public Task<string?> ReadAsync(string key, CancellationToken ct) => resolver.GetSystemAsync(key, ct);
    public Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken ct) => resolver.ListSystemKeysAsync(ct);
    public async Task WriteAsync(string key, string value, CancellationToken ct)
        => _ = await resolver.SetSystemAsync(new Dictionary<string, string>(StringComparer.Ordinal) { [key] = value }, ct).ConfigureAwait(false);
}

// Gateway runs as a single service process; serialize all updates to avoid
// replacing another request's pending operation with a stale read.
internal sealed class BrokerMailCredentialStore(IBrokerMailCredentialStorage storage) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<BrokerMailStoredState> ReadAsync(string userId, string connectorId, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await ReadCoreAsync(Key(userId, connectorId), ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    public async Task<bool> HasRetainedStateAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (string key in await storage.ListKeysAsync(ct).ConfigureAwait(false))
            {
                if (!key.StartsWith("kautuka.internal.mail.", StringComparison.Ordinal)) continue;
                BrokerMailStoredState state = await ReadCoreAsync(key, ct).ConfigureAwait(false);
                if (state.Active is not null || state.PendingActivation is not null || state.PendingRefresh is not null
                    || state.RequiresReconnect || state.DisconnectPending) return true;
            }
            return false;
        }
        finally { gate.Release(); }
    }

    public Task StageActivationAsync(string userId, string connectorId, BrokerMailPendingActivation pending, CancellationToken ct)
        => ChangeAsync(userId, connectorId, state =>
        {
            Validate(pending.Credential);
            if (state.PendingActivation == pending) return state;
            if (state.Active is not null && (state.Active.Provider != pending.Credential.Provider
                || state.Active.ConnectionId != pending.Credential.ConnectionId || state.Active.BrokerOrigin != pending.Credential.BrokerOrigin))
                throw new InvalidOperationException("A replacement must use the existing mail connection.");
            if (state.Active?.FlowId == pending.Credential.FlowId && state.Active.Generation == pending.Credential.Generation)
                throw new InvalidOperationException("This mail activation is already active.");
            if (!IsVerifier(pending.Verifier) || state.PendingActivation is not null || state.PendingRefresh is not null || state.DisconnectPending)
                throw new InvalidOperationException("A mail operation is already pending or invalid.");
            return state with { PendingActivation = pending };
        }, ct);

    public Task PromoteActivationAsync(string userId, string connectorId, string flowId, CancellationToken ct, bool disconnectPending = false)
        => ChangeAsync(userId, connectorId, state =>
        {
            if (state.PendingActivation is null && state.Active?.FlowId == flowId) return state;
            if (state.PendingActivation?.Credential.FlowId != flowId) throw new InvalidOperationException("The pending mail activation changed.");
            return state with { Active = state.PendingActivation.Credential, PendingActivation = null, RequiresReconnect = false, LastUnknownRefresh = null, DisconnectPending = disconnectPending };
        }, ct);

    // Call only after the broker has confirmed cancellation or expiry.
    public Task DiscardActivationAsync(string userId, string connectorId, string flowId, CancellationToken ct)
        => ChangeAsync(userId, connectorId, state =>
        {
            if (state.PendingActivation?.Credential.FlowId != flowId) throw new InvalidOperationException("The pending mail activation changed.");
            return state with { PendingActivation = null };
        }, ct);

    // An acknowledged replacement invalidated the prior broker generation.
    // If it was revoked to compensate for lost authorization, the old local
    // credential must not be presented as connected or usable.
    public Task DiscardCompensatedActivationAsync(string userId, string connectorId, string flowId, CancellationToken ct)
        => ChangeAsync(userId, connectorId, state =>
        {
            if (state.PendingActivation?.Credential.FlowId != flowId) throw new InvalidOperationException("The pending mail activation changed.");
            return state.Active is null ? state with { PendingActivation = null }
                : state with { PendingActivation = null, RequiresReconnect = true, LastUnknownRefresh = flowId };
        }, ct);

    // Call only after broker revocation/cancellation has been confirmed. The
    // expected snapshot prevents a delayed disconnect from deleting a newer flow.
    public Task ClearConnectionAsync(string userId, string connectorId, BrokerMailStoredState expected, CancellationToken ct)
        => ChangeAsync(userId, connectorId, state => state == expected
            ? new BrokerMailStoredState(null, null, null)
            : throw new InvalidOperationException("The mail connection changed during disconnect."), ct);

    public Task StageRefreshAsync(string userId, string connectorId, string operationId, CancellationToken ct)
        => ChangeAsync(userId, connectorId, state =>
        {
            if (state.PendingRefresh?.OperationId == operationId && state.PendingRefresh.ReplacementRefreshToken is null
                && state.Active?.RefreshToken == state.PendingRefresh.InputRefreshToken) return state;
            if (!IsId(operationId) || state.Active is null || state.RequiresReconnect || state.DisconnectPending || state.PendingActivation is not null || state.PendingRefresh is not null)
                throw new InvalidOperationException("A mail operation is already pending or invalid.");
            if (state.LastCompletedRefresh == operationId) throw new InvalidOperationException("The mail refresh operation has already completed.");
            return state with { PendingRefresh = new BrokerMailPendingRefresh(operationId, state.Active.RefreshToken, null) };
        }, ct);

    // An ambiguous provider refresh cannot safely be retried. Keep the broker
    // generation for reconnect, but never use its old credential again.
    public Task MarkRefreshUnknownAsync(string userId, string connectorId, string operationId, CancellationToken ct)
        => ChangeAsync(userId, connectorId, state =>
        {
            if (state.RequiresReconnect && state.PendingRefresh is null && state.LastUnknownRefresh == operationId) return state;
            if (state.PendingRefresh?.OperationId != operationId || state.PendingRefresh.ReplacementRefreshToken is not null)
                throw new InvalidOperationException("The pending mail refresh changed.");
            return state with { PendingRefresh = null, RequiresReconnect = true, LastUnknownRefresh = operationId };
        }, ct);

    public Task MarkConnectionUnavailableAsync(string userId, string connectorId, string operationId, CancellationToken ct)
        => ChangeAsync(userId, connectorId, state =>
        {
            if (state.RequiresReconnect && state.PendingRefresh is null && state.LastUnknownRefresh == operationId) return state;
            if (state.PendingRefresh?.OperationId != operationId || state.Active is null)
                throw new InvalidOperationException("The pending mail refresh changed.");
            return state with { PendingRefresh = null, RequiresReconnect = true, LastUnknownRefresh = operationId };
        }, ct);

    public Task MarkActiveUnavailableAsync(string userId, string connectorId, string flowId, CancellationToken ct)
        => ChangeAsync(userId, connectorId, state =>
        {
            if (state.Active?.FlowId != flowId || state.PendingActivation is not null || state.PendingRefresh is not null)
                throw new InvalidOperationException("The mail connection changed.");
            return state with { RequiresReconnect = true, LastUnknownRefresh = flowId };
        }, ct);

    // Retain the credential needed for remote revocation but stop local use
    // before attempting a network operation that may fail or be interrupted.
    public Task MarkDisconnectPendingAsync(string userId, string connectorId, CancellationToken ct)
        => ChangeAsync(userId, connectorId, state => state.Active is null && state.PendingActivation is null ? state : state with { DisconnectPending = true }, ct);

    public Task SaveRefreshResultAsync(string userId, string connectorId, string operationId, string replacement, CancellationToken ct)
        => ChangeAsync(userId, connectorId, state =>
        {
            if (state.PendingRefresh?.OperationId == operationId && state.PendingRefresh.ReplacementRefreshToken == replacement
                && state.Active?.RefreshToken == replacement) return state;
            if (state.Active is null || state.PendingRefresh is null || state.PendingRefresh.OperationId != operationId
                || state.Active.RefreshToken != state.PendingRefresh.InputRefreshToken || !IsEnvelope(replacement))
                throw new InvalidOperationException("The pending mail refresh changed.");
            return state with
            {
                Active = state.Active with { RefreshToken = replacement },
                PendingRefresh = state.PendingRefresh with { ReplacementRefreshToken = replacement }
            };
        }, ct);

    public Task ClearRefreshAsync(string userId, string connectorId, string operationId, CancellationToken ct)
        => ChangeAsync(userId, connectorId, state =>
        {
            if (state.PendingRefresh is null && state.LastCompletedRefresh == operationId) return state;
            if (state.PendingRefresh?.OperationId != operationId || state.PendingRefresh.ReplacementRefreshToken is null
                || state.Active?.RefreshToken != state.PendingRefresh.ReplacementRefreshToken)
                throw new InvalidOperationException("The pending mail refresh changed.");
            return state with { PendingRefresh = null, LastCompletedRefresh = operationId };
        }, ct);

    private async Task ChangeAsync(string userId, string connectorId, Func<BrokerMailStoredState, BrokerMailStoredState> change, CancellationToken ct)
    {
        string key = Key(userId, connectorId);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            BrokerMailStoredState next = change(await ReadCoreAsync(key, ct).ConfigureAwait(false));
            string json = JsonSerializer.Serialize(next, BrokerMailStateJsonContext.Default.BrokerMailStoredState);
            await storage.WriteAsync(key, json, ct).ConfigureAwait(false);
            if (!string.Equals(await storage.ReadAsync(key, ct).ConfigureAwait(false), json, StringComparison.Ordinal))
                throw new InvalidOperationException("The mail connection could not be persisted.");
        }
        finally { gate.Release(); }
    }

    private async Task<BrokerMailStoredState> ReadCoreAsync(string key, CancellationToken ct)
    {
        string? json = await storage.ReadAsync(key, ct).ConfigureAwait(false);
        if (json is null) return new BrokerMailStoredState(null, null, null);
        try
        {
            BrokerMailStoredState state = JsonSerializer.Deserialize(json, BrokerMailStateJsonContext.Default.BrokerMailStoredState) ?? throw new JsonException();
            if (state.Active is not null) Validate(state.Active);
            if (state.PendingActivation is not null)
            {
                Validate(state.PendingActivation.Credential);
                if (!IsVerifier(state.PendingActivation.Verifier) || state.PendingRefresh is not null
                    || state.Active is not null && (state.Active.Provider != state.PendingActivation.Credential.Provider
                        || state.Active.ConnectionId != state.PendingActivation.Credential.ConnectionId
                        || state.Active.BrokerOrigin != state.PendingActivation.Credential.BrokerOrigin)) throw new JsonException();
            }
            if (state.PendingRefresh is not null && (state.Active is null || !IsId(state.PendingRefresh.OperationId)
                || !IsEnvelope(state.PendingRefresh.InputRefreshToken) || state.RequiresReconnect
                || state.PendingRefresh.ReplacementRefreshToken is null && state.Active.RefreshToken != state.PendingRefresh.InputRefreshToken
                || state.PendingRefresh.ReplacementRefreshToken is not null && (!IsEnvelope(state.PendingRefresh.ReplacementRefreshToken)
                    || state.Active.RefreshToken != state.PendingRefresh.ReplacementRefreshToken))) throw new JsonException();
            if (state.RequiresReconnect && (state.Active is null || !IsId(state.LastUnknownRefresh))
                || state.LastCompletedRefresh is not null && !IsId(state.LastCompletedRefresh)
                || state.LastUnknownRefresh is not null && !IsId(state.LastUnknownRefresh)) throw new JsonException();
            return state;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or CryptographicException or NullReferenceException)
        { throw new InvalidDataException("The saved mail connection is invalid.", e); }
    }

    private static string Key(string userId, string connectorId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { userId, connectorId }, BrokerMailStateJsonContext.Default.StringArray)));
        return "kautuka.internal.mail." + Convert.ToHexString(bytes);
    }

    private static void Validate(BrokerMailCredential value)
    {
        if (value.Provider is not ("google" or "microsoft") || !IsId(value.ConnectionId) || !IsId(value.FlowId)
            || value.Generation is < 1 or > 9007199254740991L || !IsEnvelope(value.RefreshToken))
            throw new ArgumentException("The mail credential is invalid.");
        if (string.IsNullOrEmpty(value.BrokerOrigin) || Kautuka.Gateway.Domain.Connectors.ConnectorAuthEndpoint.Parse(value.BrokerOrigin).Host != value.BrokerOrigin)
            throw new ArgumentException("The mail credential broker origin is invalid.");
        _ = Kautuka.Gateway.Domain.Connectors.ConnectorDpop.Thumbprint(value.DpopPrivateJwk);
    }

    private static bool IsEnvelope(string value) => value is { Length: > 20 and <= 12000 } && value.StartsWith("kmf1.", StringComparison.Ordinal);
    private static bool IsVerifier(string? value) => value is { Length: >= 43 and <= 128 }
        && value.All(static c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-' or '.' or '~');
    private static bool IsId(string? value) => value is { Length: 43 } && value.All(static c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-');
    public void Dispose() => gate.Dispose();
}
