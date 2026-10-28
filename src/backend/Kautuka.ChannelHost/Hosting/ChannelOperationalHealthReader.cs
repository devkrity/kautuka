using Kautuka.ChannelHost.Grpc;
using Kautuka.Protos;

using Microsoft.Data.Sqlite;

namespace Kautuka.ChannelHost.Hosting;

internal sealed class ChannelOperationalHealthReader
{
    private readonly ChannelHostDatabase database;
    private readonly ChannelDelegationVerificationKeys delegationKeys;
    private readonly ChannelWorkloadPolicyRegistry workloadPolicies;
    private readonly ChannelRequestAuthorizer authorizer;
    private readonly ChannelResourceGovernor resourceGovernor;
    private readonly ChannelSecurityRuntimeHealth securityHealth;

    public ChannelOperationalHealthReader(
        ChannelHostDatabase database,
        ChannelDelegationVerificationKeys delegationKeys,
        ChannelWorkloadPolicyRegistry workloadPolicies,
        ChannelRequestAuthorizer authorizer,
        ChannelResourceGovernor resourceGovernor)
        : this(database, delegationKeys, workloadPolicies, authorizer, resourceGovernor, ChannelSecurityRuntimeHealth.Unspecified) { }

    public ChannelOperationalHealthReader(
        ChannelHostDatabase database,
        ChannelDelegationVerificationKeys delegationKeys,
        ChannelWorkloadPolicyRegistry workloadPolicies,
        ChannelRequestAuthorizer authorizer,
        ChannelResourceGovernor resourceGovernor,
        ChannelSecurityRuntimeHealth securityHealth)
    {
        this.database = database;
        this.delegationKeys = delegationKeys;
        this.workloadPolicies = workloadPolicies;
        this.authorizer = authorizer;
        this.resourceGovernor = resourceGovernor;
        this.securityHealth = securityHealth;
    }

    public ChannelGatewayOperationalHealth Read() => Read("unattributed");

    public ChannelGatewayOperationalHealth Read(ChannelAuthorizationResult authorization)
    {
        ChannelGatewayOperationalHealth response = Read(authorization.WorkloadIdentity);
        response.AuthenticatedWorkloadId = authorization.WorkloadId;
        return response;
    }

