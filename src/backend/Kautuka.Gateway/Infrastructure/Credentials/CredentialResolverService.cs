using System.Diagnostics;

using Grpc.Core;

using Kautuka.Gateway.Infrastructure.Observability;
using Kautuka.Protos;

namespace Kautuka.Gateway.Infrastructure.Credentials;

/// <summary>
/// Thin gRPC client wrapper for the CredentialsHost service.
/// No caching — every call goes to CredentialsHost directly.
/// Gateway must hold decrypted values only in-memory for the active request duration.
/// All transport, authorization, cancellation, and integrity failures remain distinct.
/// Only an authoritative response can represent an absent credential.
/// </summary>
internal sealed class CredentialResolverService(
    Lazy<CredentialsService.CredentialsServiceClient> clientFactory,
    string credentialsGrpcAddress = "")
{
    private CredentialsService.CredentialsServiceClient Client
    {
        get
        {
            try
            {
                return clientFactory.Value;
            }
            catch (InvalidOperationException exception)
            {
                throw new RpcException(new Status(StatusCode.Unavailable, "CredentialsHost client material is unavailable."), exception.Message);
            }
        }
    }

    /// <summary>
    /// Resolves credentials for the given user and required keys.
    /// User-scope wins over system-scope. Missing keys are absent from the result — no error.
    /// </summary>
    public Task<IReadOnlyDictionary<string, string>> GetResolvedAsync(
        string userId,
        IEnumerable<string> requiredKeys,
        CancellationToken ct = default)
        => GetResolvedAsync(userId, requiredKeys, includeSystemScope: false, includeHeldSecrets: false, ct);

    public async Task<IReadOnlyDictionary<string, string>> GetResolvedAsync(
        string userId,
        IEnumerable<string> requiredKeys,
        bool includeSystemScope,
        bool includeHeldSecrets,
        CancellationToken ct = default)
    {
        var request = new GetResolvedCredentialsRequest
        {
            UserId = userId,
            Access = Access(userId, CredentialCapability.RuntimeResolve),
            IncludeSystemScope = includeSystemScope,
            IncludeHeldSecrets = includeHeldSecrets,
        };
        request.RequiredKeys.AddRange(requiredKeys);

        CredentialMapResponse response = await CallAsync(
            Client,
            "GetResolvedCredentials",
            client => client.GetResolvedCredentialsAsync(request, cancellationToken: ct).ResponseAsync)
            .ConfigureAwait(false);

        return response.Entries;
    }

    public async Task<CredentialHealthResponse> GetHealthAsync(CancellationToken ct = default)
        => await CallAsync(
            Client,
            "GetHealth",
            client => client.GetHealthAsync(new CredentialHealthRequest(), cancellationToken: ct).ResponseAsync)
            .ConfigureAwait(false);

    /// <summary>
    /// Retrieves a single system-scope credential by key. Returns null if not found.
    /// </summary>
    public async Task<string?> GetSystemAsync(string key, CancellationToken ct = default)
    {
        var request = new GetSystemCredentialRequest
        {
            Key = key,
            Access = Access("gateway-runtime", CredentialCapability.RuntimeResolve)
        };

        CredentialMapResponse response = await CallAsync(
            Client,
            "GetSystemCredential",
            client => client.GetSystemCredentialAsync(request, cancellationToken: ct).ResponseAsync)
            .ConfigureAwait(false);

        return response.Entries.TryGetValue(key, out string? value) ? value : null;
    }

    public async Task<IReadOnlyList<string>> ListUserKeysAsync(string userId, CancellationToken ct = default)
        => await ListUserKeysCoreAsync(userId, userId, CredentialCapability.OwnUserAdmin, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<string>> ListUserKeysAsAdminAsync(string actorId, string userId, CancellationToken ct = default)
        => await ListUserKeysCoreAsync(userId, actorId, CredentialCapability.CrossUserAdmin, ct).ConfigureAwait(false);

    private async Task<IReadOnlyList<string>> ListUserKeysCoreAsync(
        string userId,
        string actorId,
        CredentialCapability capability,
        CancellationToken ct)
    {
        CredentialKeyListResponse response = await CallAsync(
            Client,
            "ListUserCredentialKeys",
            client => client.ListUserCredentialKeysAsync(new ListUserCredentialKeysRequest
            {
                UserId = userId,
                Access = Access(actorId, capability)
            }, cancellationToken: ct).ResponseAsync)
            .ConfigureAwait(false);
        return [.. response.Keys];
    }

    public async Task<int> SetUserAsync(string userId, IDictionary<string, string> entries, CancellationToken ct = default)
        => await SetUserCoreAsync(userId, userId, CredentialCapability.OwnUserAdmin, entries, ct).ConfigureAwait(false);

    public async Task<int> SetUserAsAdminAsync(string actorId, string userId, IDictionary<string, string> entries, CancellationToken ct = default)
        => await SetUserCoreAsync(userId, actorId, CredentialCapability.CrossUserAdmin, entries, ct).ConfigureAwait(false);

    private async Task<int> SetUserCoreAsync(
        string userId,
        string actorId,
        CredentialCapability capability,
        IDictionary<string, string> entries,
        CancellationToken ct)
    {
        var request = new SetUserCredentialsRequest
        {
            UserId = userId,
            Access = Access(actorId, capability),
            OperationId = Guid.NewGuid().ToString("N")
        };
        foreach ((string key, string value) in entries)
            request.Entries[key] = value;

        SetCredentialsResponse response = await CallAsync(
            Client,
            "SetUserCredentials",
            client => client.SetUserCredentialsAsync(request, cancellationToken: ct).ResponseAsync)
            .ConfigureAwait(false);

        return response.UpsertedCount;
    }

    public async Task<bool> DeleteUserAsync(string userId, string key, CancellationToken ct = default)
        => await DeleteUserCoreAsync(userId, userId, CredentialCapability.OwnUserAdmin, key, ct).ConfigureAwait(false);

    public async Task<bool> DeleteUserAsAdminAsync(string actorId, string userId, string key, CancellationToken ct = default)
        => await DeleteUserCoreAsync(userId, actorId, CredentialCapability.CrossUserAdmin, key, ct).ConfigureAwait(false);

    private async Task<bool> DeleteUserCoreAsync(
        string userId,
        string actorId,
        CredentialCapability capability,
        string key,
        CancellationToken ct)
    {
        DeleteCredentialResponse response = await CallAsync(
            Client,
            "DeleteUserCredential",
                client => client.DeleteUserCredentialAsync(new DeleteUserCredentialRequest
                {
                    UserId = userId,
                    Key = key,
                    Access = Access(actorId, capability),
                    OperationId = Guid.NewGuid().ToString("N")
                }, cancellationToken: ct).ResponseAsync)
            .ConfigureAwait(false);

        return response.Deleted;
    }

    public async Task<IReadOnlyList<string>> ListSystemKeysAsync(CancellationToken ct = default)
    {
        CredentialKeyListResponse response = await CallAsync(
            Client,
            "ListSystemCredentialKeys",
            client => client.ListSystemCredentialKeysAsync(new ListSystemCredentialKeysRequest
            {
                Access = Access("gateway-system", CredentialCapability.SystemAdmin)
            }, cancellationToken: ct).ResponseAsync)
            .ConfigureAwait(false);
        return [.. response.Keys];
    }

    public async Task<int> SetSystemAsync(IDictionary<string, string> entries, CancellationToken ct = default)
    {
        var request = new SetSystemCredentialsRequest
        {
            Access = Access("gateway-system", CredentialCapability.SystemAdmin),
            OperationId = Guid.NewGuid().ToString("N")
        };
        foreach ((string key, string value) in entries)
            request.Entries[key] = value;

        SetCredentialsResponse response = await CallAsync(
            Client,
            "SetSystemCredentials",
            client => client.SetSystemCredentialsAsync(request, cancellationToken: ct).ResponseAsync)
            .ConfigureAwait(false);

        return response.UpsertedCount;
    }

    public async Task<bool> DeleteSystemAsync(string key, CancellationToken ct = default)
    {
        DeleteCredentialResponse response = await CallAsync(
            Client,
            "DeleteSystemCredential",
            client => client.DeleteSystemCredentialAsync(new DeleteSystemCredentialRequest
            {
                Key = key,
                Access = Access("gateway-system", CredentialCapability.SystemAdmin),
                OperationId = Guid.NewGuid().ToString("N")
            }, cancellationToken: ct).ResponseAsync)
            .ConfigureAwait(false);

        return response.Deleted;
    }

    private async Task<TResponse> CallAsync<TResponse>(
        CredentialsService.CredentialsServiceClient client,
        string methodName,
        Func<CredentialsService.CredentialsServiceClient, Task<TResponse>> operation)
    {
        using Activity? activity = AgentRunHostCallTracer.StartGrpcClientActivity(
            KautukaActivitySources.Component.CredentialsHost,
            methodName,
            credentialsGrpcAddress);

        try
        {
            TResponse response = await operation(client).ConfigureAwait(false);
            AgentRunHostCallTracer.MarkSuccess(activity);
            return response;
        }
        catch (Exception ex)
        {
            AgentRunHostCallTracer.MarkFailure(activity, ex);
            throw;
        }
    }

    private static CredentialAccessContext Access(string actorId, CredentialCapability capability)
        => new() { ActorId = actorId, Capability = capability };
}
