using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Google.Protobuf;

using Grpc.Core;

using Kautuka.ChannelHost.Protos;
using Kautuka.Protos;

using Microsoft.Data.Sqlite;

namespace Kautuka.ChannelHost.Grpc;

internal sealed partial class ChannelRequestAuthorizer
{
    public const string Read = "channels.read";
    public const string Write = "channels.write";
    public const string Runtime = "channels.runtime";
    public const string Ingress = "channels.ingress";
    public const string Admin = "channels.admin";

    private readonly ChannelHostDatabase database;
    private readonly ChannelDelegationVerificationKeys delegationKeys;
    private readonly ChannelWorkloadPolicyRegistry workloadPolicies;
    private readonly TimeProvider timeProvider;
    private readonly ChannelResourceGovernor resourceGovernor;
    private readonly ILogger<ChannelRequestAuthorizer> logger;
    private long deniedCount;

    public long DeniedCount => Interlocked.Read(ref deniedCount);

    public ChannelRequestAuthorizer(
        ChannelHostDatabase database,
        ChannelDelegationVerificationKeys delegationKeys,
        ChannelWorkloadPolicyRegistry workloadPolicies,
        TimeProvider timeProvider,
        ChannelResourceGovernor resourceGovernor,
        ILogger<ChannelRequestAuthorizer> logger)
    {
        this.database = database;
        this.delegationKeys = delegationKeys;
        this.workloadPolicies = workloadPolicies;
        this.timeProvider = timeProvider;
        this.resourceGovernor = resourceGovernor;
        this.logger = logger;
        EnsureSchema();
    }

    public ChannelAuthorizationResult Authorize(
        IMessage request,
        ScopedRequestContext? context,
        string method,
        string operation,
        ServerCallContext? callContext = null)
    {
        ChannelWorkloadPolicy policy;
        try
        {
            policy = ResolveWorkloadPolicy(callContext, method, operation);
            EnforceWorkloadResourcePolicy(policy, context, method);
        }
        catch (RpcException ex)
        {
            _ = Interlocked.Increment(ref deniedCount);
            AuditDenied(logger, method, PrivacySafeReference(context?.ActorUserId), PrivacySafeReference(context?.TenantId),
                PrivacySafeReference(context?.RequestId), SafeReason(ex.Status.Detail));
            RecordAudit(context, method, "denied", ex.Status.Detail, string.Empty);
            throw;
        }

        X509Certificate2? delegationCertificate = policy.DelegationVerificationKeys.FirstOrDefault(certificate =>
            string.Equals(NormalizeThumbprint(certificate.Thumbprint), NormalizeThumbprint(context?.DelegationKeyId), StringComparison.Ordinal));
        ChannelDelegationValidationResult validation = delegationCertificate is null
            ? ChannelDelegationValidationResult.Deny("delegation_key_invalid")
            : ChannelDelegationEnvelope.Validate(request, context, operation, delegationCertificate, timeProvider.GetUtcNow(),
                policy.DelegationIssuer, policy.DelegationAudience);
        if (!validation.Succeeded)
        {
            _ = Interlocked.Increment(ref deniedCount);
            AuditDenied(logger, method, PrivacySafeReference(context?.ActorUserId), PrivacySafeReference(context?.TenantId),
                PrivacySafeReference(context?.RequestId), SafeReason(validation.DenialReason));
            RecordAudit(context, method, "denied", validation.DenialReason, string.Empty);
            throw new RpcException(new Status(StatusCode.Unauthenticated, validation.DenialReason));
        }

        try
        {
            resourceGovernor.Enforce(context!, method, request, policy.WorkloadId);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.ResourceExhausted)
        {
            _ = Interlocked.Increment(ref deniedCount);
            AuditDenied(logger, method, PrivacySafeReference(context!.ActorUserId), PrivacySafeReference(context.TenantId), PrivacySafeReference(context.RequestId), SafeReason(ex.Status.Detail));
            RecordAudit(context, method, "denied", ex.Status.Detail, validation.Digest);
            throw;
        }

        string? idempotencyKey = GetIdempotencyKey(request);
        try
        {
            PersistReplay(context!, method, validation.Digest);
            ScopedRequestContext authorizedContext = context!;
            if (logger.IsEnabled(LogLevel.Information))
            {
#pragma warning disable CA1873 // Guarded explicitly; references must be hashed before entering structured logs.
                string actorReference = PrivacySafeReference(authorizedContext.ActorUserId);
                string tenantReference = PrivacySafeReference(authorizedContext.TenantId);
                string requestReference = PrivacySafeReference(authorizedContext.RequestId);
                string tokenReference = PrivacySafeReference(authorizedContext.DelegationTokenId);
                AuditAllowed(logger, method, actorReference, tenantReference, requestReference,
                    tokenReference, validation.Digest, authorizedContext.DelegationKeyId);
#pragma warning restore CA1873
            }
            RecordAudit(authorizedContext, method, "allowed", "policy_allowed", validation.Digest);
            return new ChannelAuthorizationResult(validation.Digest, idempotencyKey, false, false,
                policy.WorkloadId, policy.WorkloadIdentityUri.AbsoluteUri,
                authorizedContext.ActorUserId, authorizedContext.TenantId, string.Empty);
        }
        catch (SecurityBoundaryException ex)
        {
            _ = Interlocked.Increment(ref deniedCount);
            AuditDenied(logger, method, PrivacySafeReference(context!.ActorUserId), PrivacySafeReference(context.TenantId), PrivacySafeReference(context.RequestId), SafeReason(ex.Message));
            RecordAudit(context, method, "denied", ex.Message, validation.Digest);
            throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
        }

    }