    public ChannelGatewayOperationalHealth Read(string authenticatedWorkloadIdentity)
    {
        using SqliteConnection connection = database.OpenConnection();
        var response = new ChannelGatewayOperationalHealth
        {
            SchemaVersion = ReadSchemaVersion(connection),
            ContractVersion = "kautuka-channelhost/v1",
            AuthenticatedWorkloadIdentity = authenticatedWorkloadIdentity,
            ActiveRegistrations = TableExists(connection, "runtime_channel_attachments") ? ReadCount(connection, "ACTIVE") : 0,
            SuspendedRegistrations = TableExists(connection, "runtime_channel_attachments") ? ReadCount(connection, "SUSPENDED") : 0,
            ReconciliationRequiredRegistrations = TableExists(connection, "runtime_channel_attachments")
                ? ReadCount(connection, "RECOVERY_REQUIRED") + ReadCount(connection, "ERROR") : 0,
            IngressDepth = TableExists(connection, "channel_ingress_outbox") ? ReadIngressDepth(connection) : 0,
            OldestIngressAtUtc = TableExists(connection, "channel_ingress_outbox") ? ReadOldestIngress(connection) : string.Empty,
            AuthorizationDenials = authorizer.DeniedCount + workloadPolicies.TransportDenialCount,
            RateLimitDenials = resourceGovernor.DeniedCount,
            StateIntegrityVerified = true,
            StateStorageBytes = File.Exists(database.DatabasePath) ? new FileInfo(database.DatabasePath).Length : 0,
            ClientCertificateValid = securityHealth.ClientCertificateNotAfterUtc > DateTimeOffset.UtcNow,
            DelegationCertificateValid = securityHealth.DelegationCertificateNotAfterUtc > DateTimeOffset.UtcNow,
            RotationState = securityHealth.RotationState,
            RotationOverlapExpiresAtUtc = securityHealth.RotationOverlapExpiresAtUtc?.ToString("O") ?? string.Empty,
        };
        if (TableExists(connection, "channel_ingress_outbox"))
        {
            response.OldestIngressAckAgeSeconds = ReadOldestIngressAgeSeconds(connection);
        }
        if (TableExists(connection, "channel_security_operations_v2"))
        {
            response.PendingOperations = ReadOperationCount(connection, "RESERVED");
            response.RetryOperations = ReadExpiredReservationCount(connection);
            response.ReconciliationOperations = ReadOperationCount(connection, "EFFECT_STARTED");
        }
        if (TableExists(connection, "runtime_channel_streams"))
        {
            response.ActiveStreams = ReadStreamCount(connection, isFinal: false);
            response.CompletedStreams = ReadStreamCount(connection, isFinal: true);
        }
        if (TableExists(connection, "channel_registration_lifecycle_audit"))
            response.OperatorActionFailures = ReadLifecycleFailureCount(connection);
        if (TableExists(connection, "channel_security_rate_windows"))
            response.ActiveRateLimitDimensions.AddRange(ReadRateLimitDimensions(connection));
        if (TableExists(connection, "runtime_channel_attachments"))
            response.Registrations.AddRange(ReadRegistrationHealth(connection));
        response.ActiveDelegationKeyIds.AddRange(delegationKeys.Certificates.Select(static certificate => certificate.Thumbprint));
        response.RevokedIdentityKeyIds.AddRange(securityHealth.RevokedIdentityKeyIds);
        return response;
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name";
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static int ReadCount(SqliteConnection connection, string status)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM runtime_channel_attachments WHERE status=$status";
        command.Parameters.AddWithValue("$status", status);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static int ReadSchemaVersion(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static long ReadIngressDepth(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM channel_ingress_outbox";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ReadOldestIngress(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MIN(created_at_utc),'') FROM channel_ingress_outbox";
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static long ReadOldestIngressAgeSeconds(SqliteConnection connection)
    {
        string oldest = ReadOldestIngress(connection);
        return DateTimeOffset.TryParse(oldest, out DateTimeOffset created)
            ? Math.Max(0, Convert.ToInt64((DateTimeOffset.UtcNow - created).TotalSeconds))
            : 0;
    }

    private static long ReadOperationCount(SqliteConnection connection, string state)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM channel_security_operations_v2 WHERE state=$state";
        command.Parameters.AddWithValue("$state", state);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static long ReadExpiredReservationCount(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM channel_security_operations_v2 WHERE state='RESERVED' AND lease_expires_at_utc <= $now";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static long ReadStreamCount(SqliteConnection connection, bool isFinal)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM runtime_channel_streams WHERE is_final=$final";
        command.Parameters.AddWithValue("$final", isFinal);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static long ReadLifecycleFailureCount(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM channel_registration_lifecycle_audit WHERE to_status IN ('ERROR','RECOVERY_REQUIRED')";
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static IEnumerable<string> ReadRateLimitDimensions(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT substr(counter_key,1,instr(counter_key,':')-1) FROM channel_security_rate_windows ORDER BY 1";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read()) yield return reader.GetString(0);
    }

    private static IEnumerable<ChannelRegistrationOperationalHealth> ReadRegistrationHealth(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.attachment_id,a.provider_key,a.status,
              (SELECT COUNT(*) FROM channel_ingress_outbox i WHERE i.attachment_id=a.attachment_id),
              (SELECT COALESCE(MIN(created_at_utc),'') FROM channel_ingress_outbox i WHERE i.attachment_id=a.attachment_id),
              (SELECT COUNT(*) FROM runtime_channel_streams s WHERE s.attachment_id=a.attachment_id AND s.is_final=0),
              (SELECT COUNT(*) FROM runtime_channel_streams s WHERE s.attachment_id=a.attachment_id AND s.is_final=1),
              (SELECT COUNT(*) FROM channel_registration_lifecycle_audit l WHERE l.attachment_id=a.attachment_id AND l.to_status IN ('ERROR','RECOVERY_REQUIRED'))
            FROM runtime_channel_attachments a ORDER BY a.attachment_id
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string status = reader.GetString(2);
            yield return new ChannelRegistrationOperationalHealth
            {
                RegistrationId = reader.GetString(0),
                ProviderKey = reader.GetString(1),
                LifecycleStatus = status,
                Ready = string.Equals(status, "ACTIVE", StringComparison.Ordinal),
                IngressDepth = reader.GetInt64(3),
                OldestUnacknowledgedIngressAtUtc = reader.GetString(4),
                ActiveStreams = reader.GetInt64(5),
                CompletedStreams = reader.GetInt64(6),
                OperatorActionFailures = reader.GetInt64(7),
            };
        }
    }
}

internal sealed record ChannelSecurityRuntimeHealth(
    DateTimeOffset ClientCertificateNotAfterUtc,
    DateTimeOffset DelegationCertificateNotAfterUtc,
    string RotationState,
    DateTimeOffset? RotationOverlapExpiresAtUtc,
    IReadOnlyList<string> RevokedIdentityKeyIds)
{
    public static ChannelSecurityRuntimeHealth Unspecified { get; } = new(
        DateTimeOffset.MaxValue, DateTimeOffset.MaxValue, "UNSPECIFIED", null, []);
}
