using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

using Google.Protobuf.Collections;

using Grpc.Core;

using Kautuka.CredentialsHost.Database;
using Kautuka.CredentialsHost.Hosting;
using Kautuka.CredentialsHost.Security;
using Kautuka.Protos;

using Microsoft.Data.Sqlite;

namespace Kautuka.CredentialsHost.Grpc;

[SuppressMessage("Performance", "CA1812", Justification = "Constructed by ASP.NET Core DI via MapGrpcService")]
internal sealed class CredentialsGrpcService(
    CredentialsDatabase db,
    CredentialRepository repository,
    CredentialsVaultService vault,
    CredentialAuditStore audit,
    CredentialClientIdentityState clientIdentityState,
    ILogger<CredentialsGrpcService> logger)
    : CredentialsService.CredentialsServiceBase
{
    private ConnectorDraftRepository Drafts => new(db, vault, TimeProvider.System);
    private ConnectorAssociationRepository Associations => new(db, vault, TimeProvider.System);

    public override Task<StageConnectorAssociationResponse> StageConnectorMutation(StageConnectorMutationRequest request, ServerCallContext context)
    {
        AuthorizeDraft(request.Access, request.CredentialScope);
        ValidateOperationId(request.OperationId); ValidateOperationId(request.CredentialNamespace); ValidateUserId(request.Access!.ActorId, allowEmpty: false);
        if (request.SourceAssociationId.Length != 0) ValidateOperationId(request.SourceAssociationId);
        if (string.IsNullOrWhiteSpace(request.ConnectorId) || request.ConnectorId.Length > 128 || request.Entries.Count > 65 || request.RemoveKeys.Count > 64)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid connector credential mutation."));
        ValidateKeys(request.Entries.Keys); ValidateKeys(request.RemoveKeys);
        if (request.Entries.Any(entry => string.IsNullOrWhiteSpace(entry.Value) || Encoding.UTF8.GetByteCount(entry.Value) > 65536))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid connector credential value."));
        if (request.RemoveKeys.Distinct(StringComparer.Ordinal).Count() != request.RemoveKeys.Count || request.RemoveKeys.Any(request.Entries.ContainsKey))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Conflicting connector credential mutation."));
        string id = Associations.StageMutation(request.OperationId, request.Access.ActorId, request.ConnectorId, request.CredentialNamespace, request.SourceAssociationId, request.CredentialScope, request.Entries, [.. request.RemoveKeys]);
        return Task.FromResult(new StageConnectorAssociationResponse { AssociationId = id });
    }

    public override Task<StageConnectorAssociationResponse> StageConnectorAssociation(StageConnectorAssociationRequest request, ServerCallContext context)
    {
        ValidateOperationId(request.DraftId); ValidateUserId(request.Access?.ActorId ?? "", allowEmpty: false);
        if (request.SourceAssociationId.Length != 0) ValidateOperationId(request.SourceAssociationId);
        ConnectorDraftRecord draft;
        try { draft = Drafts.Get(request.DraftId, request.Access!.ActorId); }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound) { draft = Associations.GetStagedDraft(request.DraftId, request.Access!.ActorId); }
        AuthorizeDraft(request.Access, draft.Scope);
        return Task.FromResult(new StageConnectorAssociationResponse { AssociationId = Associations.Stage(request.DraftId, request.Access!.ActorId, request.SourceAssociationId) });
    }

    public override Task<ConnectorDraftMetadata> GetStagedConnectorDraft(ConnectorDraftAccessRequest request, ServerCallContext context)
    {
        ValidateOperationId(request.DraftId); ValidateUserId(request.Access?.ActorId ?? "", allowEmpty: false);
        CredentialRequestAuthorizer.AuthorizeUserAdministration(request.Access, request.Access!.ActorId);
        return Task.FromResult(ToMetadata(Associations.GetStagedDraft(request.DraftId, request.Access.ActorId)));
    }

    public override Task<CredentialMapResponse> ResolveConnectorAssociation(ResolveConnectorAssociationRequest request, ServerCallContext context)
    {
        ValidateAssociationRequest(request);
        var response = new CredentialMapResponse();
        response.Entries.Add(Associations.Resolve(request.AssociationId, request.ConnectorId, request.CredentialNamespace, request.UserId, request.RequiredKeys, request.IncludeSystemScope || request.UserId.Length == 0));
        return Task.FromResult(response);
    }

    public override Task<ConnectorAssociationPresenceResponse> GetConnectorAssociationPresence(ResolveConnectorAssociationRequest request, ServerCallContext context)
    {
        ValidateAssociationRequest(request);
        (List<string> user, List<string> system) = Associations.GetPresence(request.AssociationId, request.ConnectorId, request.CredentialNamespace, request.UserId, request.RequiredKeys, request.IncludeSystemScope || request.UserId.Length == 0);
        var response = new ConnectorAssociationPresenceResponse(); response.UserKeys.AddRange(user); response.SystemKeys.AddRange(system);
        return Task.FromResult(response);
    }

    private static void ValidateAssociationRequest(ResolveConnectorAssociationRequest request)
    {
        CredentialRequestAuthorizer.AuthorizeResolve(request.Access, request.UserId);
        ValidateOperationId(request.AssociationId); ValidateOperationId(request.CredentialNamespace); ValidateUserId(request.UserId, allowEmpty: true); ValidateKeys(request.RequiredKeys);
        if (string.IsNullOrWhiteSpace(request.ConnectorId) || request.ConnectorId.Length > 128) throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid connector."));
        if (request.RequiredKeys.Count > 64) throw new RpcException(new Status(StatusCode.InvalidArgument, "Too many credential keys."));
    }

    public override Task<ConnectorDraftListResponse> ListConnectorDrafts(ListConnectorDraftsRequest request, ServerCallContext context)
    {
        CredentialRequestAuthorizer.AuthorizeUserAdministration(request.Access, request.Access?.ActorId ?? string.Empty);
        ValidateUserId(request.Access!.ActorId, allowEmpty: false);
        var response = new ConnectorDraftListResponse();
        response.Drafts.AddRange(Drafts.ListPending(request.Access.ActorId).Select(ToMetadata));
        return Task.FromResult(response);
    }

    public override Task<ConnectorDraftMetadata> CreateConnectorDraft(CreateConnectorDraftRequest request, ServerCallContext context)
    {
        AuthorizeDraft(request.Access, request.CredentialScope);
        ValidateOperationId(request.DraftId);
        ValidateOperationId(request.CredentialNamespace);
        if (request.SourceAssociationId.Length != 0) ValidateOperationId(request.SourceAssociationId);
        ValidateUserId(request.Access!.ActorId, allowEmpty: false);
        if (string.IsNullOrWhiteSpace(request.ConnectorId) || request.ConnectorId.Length > 128 || request.CandidateJson.Length is 0 or > 131072 || request.EffectiveEndpoint.Length is 0 or > 8192
            || request.DeclaredKeys.Count > 64 || request.Entries.Count > 64 || request.RemoveKeys.Count > 64)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid connector draft."));
        string[] declared = [.. request.DeclaredKeys];
        string[] removed = [.. request.RemoveKeys];
        ValidateKeys(declared); ValidateKeys(removed);
        if (declared.Distinct(StringComparer.Ordinal).Count() != declared.Length
            || removed.Any(key => !declared.Contains(key, StringComparer.Ordinal))
            || request.Entries.Any(pair => !declared.Contains(pair.Key, StringComparer.Ordinal) || removed.Contains(pair.Key, StringComparer.Ordinal)
                || string.IsNullOrWhiteSpace(pair.Value) || Encoding.UTF8.GetByteCount(pair.Value) > 65536))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid connector draft credential mutation."));
        ConnectorDraftRecord draft = Drafts.Create(request.DraftId, request.Access!.ActorId, request.ConnectorId, request.CredentialScope,
            request.CandidateJson, request.BaseUpdatedAt, request.EffectiveEndpoint, request.BaseEndpoint, request.UseDurableCredentials, request.CredentialNamespace, declared, new Dictionary<string, string>(request.Entries, StringComparer.Ordinal), removed, request.SourceAssociationId);
        return Task.FromResult(ToMetadata(draft));
    }

    public override Task<ConnectorDraftMetadata> GetConnectorDraft(ConnectorDraftAccessRequest request, ServerCallContext context)
    {
        ValidateOperationId(request.DraftId); ValidateUserId(request.Access?.ActorId ?? "", allowEmpty: false);
        ConnectorDraftRecord draft = Drafts.Get(request.DraftId, request.Access!.ActorId);
        CredentialRequestAuthorizer.AuthorizeUserAdministration(request.Access, request.Access!.ActorId);
        return Task.FromResult(ToMetadata(draft));
    }

    public override Task<CredentialMapResponse> ResolveConnectorDraft(ResolveConnectorDraftRequest request, ServerCallContext context)
    {
        ValidateOperationId(request.DraftId); ValidateUserId(request.Access?.ActorId ?? "", allowEmpty: false);
        ConnectorDraftRecord draft = Drafts.Get(request.DraftId, request.Access!.ActorId);
        AuthorizeDraft(request.Access, draft.Scope); ValidateKeys(request.RequiredKeys);
        var response = new CredentialMapResponse();
        response.Entries.Add(Drafts.Resolve(request.DraftId, request.Access!.ActorId, [.. request.RequiredKeys], request.IncludeSystemScope));
        return Task.FromResult(response);
    }

    public override Task<DeleteCredentialResponse> CancelConnectorDraft(ConnectorDraftAccessRequest request, ServerCallContext context)
    {
        ValidateOperationId(request.DraftId); ValidateUserId(request.Access?.ActorId ?? "", allowEmpty: false);
        CredentialRequestAuthorizer.AuthorizeUserAdministration(request.Access, request.Access!.ActorId);
        return Task.FromResult(new DeleteCredentialResponse { Deleted = Drafts.Cancel(request.DraftId, request.Access!.ActorId) });
    }

    public override Task<DeleteCredentialResponse> DiscardConnectorDraftInput(ConnectorDraftAccessRequest request, ServerCallContext context)
    {
        ValidateOperationId(request.DraftId); ValidateUserId(request.Access?.ActorId ?? "", allowEmpty: false);
        CredentialRequestAuthorizer.AuthorizeUserAdministration(request.Access, request.Access!.ActorId);
        _ = Associations.GetStagedDraft(request.DraftId, request.Access.ActorId);
        Drafts.DiscardInput(request.DraftId, request.Access.ActorId);
        return Task.FromResult(new DeleteCredentialResponse { Deleted = true });
    }

    private static void AuthorizeDraft(CredentialAccessContext? access, string scope)
    {
        if (scope == "user") CredentialRequestAuthorizer.AuthorizeUserAdministration(access, access?.ActorId ?? "");
        else if (scope == "system") CredentialRequestAuthorizer.AuthorizeSystemAdministration(access);
        else throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid draft credential scope."));
    }

    private static ConnectorDraftMetadata ToMetadata(ConnectorDraftRecord draft) => new()
    {
        DraftId = draft.Id,
        ConnectorId = draft.ConnectorId,
        CredentialScope = draft.Scope,
        CandidateJson = draft.Candidate,
        BaseUpdatedAt = draft.BaseUpdatedAt,
        ExpiresAtUnixSeconds = draft.ExpiresAt,
        EffectiveEndpoint = draft.EffectiveEndpoint,
        BaseEndpoint = draft.BaseEndpoint,
        UseDurableCredentials = draft.UseDurable,
        CredentialNamespace = draft.CredentialNamespace,
        SourceAssociationId = draft.SourceAssociationId,
    };

    public override async Task<CredentialHealthResponse> GetHealth(
        CredentialHealthRequest request,
        ServerCallContext context)
    {
        await using SqliteConnection connection = await db.OpenConnectionAsync(context.CancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        string? integrity = Convert.ToString(await command.ExecuteScalarAsync(context.CancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        return new CredentialHealthResponse
        {
            State = string.Equals(integrity, "ok", StringComparison.Ordinal)
                ? CredentialHealthState.Ready
                : CredentialHealthState.IntegrityFailed,
            ActiveClientCertificateNotAfterUnixSeconds = clientIdentityState.Current.NotAfter.ToUnixTimeSeconds()
        };
    }

    // ────────────────────────────────────────────────────────────────────────
    // GetResolvedCredentials
    // User-scope wins over system-scope for same key.
    // Missing keys are silently omitted — no error.
    // ────────────────────────────────────────────────────────────────────────

    public override async Task<CredentialMapResponse> GetResolvedCredentials(
        GetResolvedCredentialsRequest request,
        ServerCallContext context)
    {
        string userId = request.UserId;
        string[] keys = [.. request.RequiredKeys];

        await AuthorizeAsync(() => CredentialRequestAuthorizer.AuthorizeResolve(request.Access, userId), request.Access, "resolve", "merged", userId, "batch", context).ConfigureAwait(false);
        ValidateUserId(userId, allowEmpty: true);
        ValidateKeys(keys);

        if (keys.Length == 0)
            return new CredentialMapResponse();

        // Keep requested keys as JSON so SQLite can bind one fixed parameter and expand it safely.
        // Pass keys as a JSON array scalar; SQLite's json_each() expands it server-side.
        // Build the array manually — JsonSerializer.Serialize is unavailable under AOT (reflection disabled).
        string keysJson = BuildJsonStringArray(keys);

        (IReadOnlyList<StoredCredential> userRows, IReadOnlyList<StoredCredential> systemRows) =
            await repository.ResolveAsync(userId, keysJson, context.CancellationToken).ConfigureAwait(false);

        bool includeSystem = request.IncludeSystemScope || string.IsNullOrEmpty(userId);
        bool includeHeld = request.IncludeHeldSecrets;
        var result = new CredentialMapResponse();
        var merged = new Dictionary<string, string>(StringComparer.Ordinal);

        if (includeSystem)
        {
            foreach (StoredCredential row in systemRows)
            {
                if (IncludeResolvedKey(row.Key, includeHeld))
                {
                    merged[row.Key] = Decrypt(row, "system", string.Empty);
                }
            }
        }

        foreach (StoredCredential row in userRows)
        {
            if (IncludeResolvedKey(row.Key, includeHeld))
            {
                merged[row.Key] = Decrypt(row, "user", userId);
            }
        }

        result.Entries.Add(merged);
        await audit.WriteAsync(new CredentialAuditEvent(CorrelationId(context), request.Access!.ActorId, "resolve", "merged", userId, "batch", "success"),
            context.CancellationToken).ConfigureAwait(false);
        return result;
    }

    private static bool IncludeResolvedKey(string key, bool includeHeldSecrets)
        => !key.StartsWith("kautuka.internal.", StringComparison.OrdinalIgnoreCase)
            && (includeHeldSecrets
            || (!key.Contains("REFRESH_TOKEN", StringComparison.OrdinalIgnoreCase)
                && !key.Contains("DPOP", StringComparison.OrdinalIgnoreCase)));

    public override async Task<CredentialMapResponse> GetSystemCredential(
        GetSystemCredentialRequest request,
        ServerCallContext context)
    {
        await AuthorizeAsync(() => CredentialRequestAuthorizer.AuthorizeResolve(request.Access, string.Empty), request.Access, "resolve", "system", string.Empty, request.Key, context).ConfigureAwait(false);
        ValidateKey(request.Key);
        StoredCredential? row = await repository.GetSystemAsync(request.Key, context.CancellationToken).ConfigureAwait(false);
        var response = new CredentialMapResponse();
        if (row.HasValue)
            response.Entries.Add(row.Value.Key, Decrypt(row.Value, "system", string.Empty));
        await audit.WriteAsync(new CredentialAuditEvent(CorrelationId(context), request.Access!.ActorId, "resolve", "system", string.Empty, request.Key, row.HasValue ? "success" : "not_found"),
            context.CancellationToken).ConfigureAwait(false);
        return response;
    }

    // ────────────────────────────────────────────────────────────────────────
    // User credential management
    // ────────────────────────────────────────────────────────────────────────

    public override async Task<CredentialKeyListResponse> ListUserCredentialKeys(
        ListUserCredentialKeysRequest request,
        ServerCallContext context)
    {
        await AuthorizeAsync(() => CredentialRequestAuthorizer.AuthorizeUserAdministration(request.Access, request.UserId), request.Access, "list", "user", request.UserId, "batch", context).ConfigureAwait(false);
        ValidateUserId(request.UserId, allowEmpty: false);
        IReadOnlyList<string> keys = await repository.ListAsync("user", request.UserId, context.CancellationToken).ConfigureAwait(false);

        var response = new CredentialKeyListResponse();
        response.Keys.AddRange(keys);
        await audit.WriteAsync(new CredentialAuditEvent(CorrelationId(context), request.Access!.ActorId, "list", "user", request.UserId, "batch", "success"),
            context.CancellationToken).ConfigureAwait(false);
        return response;
    }

    public override async Task<SetCredentialsResponse> SetUserCredentials(
        SetUserCredentialsRequest request,
        ServerCallContext context)
    {
        string userId = request.UserId;
        await AuthorizeAsync(() => CredentialRequestAuthorizer.AuthorizeUserAdministration(request.Access, userId), request.Access, "set", "user", userId, "batch", context).ConfigureAwait(false);
        ValidateUserId(userId, allowEmpty: false);
        ValidateEntries(request.Entries);
        ValidateOperationId(request.OperationId);
        int upserted = await repository.MutateAsync(new CredentialMutation(
            request.OperationId, request.Access!.ActorId, "user", userId,
            new Dictionary<string, string>(request.Entries, StringComparer.Ordinal), null,
            new CredentialAuditEvent(request.OperationId, request.Access!.ActorId, "set", "user", userId, "batch", "success")),
            context.CancellationToken).ConfigureAwait(false);
        CredentialsGrpcServiceLog.UserCredentialsSet(logger, userId, upserted);
        return new SetCredentialsResponse { UpsertedCount = upserted };
    }

    public override async Task<DeleteCredentialResponse> DeleteUserCredential(
        DeleteUserCredentialRequest request,
        ServerCallContext context)
    {
        await AuthorizeAsync(() => CredentialRequestAuthorizer.AuthorizeUserAdministration(request.Access, request.UserId), request.Access, "delete", "user", request.UserId, request.Key, context).ConfigureAwait(false);
        ValidateUserId(request.UserId, allowEmpty: false);
        ValidateKey(request.Key);
        ValidateOperationId(request.OperationId);
        int rows = await repository.MutateAsync(new CredentialMutation(
            request.OperationId, request.Access!.ActorId, "user", request.UserId, null, request.Key,
            new CredentialAuditEvent(request.OperationId, request.Access!.ActorId, "delete", "user", request.UserId, request.Key, "completed")),
            context.CancellationToken).ConfigureAwait(false);

        return new DeleteCredentialResponse { Deleted = rows > 0 };
    }

    // ────────────────────────────────────────────────────────────────────────
    // System credential management
    // ────────────────────────────────────────────────────────────────────────

    public override async Task<CredentialKeyListResponse> ListSystemCredentialKeys(
        ListSystemCredentialKeysRequest request,
        ServerCallContext context)
    {
        await AuthorizeAsync(() => CredentialRequestAuthorizer.AuthorizeSystemAdministration(request.Access), request.Access, "list", "system", string.Empty, "batch", context).ConfigureAwait(false);
        IReadOnlyList<string> keys = await repository.ListAsync("system", string.Empty, context.CancellationToken).ConfigureAwait(false);

        var response = new CredentialKeyListResponse();
        response.Keys.AddRange(keys);
        await audit.WriteAsync(new CredentialAuditEvent(CorrelationId(context), request.Access!.ActorId, "list", "system", string.Empty, "batch", "success"),
            context.CancellationToken).ConfigureAwait(false);
        return response;
    }

    public override async Task<SetCredentialsResponse> SetSystemCredentials(
        SetSystemCredentialsRequest request,
        ServerCallContext context)
    {
        await AuthorizeAsync(() => CredentialRequestAuthorizer.AuthorizeSystemAdministration(request.Access), request.Access, "set", "system", string.Empty, "batch", context).ConfigureAwait(false);
        ValidateEntries(request.Entries);
        ValidateOperationId(request.OperationId);
        int upserted = await repository.MutateAsync(new CredentialMutation(
            request.OperationId, request.Access!.ActorId, "system", string.Empty,
            new Dictionary<string, string>(request.Entries, StringComparer.Ordinal), null,
            new CredentialAuditEvent(request.OperationId, request.Access!.ActorId, "set", "system", string.Empty, "batch", "success")),
            context.CancellationToken).ConfigureAwait(false);
        CredentialsGrpcServiceLog.SystemCredentialsSet(logger, upserted);
        return new SetCredentialsResponse { UpsertedCount = upserted };
    }

    public override async Task<DeleteCredentialResponse> DeleteSystemCredential(
        DeleteSystemCredentialRequest request,
        ServerCallContext context)
    {
        await AuthorizeAsync(() => CredentialRequestAuthorizer.AuthorizeSystemAdministration(request.Access), request.Access, "delete", "system", string.Empty, request.Key, context).ConfigureAwait(false);
        ValidateKey(request.Key);
        ValidateOperationId(request.OperationId);
        int rows = await repository.MutateAsync(new CredentialMutation(
            request.OperationId, request.Access!.ActorId, "system", string.Empty, null, request.Key,
            new CredentialAuditEvent(request.OperationId, request.Access!.ActorId, "delete", "system", string.Empty, request.Key, "completed")),
            context.CancellationToken).ConfigureAwait(false);

        return new DeleteCredentialResponse { Deleted = rows > 0 };
    }

    /// <summary>
    /// Serialises a string array to a JSON array without reflection or <see cref="System.Text.Json.JsonSerializer"/>,
    /// which is unavailable under Native AOT when reflection-based serialisation is disabled.
    /// </summary>
    private static string BuildJsonStringArray(string[] values)
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('"');
            foreach (char c in values[i])
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                            sb.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}");
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
        sb.Append(']');
        return sb.ToString();
    }

    private string Decrypt(StoredCredential row, string scope, string subject)
    {
        try
        {
            return vault.Decrypt(row.EncryptedValue, new CredentialEnvelopeIdentity(scope, subject, row.Key));
        }
        catch (CredentialIntegrityException exception)
        {
            CredentialsGrpcServiceLog.CredentialIntegrityFailure(logger, row.Key);
            throw new RpcException(new Status(StatusCode.DataLoss, "Stored credential failed integrity verification."), exception.Message);
        }
    }

    private static void ValidateEntries(MapField<string, string> entries)
    {
        if (entries.Count is 0 or > 64)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "A mutation requires between 1 and 64 credential entries."));
        }

        foreach ((string key, string value) in entries)
        {
            ValidateKey(key);
            if (string.IsNullOrEmpty(value) || Encoding.UTF8.GetByteCount(value) > 65536)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Credential values must contain between 1 and 65536 UTF-8 bytes."));
            }
        }
    }

    private static void ValidateKeys(IEnumerable<string> keys)
    {
        foreach (string key in keys)
        {
            ValidateKey(key);
        }
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 256)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Credential keys must contain 1 to 256 non-whitespace characters."));
        }
    }

    private static void ValidateUserId(string userId, bool allowEmpty)
    {
        if ((!allowEmpty && string.IsNullOrWhiteSpace(userId)) || userId.Length > 256)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Credential user id is invalid."));
        }
    }

    private static void ValidateOperationId(string operationId)
    {
        if (!Guid.TryParseExact(operationId, "N", out _))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "A canonical operation id is required."));
    }

    private static string CorrelationId(ServerCallContext context)
    {
        string? supplied = context.RequestHeaders.FirstOrDefault(header =>
            string.Equals(header.Key, "x-correlation-id", StringComparison.OrdinalIgnoreCase))?.Value;
        return string.IsNullOrWhiteSpace(supplied) ? Guid.NewGuid().ToString("N") : supplied;
    }

    private async Task AuthorizeAsync(Action authorize, CredentialAccessContext? access, string action, string scope, string subject, string keyId, ServerCallContext context)
    {
        try { authorize(); }
        catch (RpcException exception) when (exception.StatusCode is StatusCode.Unauthenticated or StatusCode.PermissionDenied)
        {
            await audit.WriteAsync(new CredentialAuditEvent(CorrelationId(context), access?.ActorId ?? "anonymous", action, scope, subject, keyId, "denied"),
                context.CancellationToken).ConfigureAwait(false);
            throw;
        }
    }

}

internal static partial class CredentialsGrpcServiceLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "User credentials upserted for user={UserId} count={Count}")]
    public static partial void UserCredentialsSet(ILogger logger, string userId, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "System credentials upserted count={Count}")]
    public static partial void SystemCredentialsSet(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Credential integrity verification failed for key={Key}")]
    public static partial void CredentialIntegrityFailure(ILogger logger, string key);
}