    public void RequireOwner(
        ChannelAuthorizationResult authorization,
        ScopedRequestContext? context,
        string method,
        string ownerUserId)
    {
        RequireResourceScope(authorization, context, method,
            context is not null && string.Equals(context.ActorUserId, ownerUserId, StringComparison.Ordinal),
            StatusCode.PermissionDenied, "channel_resource_outside_actor_scope");
    }

    internal static void RequireOwner(ScopedRequestContext? context, string ownerUserId)
    {
        if (context is not null && string.Equals(context.ActorUserId, ownerUserId, StringComparison.Ordinal)) return;
        throw new RpcException(new Status(StatusCode.PermissionDenied, "channel_resource_outside_actor_scope"));
    }

    public void RequireResourceScope(
        ChannelAuthorizationResult authorization,
        ScopedRequestContext? context,
        string method,
        bool admitted,
        StatusCode statusCode,
        string reason)
    {
        if (admitted) return;
        string safeReason = SafeReason(reason);
        _ = Interlocked.Increment(ref deniedCount);
        AuditDenied(logger, method, PrivacySafeReference(context?.ActorUserId), PrivacySafeReference(context?.TenantId),
            PrivacySafeReference(context?.RequestId), safeReason);
        RecordAudit(context, method, "denied", safeReason, authorization.RequestDigest);
        throw new RpcException(new Status(statusCode, safeReason));
    }

    public bool TryGetCachedResponse(string method, ChannelAuthorizationResult authorization, out byte[] response)
    {
        response = [];
        if (string.IsNullOrWhiteSpace(authorization.IdempotencyKey)) return false;
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT response_payload FROM channel_security_operations_v2 WHERE workload_id=$workload AND tenant_id=$tenant AND actor_user_id=$actor AND method_name=$method AND idempotency_key=$key AND request_digest=$digest AND state='COMPLETED'";
        AddOperationScope(command, method, authorization);
        object? result = command.ExecuteScalar();
        if (result is not byte[] bytes) return false;
        response = bytes;
        return true;
    }

