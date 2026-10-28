using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

using Google.Protobuf;

using Grpc.Core;

using Kautuka.Protos;

using Microsoft.Data.Sqlite;

namespace Kautuka.ChannelHost.Grpc;

internal sealed class ChannelResourceGovernor
{
    private const int maximumRequestBytes = 1024 * 1024;
    private static readonly TimeSpan window = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, WindowCounter> counters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> activeStreams = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> activeOperations = new(StringComparer.Ordinal);
    private readonly ChannelHostDatabase? database;
    private readonly TimeProvider timeProvider;
    private long denied;

    public ChannelResourceGovernor(TimeProvider timeProvider)
    {
        this.timeProvider = timeProvider;
    }

    public ChannelResourceGovernor(ChannelHostDatabase database, TimeProvider timeProvider)
    {
        this.database = database;
        this.timeProvider = timeProvider;
        EnsureSchema();
    }

    public long DeniedCount => Interlocked.Read(ref denied);

    public void Enforce(ScopedRequestContext context, string method, IMessage request, string workloadId = "unattributed")
    {
        if (request.CalculateSize() > maximumRequestBytes) Deny("channel_request_too_large");
        DateTimeOffset now = timeProvider.GetUtcNow();
        List<(string Key, int Limit)> limits =
        [
            ("workload:" + Normalize(workloadId), 2000),
            ("tenant:" + Normalize(context.TenantId), 1000),
            ("owner:" + Normalize(context.ActorUserId), 600),
            ("operation:" + Normalize(workloadId) + ":" + method, IsMutation(method) ? 60 : 600),
        ];

        (string? registration, string? conversation) = ResourceKeys(request);
        if (!string.IsNullOrWhiteSpace(registration)) limits.Add(("registration:" + registration, 240));
        if (!string.IsNullOrWhiteSpace(conversation)) limits.Add(("conversation:" + conversation, 120));
        string? provider = ProviderKey(request, registration);
        if (!string.IsNullOrWhiteSpace(provider)) limits.Add(("provider:" + provider, 480));
        if (method is "StreamIngress" or "AcknowledgeIngress") limits.Add(("provider-ingress:" + Normalize(provider ?? "canonical"), 600));
        if (request is SendOutboundMessageRequest outbound)
        {
            string action = Normalize(outbound.Content?.ActionType ?? "unknown");
            limits.Add(("outbound:" + Normalize(registration ?? "default"), 120));
            if (action.Contains("edit", StringComparison.OrdinalIgnoreCase))
                limits.Add(("edit:" + Normalize(registration ?? "default"), 30));
            if (action.Contains("retry", StringComparison.OrdinalIgnoreCase))
                limits.Add(("retry:" + Normalize(registration ?? "default"), 20));
        }
        if (request is ReadInboundMediaRequest)
            limits.Add(("media:" + Normalize(registration ?? "default"), 120));

        if (database is null)
        {
            foreach ((string key, int limit) in limits) Check(key, limit, now);
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(registration) && IsMutation(method))
                EnforceRegistrationBackpressure(registration);
            CheckDurable(limits, now);
        }

