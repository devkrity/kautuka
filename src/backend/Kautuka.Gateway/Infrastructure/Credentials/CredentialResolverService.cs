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

    private static CredentialAccessContext DraftAccess(string userId, string scope)
        => Access(userId, scope == "system" ? CredentialCapability.SystemAdmin : CredentialCapability.OwnUserAdmin);

    public async Task<string> StageAssociationAsync(string draftId, string userId, string scope, string? sourceId, CancellationToken ct)
    {
        var request = new StageConnectorAssociationRequest { Access = DraftAccess(userId, scope), DraftId = draftId, SourceAssociationId = sourceId ?? string.Empty };
        StageConnectorAssociationResponse response = await CallAsync(Client, "StageConnectorAssociation", client => client.StageConnectorAssociationAsync(request, cancellationToken: ct).ResponseAsync).ConfigureAwait(false);
        return response.AssociationId;
    }

    public async Task<string> StageMutationAsync(string operationId, string userId, string scope, string connectorId, string credentialNamespace, string? sourceId, IReadOnlyDictionary<string, string> entries, IReadOnlyList<string> removals, CancellationToken ct)
    {
        var request = new StageConnectorMutationRequest { Access = DraftAccess(userId, scope), OperationId = operationId, ConnectorId = connectorId, CredentialNamespace = credentialNamespace, SourceAssociationId = sourceId ?? string.Empty, CredentialScope = scope };
        foreach ((string key, string value) in entries) request.Entries[key] = value;
        request.RemoveKeys.AddRange(removals);
        StageConnectorAssociationResponse response = await CallAsync(Client, "StageConnectorMutation", client => client.StageConnectorMutationAsync(request, cancellationToken: ct).ResponseAsync).ConfigureAwait(false);
        return response.AssociationId;
    }

    public async Task<ConnectorDraftMetadata> GetStagedDraftAsync(string draftId, string userId, CancellationToken ct)
        => await CallAsync(Client, "GetStagedConnectorDraft", client => client.GetStagedConnectorDraftAsync(new ConnectorDraftAccessRequest { Access = DraftAccess(userId, "user"), DraftId = draftId }, cancellationToken: ct).ResponseAsync).ConfigureAwait(false);

    public async Task<IReadOnlyDictionary<string, string>> ResolveAssociationAsync(string associationId, string connectorId, string credentialNamespace, string userId, IReadOnlyList<string> keys, bool includeSystem, CancellationToken ct)
    {
        var request = new ResolveConnectorAssociationRequest { Access = Access(string.IsNullOrEmpty(userId) ? "gateway-runtime" : userId, CredentialCapability.RuntimeResolve), AssociationId = associationId, ConnectorId = connectorId, CredentialNamespace = credentialNamespace, UserId = userId, IncludeSystemScope = includeSystem };
        request.RequiredKeys.AddRange(keys);
        CredentialMapResponse response = await CallAsync(Client, "ResolveConnectorAssociation", client => client.ResolveConnectorAssociationAsync(request, cancellationToken: ct).ResponseAsync).ConfigureAwait(false);
        return response.Entries;
    }

    public Task<IReadOnlyDictionary<string, string>> ResolveRemoteAsync(string? associationId, string connectorId, string? credentialNamespace, string userId, IReadOnlyList<string> keys, bool includeSystem, CancellationToken ct)
    {
        if (associationId is null) return Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>(StringComparer.Ordinal));
        if (credentialNamespace is null) throw new RpcException(new Status(StatusCode.FailedPrecondition, "Credential association has no connector identity."));
        return ResolveAssociationAsync(associationId, connectorId, credentialNamespace, userId, keys, includeSystem, ct);
    }

    public async Task<(IReadOnlyList<string> User, IReadOnlyList<string> System)> GetAssociationPresenceAsync(string associationId, string connectorId, string credentialNamespace, string userId, IReadOnlyList<string> keys, bool includeSystem, CancellationToken ct)
    {
        var request = new ResolveConnectorAssociationRequest { Access = Access(string.IsNullOrEmpty(userId) ? "gateway-runtime" : userId, CredentialCapability.RuntimeResolve), AssociationId = associationId, ConnectorId = connectorId, CredentialNamespace = credentialNamespace, UserId = userId, IncludeSystemScope = includeSystem };
        request.RequiredKeys.AddRange(keys);
        ConnectorAssociationPresenceResponse response = await CallAsync(Client, "GetConnectorAssociationPresence", client => client.GetConnectorAssociationPresenceAsync(request, cancellationToken: ct).ResponseAsync).ConfigureAwait(false);
        return ([.. response.UserKeys], [.. response.SystemKeys]);
    }

    public async Task<IReadOnlyList<ConnectorDraftMetadata>> ListDraftsAsync(string userId, CancellationToken ct)
    {
        ConnectorDraftListResponse response = await CallAsync(Client, "ListConnectorDrafts", client => client.ListConnectorDraftsAsync(new ListConnectorDraftsRequest { Access = DraftAccess(userId, "user") }, cancellationToken: ct).ResponseAsync).ConfigureAwait(false);
        return [.. response.Drafts];
    }

    public async Task<ConnectorDraftMetadata> CreateDraftAsync(string id, string userId, string connectorId, string scope, string candidateJson, string baseUpdatedAt, string effectiveEndpoint, string baseEndpoint, bool useDurable, string credentialNamespace, IReadOnlyList<string> declared, IReadOnlyDictionary<string, string> entries, IReadOnlyList<string> removed, string? sourceAssociationId, CancellationToken ct)
    {
        var request = new CreateConnectorDraftRequest { Access = DraftAccess(userId, scope), DraftId = id, ConnectorId = connectorId, CredentialScope = scope, CandidateJson = candidateJson, BaseUpdatedAt = baseUpdatedAt, EffectiveEndpoint = effectiveEndpoint, BaseEndpoint = baseEndpoint, UseDurableCredentials = useDurable, CredentialNamespace = credentialNamespace, SourceAssociationId = sourceAssociationId ?? string.Empty };
        request.DeclaredKeys.AddRange(declared); request.RemoveKeys.AddRange(removed);
        foreach ((string key, string value) in entries) request.Entries[key] = value;
        return await CallAsync(Client, "CreateConnectorDraft", client => client.CreateConnectorDraftAsync(request, cancellationToken: ct).ResponseAsync).ConfigureAwait(false);
    }

    public async Task<ConnectorDraftMetadata> GetDraftAsync(string id, string userId, string scope, CancellationToken ct)
        => await CallAsync(Client, "GetConnectorDraft", client => client.GetConnectorDraftAsync(new ConnectorDraftAccessRequest { Access = DraftAccess(userId, scope), DraftId = id }, cancellationToken: ct).ResponseAsync).ConfigureAwait(false);

    public async Task<IReadOnlyDictionary<string, string>> ResolveDraftAsync(string id, string userId, string scope, IReadOnlyList<string> keys, bool includeSystem, CancellationToken ct)
    {
        var request = new ResolveConnectorDraftRequest { Access = DraftAccess(userId, scope), DraftId = id, IncludeSystemScope = includeSystem };
        request.RequiredKeys.AddRange(keys);
        CredentialMapResponse response = await CallAsync(Client, "ResolveConnectorDraft", client => client.ResolveConnectorDraftAsync(request, cancellationToken: ct).ResponseAsync).ConfigureAwait(false);
        return response.Entries;
    }

    public async Task<bool> CancelDraftAsync(string id, string userId, string scope, CancellationToken ct)
    {
        DeleteCredentialResponse response = await CallAsync(Client, "CancelConnectorDraft", client => client.CancelConnectorDraftAsync(new ConnectorDraftAccessRequest { Access = DraftAccess(userId, scope), DraftId = id }, cancellationToken: ct).ResponseAsync).ConfigureAwait(false);
        return response.Deleted;
    }

    public async Task DiscardDraftInputAsync(string id, string userId, CancellationToken ct)
        => _ = await CallAsync(Client, "DiscardConnectorDraftInput", client => client.DiscardConnectorDraftInputAsync(new ConnectorDraftAccessRequest { Access = DraftAccess(userId, "user"), DraftId = id }, cancellationToken: ct).ResponseAsync).ConfigureAwait(false);

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