    public void StoreResponse(string method, ChannelAuthorizationResult authorization, IMessage response)
    {
        if (string.IsNullOrWhiteSpace(authorization.IdempotencyKey)) return;
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE channel_security_operations_v2
            SET state='COMPLETED',response_payload=$payload,updated_at_utc=$created,lease_expires_at_utc=NULL
            WHERE workload_id=$workload AND tenant_id=$tenant AND actor_user_id=$actor AND method_name=$method
              AND idempotency_key=$key AND request_digest=$digest AND lease_id=$lease
              AND state IN ('RESERVED','EFFECT_STARTED')
            """;
        AddOperationScope(command, method, authorization);
        command.Parameters.AddWithValue("$lease", authorization.LeaseId);
        command.Parameters.AddWithValue("$payload", response.ToByteArray());
        command.Parameters.AddWithValue("$created", timeProvider.GetUtcNow().ToString("O"));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("idempotent_operation_lease_lost");
    }

    public ChannelAuthorizationResult BeginOperation(string method, ChannelAuthorizationResult authorization)
    {
        if (string.IsNullOrWhiteSpace(authorization.IdempotencyKey)) return authorization;
        using SqliteConnection connection = database.OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT request_digest,state,lease_expires_at_utc FROM channel_security_operations_v2 WHERE workload_id=$workload AND tenant_id=$tenant AND actor_user_id=$actor AND method_name=$method AND idempotency_key=$key";
        AddOperationScope(read, method, authorization);
        using SqliteDataReader reader = read.ExecuteReader();
        string? digest = null;
        string? state = null;
        string? expiry = null;
        if (reader.Read())
        {
            digest = reader.GetString(0);
            state = reader.GetString(1);
            expiry = reader.IsDBNull(2) ? null : reader.GetString(2);
        }
        reader.Close();
        if (digest is not null && !string.Equals(digest, authorization.RequestDigest, StringComparison.Ordinal))
        {
            _ = Interlocked.Increment(ref deniedCount);
            InsertAudit(connection, transaction, new ScopedRequestContext
            {
                ActorUserId = authorization.ActorUserId,
                TenantId = authorization.TenantId,
            }, method, "denied", "idempotency_key_digest_conflict", authorization.RequestDigest);
            transaction.Commit();
            throw new RpcException(new Status(StatusCode.PermissionDenied, "idempotency_key_digest_conflict"));
        }
        if (string.Equals(state, "COMPLETED", StringComparison.Ordinal))
        {
            transaction.Commit();
            return authorization with { IsIdempotentRetry = true };
        }
        if (string.Equals(state, "EFFECT_STARTED", StringComparison.Ordinal))
        {
            transaction.Commit();
            return authorization with { IsIdempotentRetry = true, RequiresReconciliation = true };
        }
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (string.Equals(state, "RESERVED", StringComparison.Ordinal)
            && DateTimeOffset.TryParse(expiry, out DateTimeOffset activeUntil) && activeUntil > now)
        {
            transaction.Commit();
            return authorization with { IsIdempotentRetry = true };
        }
        string leaseId = Guid.NewGuid().ToString("N");
        using SqliteCommand reserve = connection.CreateCommand();
        reserve.Transaction = transaction;
        reserve.CommandText = """
            INSERT INTO channel_security_operations_v2(workload_id,tenant_id,actor_user_id,method_name,idempotency_key,request_digest,state,lease_id,lease_expires_at_utc,created_at_utc,updated_at_utc)
            VALUES($workload,$tenant,$actor,$method,$key,$digest,'RESERVED',$lease,$expires,$now,$now)
            ON CONFLICT(workload_id,tenant_id,actor_user_id,method_name,idempotency_key) DO UPDATE SET
              state='RESERVED',lease_id=excluded.lease_id,lease_expires_at_utc=excluded.lease_expires_at_utc,updated_at_utc=excluded.updated_at_utc
            """;
        AddOperationScope(reserve, method, authorization);
        reserve.Parameters.AddWithValue("$lease", leaseId);
        reserve.Parameters.AddWithValue("$expires", now.AddSeconds(30).ToString("O"));
        reserve.Parameters.AddWithValue("$now", now.ToString("O"));
        _ = reserve.ExecuteNonQuery();
        transaction.Commit();
        return authorization with { LeaseId = leaseId };
    }

    public void MarkEffectStarted(string method, ChannelAuthorizationResult authorization)
    {
        if (string.IsNullOrWhiteSpace(authorization.IdempotencyKey)) return;
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE channel_security_operations_v2 SET state='EFFECT_STARTED',updated_at_utc=$now WHERE workload_id=$workload AND tenant_id=$tenant AND actor_user_id=$actor AND method_name=$method AND idempotency_key=$key AND request_digest=$digest AND lease_id=$lease AND state='RESERVED'";
        AddOperationScope(command, method, authorization);
        command.Parameters.AddWithValue("$lease", authorization.LeaseId);
        command.Parameters.AddWithValue("$now", timeProvider.GetUtcNow().ToString("O"));
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("idempotent_operation_lease_lost");
    }

    private static void AddOperationScope(SqliteCommand command, string method, ChannelAuthorizationResult authorization)
    {
        command.Parameters.AddWithValue("$workload", authorization.WorkloadId);
        command.Parameters.AddWithValue("$tenant", authorization.TenantId);
        command.Parameters.AddWithValue("$actor", authorization.ActorUserId);
        command.Parameters.AddWithValue("$method", method);
        command.Parameters.AddWithValue("$key", authorization.IdempotencyKey ?? string.Empty);
        command.Parameters.AddWithValue("$digest", authorization.RequestDigest);
    }

    private void PersistReplay(ScopedRequestContext context, string method, string digest)
    {
        using SqliteConnection connection = database.OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using (SqliteCommand purge = connection.CreateCommand())
        {
            purge.Transaction = transaction;
            purge.CommandText = "DELETE FROM channel_security_replay WHERE expires_at_utc < $cutoff";
            purge.Parameters.AddWithValue("$cutoff", timeProvider.GetUtcNow().AddMinutes(-1).ToString("O"));
            _ = purge.ExecuteNonQuery();
        }

        try
        {
            using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO channel_security_replay(token_id,method_name,actor_user_id,tenant_id,request_digest,expires_at_utc)
                VALUES($token,$method,$actor,$tenant,$digest,$expires)
                """;
            insert.Parameters.AddWithValue("$token", context.DelegationTokenId);
            insert.Parameters.AddWithValue("$method", method);
            insert.Parameters.AddWithValue("$actor", context.ActorUserId);
            insert.Parameters.AddWithValue("$tenant", context.TenantId);
            insert.Parameters.AddWithValue("$digest", digest);
            insert.Parameters.AddWithValue("$expires", context.DelegationExpiresAtUtc);
            _ = insert.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new SecurityBoundaryException("delegation_replay_detected");
        }

