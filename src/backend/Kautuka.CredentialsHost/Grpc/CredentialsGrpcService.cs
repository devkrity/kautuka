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
        await audit.WriteAsync(new CredentialAuditEvent(CorrelationId(context), request.Access.ActorId, "resolve", "merged", userId, "batch", "success"),
            context.CancellationToken).ConfigureAwait(false);
        return result;
    }

    private static bool IncludeResolvedKey(string key, bool includeHeldSecrets)
        => includeHeldSecrets
            || (!key.Contains("REFRESH_TOKEN", StringComparison.OrdinalIgnoreCase)
                && !key.Contains("DPOP", StringComparison.OrdinalIgnoreCase));

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
        await audit.WriteAsync(new CredentialAuditEvent(CorrelationId(context), request.Access.ActorId, "resolve", "system", string.Empty, request.Key, row.HasValue ? "success" : "not_found"),
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
        await audit.WriteAsync(new CredentialAuditEvent(CorrelationId(context), request.Access.ActorId, "list", "user", request.UserId, "batch", "success"),
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
            request.OperationId, request.Access.ActorId, "user", userId,
            new Dictionary<string, string>(request.Entries, StringComparer.Ordinal), null,
            new CredentialAuditEvent(request.OperationId, request.Access.ActorId, "set", "user", userId, "batch", "success")),
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
            request.OperationId, request.Access.ActorId, "user", request.UserId, null, request.Key,
            new CredentialAuditEvent(request.OperationId, request.Access.ActorId, "delete", "user", request.UserId, request.Key, "completed")),
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
        await audit.WriteAsync(new CredentialAuditEvent(CorrelationId(context), request.Access.ActorId, "list", "system", string.Empty, "batch", "success"),
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
            request.OperationId, request.Access.ActorId, "system", string.Empty,
            new Dictionary<string, string>(request.Entries, StringComparer.Ordinal), null,
            new CredentialAuditEvent(request.OperationId, request.Access.ActorId, "set", "system", string.Empty, "batch", "success")),
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
            request.OperationId, request.Access.ActorId, "system", string.Empty, null, request.Key,
            new CredentialAuditEvent(request.OperationId, request.Access.ActorId, "delete", "system", string.Empty, request.Key, "completed")),
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