        if (counters.Count > 10_000)
        {
            foreach ((string key, WindowCounter value) in counters)
                if (value.WindowStartedAt < now.Subtract(window + window)) _ = counters.TryRemove(key, out _);
        }
    }

    private void CheckDurable(IEnumerable<(string Key, int Limit)> limits, DateTimeOffset now)
    {
        using SqliteConnection connection = database!.OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        string nowValue = now.ToString("O");
        string resetBefore = now.Subtract(window).ToString("O");
        string? denial = null;
        foreach ((string key, int limit) in limits)
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO channel_security_rate_windows(counter_key,window_started_at_utc,request_count)
                VALUES($key,$now,1)
                ON CONFLICT(counter_key) DO UPDATE SET
                  window_started_at_utc=CASE WHEN window_started_at_utc <= $resetBefore THEN excluded.window_started_at_utc ELSE window_started_at_utc END,
                  request_count=CASE WHEN window_started_at_utc <= $resetBefore THEN 1 ELSE request_count + 1 END
                RETURNING request_count
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$now", nowValue);
            command.Parameters.AddWithValue("$resetBefore", resetBefore);
            if (Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) > limit)
                denial ??= "channel_rate_limit_exceeded";
        }
        using (SqliteCommand cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = transaction;
            cleanup.CommandText = "DELETE FROM channel_security_rate_windows WHERE window_started_at_utc <= $expired";
            cleanup.Parameters.AddWithValue("$expired", now.Subtract(window + window).ToString("O"));
            _ = cleanup.ExecuteNonQuery();
        }
        transaction.Commit();
        if (denial is not null) Deny(denial);
    }

    private void EnsureSchema()
    {
        using SqliteConnection connection = database!.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS channel_security_rate_windows(
              counter_key TEXT PRIMARY KEY,
              window_started_at_utc TEXT NOT NULL,
              request_count INTEGER NOT NULL);
            """;
        _ = command.ExecuteNonQuery();
    }

    private void EnforceRegistrationBackpressure(string registrationId)
    {
        using SqliteConnection connection = database!.OpenConnection();
        if (TableExists(connection, "channel_ingress_outbox")
            && ReadScalar(connection, "SELECT COUNT(*) FROM channel_ingress_outbox WHERE attachment_id=$id", registrationId) >= 10_000)
            Deny("registration_ingress_queue_depth_exceeded");
        if (TableExists(connection, "runtime_channel_streams")
            && ReadScalar(connection, "SELECT COUNT(*) FROM runtime_channel_streams WHERE attachment_id=$id AND is_final=0", registrationId) >= 32)
            Deny("registration_stream_queue_depth_exceeded");
        if (TableExists(connection, "channel_outbound_assets"))
        {
            if (ReadScalar(connection, "SELECT COUNT(*) FROM channel_outbound_assets WHERE attachment_id=$id AND state NOT IN ('consumed','expired')", registrationId) >= 256)
                Deny("registration_outbound_queue_depth_exceeded");
            if (ReadScalar(connection, "SELECT COALESCE(SUM(declared_size_bytes),0) FROM channel_outbound_assets WHERE attachment_id=$id AND state NOT IN ('consumed','expired')", registrationId) >= 256L * 1024 * 1024)
                Deny("registration_storage_limit_exceeded");
        }
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name";
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    [SuppressMessage("Security", "CA2100", Justification = "Only fixed internal SQL statements are passed by this governor.")]
    private static long ReadScalar(SqliteConnection connection, string sql, string registrationId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", registrationId);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public IDisposable AcquireExclusiveIngressStream(string subscriberId)
    {
        string key = "ingress:" + subscriberId;
        int count = activeStreams.AddOrUpdate(key, 1, static (_, current) => current + 1);
        if (count > 1)
        {
            _ = activeStreams.AddOrUpdate(key, 0, static (_, current) => Math.Max(0, current - 1));
            Deny("canonical_ingress_stream_already_connected");
        }
        return new StreamLease(activeStreams, key);
    }

    public IDisposable AcquireRegistrationOperation(string registrationId, string operation, int limit = 8)
    {
        string key = "operation:" + Normalize(registrationId) + ":" + Normalize(operation);
        int count = activeOperations.AddOrUpdate(key, 1, static (_, current) => current + 1);
        if (count > limit)
        {
            _ = activeOperations.AddOrUpdate(key, 0, static (_, current) => Math.Max(0, current - 1));
            Deny("registration_concurrent_work_limit_exceeded");
        }
        return new StreamLease(activeOperations, key);
    }

    public void EnforceProviderIngress(string providerKey, string registrationId)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        List<(string Key, int Limit)> limits =
        [
            ("provider-ingress:" + Normalize(providerKey), 1200),
            ("registration-ingress:" + Normalize(registrationId), 300),
        ];
        if (database is null)
        {
            foreach ((string key, int limit) in limits) Check(key, limit, now);
        }
        else
        {
            EnforceRegistrationBackpressure(registrationId);
            CheckDurable(limits, now);
        }
    }

    private void Check(string key, int limit, DateTimeOffset now)
    {
        WindowCounter result = counters.AddOrUpdate(
            key,
            _ => new WindowCounter(now, 1),
            (_, current) => now - current.WindowStartedAt >= window
                ? new WindowCounter(now, 1)
                : current with { Count = current.Count + 1 });
        if (result.Count > limit) Deny("channel_rate_limit_exceeded");
    }

    private void Deny(string reason)
    {
        _ = Interlocked.Increment(ref denied);
        throw new RpcException(new Status(StatusCode.ResourceExhausted, reason));
    }

    private static bool IsMutation(string method) => method is
        "UpsertAttachment" or "ProvisionAttachment" or "DeactivateAttachment" or
        "TransitionAttachmentLifecycle" or "SetRuntimeCredentials" or "SendOutboundMessage" or
        "UploadOutboundAsset" or "AcknowledgeIngress";

    private static (string? Registration, string? Conversation) ResourceKeys(IMessage request) => request switch
    {
        GetAttachmentRequest value => (value.AttachmentId, null),
        GetAttachmentSchemaRequest value => (value.AttachmentId, null),
        UpsertAttachmentRequest value => (value.AttachmentId, null),
        ProvisionAttachmentRequest value => (value.AttachmentId, null),
        DeactivateAttachmentRequest value => (value.AttachmentId, null),
        TransitionAttachmentLifecycleRequest value => (value.AttachmentId, null),
        SetRuntimeCredentialsRequest value => (value.AttachmentId, null),
        UploadOutboundAssetRequest value when value.Metadata is not null => (value.Metadata.AttachmentId, value.Metadata.ConversationId),
        ReadInboundMediaRequest value => (value.RegistrationId, null),
        SendOutboundMessageRequest value => (value.AttachmentId, value.ConversationId),
        _ => (null, null),
    };

    private static string? ProviderKey(IMessage request, string? registration) => request switch
    {
        UpsertAttachmentRequest value => value.ProviderKey,
        _ when !string.IsNullOrWhiteSpace(registration) && registration.Contains(':', StringComparison.Ordinal)
            => registration[..registration.IndexOf(':', StringComparison.Ordinal)],
        _ => null,
    };

    private static string Normalize(string value) => string.IsNullOrWhiteSpace(value) ? "default" : value.Trim();

    private sealed record WindowCounter(DateTimeOffset WindowStartedAt, int Count);

    private sealed class StreamLease(ConcurrentDictionary<string, int> streams, string key) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            _ = streams.AddOrUpdate(key, 0, static (_, current) => Math.Max(0, current - 1));
        }
    }
}