        transaction.Commit();
    }

    private void EnsureSchema()
    {
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS channel_security_replay(
                token_id TEXT PRIMARY KEY,
                method_name TEXT NOT NULL,
                actor_user_id TEXT NOT NULL,
                tenant_id TEXT NOT NULL,
                request_digest TEXT NOT NULL,
                expires_at_utc TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS channel_security_idempotency(
                method_name TEXT NOT NULL,
                idempotency_key TEXT NOT NULL,
                request_digest TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                PRIMARY KEY(method_name,idempotency_key));
            CREATE TABLE IF NOT EXISTS channel_security_outcomes(
                method_name TEXT NOT NULL,
                idempotency_key TEXT NOT NULL,
                request_digest TEXT NOT NULL,
                response_payload BLOB NOT NULL,
                created_at_utc TEXT NOT NULL,
                PRIMARY KEY(method_name,idempotency_key));
            CREATE TABLE IF NOT EXISTS channel_security_authorization_audit(
                audit_id INTEGER PRIMARY KEY AUTOINCREMENT,
                occurred_at_utc TEXT NOT NULL, method_name TEXT NOT NULL,
                actor_user_id TEXT NOT NULL, tenant_id TEXT NOT NULL, request_id TEXT NOT NULL,
                token_id TEXT NOT NULL, key_id TEXT NOT NULL, request_digest TEXT NOT NULL,
                decision TEXT NOT NULL, reason_code TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS channel_security_operations_v2(
                workload_id TEXT NOT NULL, tenant_id TEXT NOT NULL, actor_user_id TEXT NOT NULL,
                method_name TEXT NOT NULL,idempotency_key TEXT NOT NULL,request_digest TEXT NOT NULL,
                state TEXT NOT NULL,lease_id TEXT NOT NULL,lease_expires_at_utc TEXT NULL,response_payload BLOB NULL,
                created_at_utc TEXT NOT NULL,updated_at_utc TEXT NOT NULL,
                PRIMARY KEY(workload_id,tenant_id,actor_user_id,method_name,idempotency_key));
            """;
        _ = command.ExecuteNonQuery();
    }

    private void RecordAudit(ScopedRequestContext? context, string method, string decision, string reason, string digest)
    {
        using SqliteConnection connection = database.OpenConnection();
        InsertAudit(connection, null, context, method, decision, reason, digest);
    }

    private void InsertAudit(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ScopedRequestContext? context,
        string method,
        string decision,
        string reason,
        string digest)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO channel_security_authorization_audit(
                occurred_at_utc,method_name,actor_user_id,tenant_id,request_id,token_id,key_id,request_digest,decision,reason_code)
            VALUES($now,$method,$actor,$tenant,$request,$token,$key,$digest,$decision,$reason)
            """;
        command.Parameters.AddWithValue("$now", timeProvider.GetUtcNow().ToString("O"));
        command.Parameters.AddWithValue("$method", method);
        command.Parameters.AddWithValue("$actor", PrivacySafeReference(context?.ActorUserId));
        command.Parameters.AddWithValue("$tenant", PrivacySafeReference(context?.TenantId));
        command.Parameters.AddWithValue("$request", PrivacySafeReference(context?.RequestId));
        command.Parameters.AddWithValue("$token", PrivacySafeReference(context?.DelegationTokenId));
        command.Parameters.AddWithValue("$key", context?.DelegationKeyId ?? string.Empty);
        command.Parameters.AddWithValue("$digest", digest);
        command.Parameters.AddWithValue("$decision", decision);
        command.Parameters.AddWithValue("$reason", reason);
        _ = command.ExecuteNonQuery();
    }

    private static string PrivacySafeReference(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];

    private static string SafeReason(string? value)
        => string.IsNullOrWhiteSpace(value) || value.Length > 96
            || value.Any(static character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '-'))
            ? "security_boundary_denied"
            : value;

    private static string? GetIdempotencyKey(IMessage request) => request switch
    {
        ReadInboundMediaRequest value => value.IdempotencyKey,
        SendOutboundMessageRequest value => value.IdempotencyKey,
        UploadOutboundAssetRequest value => value.Metadata?.IdempotencyKey,
        UpsertAttachmentRequest value => value.IdempotencyKey,
        SyncOwnerTargetsRequest value => value.IdempotencyKey,
        ProvisionAttachmentRequest value => value.IdempotencyKey,
        DeactivateAttachmentRequest value => value.IdempotencyKey,
        TransitionAttachmentLifecycleRequest value => value.OperationId,
        SetRuntimeCredentialsRequest value => value.IdempotencyKey,
        AcknowledgeIngressRequest value => value.IdempotencyKey,
        _ => null,
    };

    private static string NormalizeThumbprint(string? value) =>
        (value ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    internal static void EnforceWorkloadResourcePolicy(ChannelWorkloadPolicy policy, ScopedRequestContext? context, string method)
    {
        if (!policy.AllowCredentialOperations && method == "SetRuntimeCredentials")
            throw new RpcException(new Status(StatusCode.PermissionDenied, "external_workload_credentials_forbidden"));
        if (!policy.AllowAdministrativeOperations && IsAdministrativeMethod(method))
            throw new RpcException(new Status(StatusCode.PermissionDenied, "external_workload_administration_forbidden"));
        if (!policy.AllowCanonicalIngress && method is "StreamIngress" or "AcknowledgeIngress")
            throw new RpcException(new Status(StatusCode.PermissionDenied, "external_workload_canonical_ingress_forbidden"));
        if (IsOwnerScopedMethod(method)
            && !policy.OwnerScopeWildcard
            && (context is null || !policy.AllowedOwnerUserIds.Contains(context.ActorUserId)))
            throw new RpcException(new Status(StatusCode.PermissionDenied, "external_workload_owner_scope_denied"));
    }

    private ChannelWorkloadPolicy ResolveWorkloadPolicy(ServerCallContext? callContext, string method, string operation)
    {
        if (callContext is null) return workloadPolicies.DefaultPolicy;
        X509Certificate2? clientCertificate = callContext.GetHttpContext().Connection.ClientCertificate;
        if (clientCertificate is null)
            throw new RpcException(new Status(StatusCode.Unauthenticated, "workload_identity_missing"));
        try
        {
            return workloadPolicies.RequireAuthorized(clientCertificate, method, operation);
        }
        catch (UnauthorizedAccessException)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, "workload_policy_denied"));
        }
    }

    private static bool IsOwnerScopedMethod(string method) => method is not
        "ListProviders" and not "StreamIngress" and not "AcknowledgeIngress";

    private static bool IsAdministrativeMethod(string method) => method is
        "UpsertAttachment" or "SyncOwnerTargets" or "ProvisionAttachment" or
        "DeactivateAttachment" or "TransitionAttachmentLifecycle";

    private sealed class SecurityBoundaryException : Exception
    {
        public SecurityBoundaryException() { }
        public SecurityBoundaryException(string message) : base(message) { }
        public SecurityBoundaryException(string message, Exception innerException) : base(message, innerException) { }
    }

    [LoggerMessage(EventId = 180, Level = LogLevel.Information,
        Message = "Channel authorization allowed. method={Method} actor={ActorUserId} tenant={TenantId} request={RequestId} token={TokenId} digest={Digest} key={KeyId}")]
    private static partial void AuditAllowed(ILogger logger, string method, string actorUserId, string tenantId, string requestId, string tokenId, string digest, string keyId);

    [LoggerMessage(EventId = 181, Level = LogLevel.Warning,
        Message = "Channel authorization denied. method={Method} actor={ActorUserId} tenant={TenantId} request={RequestId} reason={Reason}")]
    private static partial void AuditDenied(ILogger logger, string method, string actorUserId, string tenantId, string requestId, string reason);
}

internal sealed record ChannelDelegationVerificationKeys(IReadOnlyList<X509Certificate2> Certificates);
internal readonly record struct ChannelAuthorizationResult(
    string RequestDigest,
    string? IdempotencyKey,
    bool IsIdempotentRetry,
    bool RequiresReconciliation,
    string WorkloadId,
    string WorkloadIdentity,
    string ActorUserId,
    string TenantId,
    string LeaseId);
