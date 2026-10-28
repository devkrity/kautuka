using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

using Kautuka.Protos;

using Microsoft.Data.Sqlite;

namespace Kautuka.ChannelHost.Hosting;

internal sealed class ChannelOutboundAssetStore
{
    internal const long MaximumAssetBytes = 25L * 1024 * 1024;
    internal const int MaximumChunkBytes = 64 * 1024;
    private static readonly TimeSpan lifetime = TimeSpan.FromHours(1);
    private readonly ChannelHostDatabase database;
    private readonly string spoolRoot;

    public ChannelOutboundAssetStore(ChannelHostDatabase database, IConfiguration configuration)
    {
        this.database = database;
        spoolRoot = Path.Combine(KautukaHostPathPolicy.ResolveServiceStateRoot(configuration, "channelHost"), "outbound-assets");
        Directory.CreateDirectory(spoolRoot);
        RestrictSpoolDirectory(spoolRoot);
        EnsureSchema();
        Cleanup(DateTimeOffset.UtcNow);
    }

    public async Task<ChannelOutboundAssetUploadResult> ReceiveAsync(
        UploadOutboundAssetMetadata metadata,
        IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
        CancellationToken cancellationToken)
    {
        ValidateMetadata(metadata);
        string metadataDigest = ComputeMetadataDigest(metadata);
        ChannelOutboundAssetUploadResult? existing = FindIdempotent(metadata.AttachmentId, metadata.IdempotencyKey, metadataDigest);
        if (existing is not null) return existing;

        string uploadId = Guid.NewGuid().ToString("N");
        string finalPath = Path.Combine(spoolRoot, uploadId + ".asset");
        string temporaryPath = finalPath + ".receiving";
        DateTimeOffset now = DateTimeOffset.UtcNow;
        InsertReceiving(uploadId, metadata, metadataDigest, finalPath, now);
        long total = 0;
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                MaximumChunkBytes, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await foreach (ReadOnlyMemory<byte> chunk in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    if (chunk.Length == 0) continue;
                    if (chunk.Length > MaximumChunkBytes) throw new InvalidOperationException("outbound_asset_chunk_too_large");
                    total += chunk.Length;
                    if (total > metadata.DeclaredSizeBytes || total > MaximumAssetBytes)
                        throw new InvalidOperationException("outbound_asset_size_exceeded");
                    hasher.AppendData(chunk.Span);
                    await stream.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
                }
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            SetOwnerOnly(temporaryPath);
            string actualDigest = Convert.ToHexString(hasher.GetHashAndReset());
            if (total != metadata.DeclaredSizeBytes
                || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(actualDigest), Encoding.ASCII.GetBytes(metadata.Sha256Hex)))
                throw new InvalidOperationException("outbound_asset_digest_or_size_mismatch");

            File.Move(temporaryPath, finalPath);
            SetOwnerOnly(finalPath);
            MarkAvailable(uploadId, now.Add(lifetime));
            return new ChannelOutboundAssetUploadResult(uploadId, true, "available", "asset_verified");
        }
        catch
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            if (File.Exists(finalPath)) File.Delete(finalPath);
            DeleteState(uploadId);
            throw;
        }
    }

    public ChannelOutboundAssetLease? OpenAvailable(string uploadId, string attachmentId, string conversationId)
    {
        Cleanup(DateTimeOffset.UtcNow);
        using SqliteConnection connection = database.OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using (SqliteCommand claim = connection.CreateCommand())
        {
            claim.Transaction = transaction;
            claim.CommandText = """
                UPDATE channel_outbound_assets SET state='DELIVERING'
                WHERE upload_id=$upload AND attachment_id=$attachment AND conversation_id=$conversation
                  AND state='AVAILABLE' AND expires_at_utc>$now
                """;
            claim.Parameters.AddWithValue("$upload", uploadId);
            claim.Parameters.AddWithValue("$attachment", attachmentId);
            claim.Parameters.AddWithValue("$conversation", conversationId);
            claim.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            if (claim.ExecuteNonQuery() != 1) return null;
        }
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT safe_file_name,content_type,declared_size_bytes,sha256_hex,spool_path
            FROM channel_outbound_assets
            WHERE upload_id=$upload AND attachment_id=$attachment AND conversation_id=$conversation
              AND state='DELIVERING'
            """;
        command.Parameters.AddWithValue("$upload", uploadId);
        command.Parameters.AddWithValue("$attachment", attachmentId);
        command.Parameters.AddWithValue("$conversation", conversationId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        string path = reader.GetString(4);
        if (!File.Exists(path)) return null;
        var lease = new ChannelOutboundAssetLease(
            uploadId,
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt64(2),
            reader.GetString(3),
            path);
        reader.Close();
        transaction.Commit();
        return lease;
    }

    public void MarkConsumed(string uploadId)
    {
        string? path = UpdateStateAndGetPath(uploadId, "CONSUMED");
        if (path is not null && File.Exists(path)) File.Delete(path);
    }

    public void DeleteForAttachment(string attachmentId)
    {
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand select = connection.CreateCommand();
        select.CommandText = "SELECT spool_path FROM channel_outbound_assets WHERE attachment_id=$attachment";
        select.Parameters.AddWithValue("$attachment", attachmentId);
        var paths = new List<string>();
        using (SqliteDataReader reader = select.ExecuteReader()) while (reader.Read()) paths.Add(reader.GetString(0));
        using SqliteCommand delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM channel_outbound_assets WHERE attachment_id=$attachment";
        delete.Parameters.AddWithValue("$attachment", attachmentId);
        _ = delete.ExecuteNonQuery();
        foreach (string path in paths) if (File.Exists(path)) File.Delete(path);
    }

    public void Cleanup(DateTimeOffset now)
    {
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand select = connection.CreateCommand();
        select.CommandText = "SELECT upload_id,spool_path FROM channel_outbound_assets WHERE state='RECEIVING' OR expires_at_utc<=$now OR state='CONSUMED'";
        select.Parameters.AddWithValue("$now", now.ToString("O"));
        var expired = new List<(string Id, string Path)>();
        using (SqliteDataReader reader = select.ExecuteReader()) while (reader.Read()) expired.Add((reader.GetString(0), reader.GetString(1)));
        foreach ((string id, string path) in expired)
        {
            if (File.Exists(path)) File.Delete(path);
            string temp = path + ".receiving";
            if (File.Exists(temp)) File.Delete(temp);
            DeleteState(id);
        }
    }

    private static void ValidateMetadata(UploadOutboundAssetMetadata value)
    {
        if (string.IsNullOrWhiteSpace(value.AssetId) || value.AssetId.Length > 256
            || string.IsNullOrWhiteSpace(value.AttachmentId) || value.AttachmentId.Length > 256
            || string.IsNullOrWhiteSpace(value.ConversationId) || value.ConversationId.Length > 256
            || string.IsNullOrWhiteSpace(value.IdempotencyKey) || value.IdempotencyKey.Length > 256
            || value.DeclaredSizeBytes is < 1 or > MaximumAssetBytes)
            throw new InvalidOperationException("outbound_asset_metadata_invalid");
        if (string.IsNullOrWhiteSpace(value.SafeFileName) || value.SafeFileName.Length > 255
            || !string.Equals(Path.GetFileName(value.SafeFileName), value.SafeFileName, StringComparison.Ordinal)
            || value.SafeFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("outbound_asset_file_name_unsafe");
        if (string.IsNullOrWhiteSpace(value.ContentType) || value.ContentType.Length > 128
            || value.ContentType.Contains('\r', StringComparison.Ordinal) || value.ContentType.Contains('\n', StringComparison.Ordinal))
            throw new InvalidOperationException("outbound_asset_content_type_invalid");
        if (value.Sha256Hex.Length != 64 || value.Sha256Hex.Any(static character => !Uri.IsHexDigit(character)))
            throw new InvalidOperationException("outbound_asset_digest_invalid");
        value.Sha256Hex = value.Sha256Hex.ToUpperInvariant();
    }

    private ChannelOutboundAssetUploadResult? FindIdempotent(string attachmentId, string idempotencyKey, string digest)
    {
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT upload_id,metadata_digest,state,expires_at_utc,spool_path FROM channel_outbound_assets WHERE attachment_id=$attachment AND idempotency_key=$key";
        command.Parameters.AddWithValue("$attachment", attachmentId);
        command.Parameters.AddWithValue("$key", idempotencyKey);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        if (!string.Equals(reader.GetString(1), digest, StringComparison.Ordinal))
            throw new InvalidOperationException("outbound_asset_idempotency_conflict");
        string uploadId = reader.GetString(0);
        string state = reader.GetString(2);
        bool expired = DateTimeOffset.TryParse(reader.GetString(3), out DateTimeOffset expires) && expires <= DateTimeOffset.UtcNow;
        string path = reader.GetString(4);
        reader.Close();
        if (expired)
        {
            if (File.Exists(path)) File.Delete(path);
            DeleteState(uploadId);
            return null;
        }
        return state == "AVAILABLE"
            ? new ChannelOutboundAssetUploadResult(uploadId, true, "available", "idempotent_replay")
            : throw new InvalidOperationException("outbound_asset_upload_in_progress");
    }

    private void InsertReceiving(string uploadId, UploadOutboundAssetMetadata value, string digest, string path, DateTimeOffset now)
    {
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO channel_outbound_assets(upload_id,asset_id,attachment_id,owner_user_id,tenant_id,conversation_id,
                conversation_thread_id,safe_file_name,content_type,declared_size_bytes,sha256_hex,idempotency_key,
                metadata_digest,spool_path,state,created_at_utc,expires_at_utc)
            VALUES($upload,$asset,$attachment,$owner,$tenant,$conversation,$thread,$name,$type,$size,$sha,$key,$digest,$path,'RECEIVING',$now,$expires)
            """;
        command.Parameters.AddWithValue("$upload", uploadId);
        command.Parameters.AddWithValue("$asset", value.AssetId);
        command.Parameters.AddWithValue("$attachment", value.AttachmentId);
        command.Parameters.AddWithValue("$owner", value.Context.ActorUserId);
        command.Parameters.AddWithValue("$tenant", value.Context.TenantId);
        command.Parameters.AddWithValue("$conversation", value.ConversationId);
        command.Parameters.AddWithValue("$thread", value.ConversationThreadId);
        command.Parameters.AddWithValue("$name", value.SafeFileName);
        command.Parameters.AddWithValue("$type", value.ContentType);
        command.Parameters.AddWithValue("$size", value.DeclaredSizeBytes);
        command.Parameters.AddWithValue("$sha", value.Sha256Hex);
        command.Parameters.AddWithValue("$key", value.IdempotencyKey);
        command.Parameters.AddWithValue("$digest", digest);
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$now", now.ToString("O"));
        command.Parameters.AddWithValue("$expires", now.Add(lifetime).ToString("O"));
        _ = command.ExecuteNonQuery();
    }

    private void MarkAvailable(string uploadId, DateTimeOffset expires)
    {
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE channel_outbound_assets SET state='AVAILABLE',expires_at_utc=$expires WHERE upload_id=$upload AND state='RECEIVING'";
        command.Parameters.AddWithValue("$expires", expires.ToString("O"));
        command.Parameters.AddWithValue("$upload", uploadId);
        if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("outbound_asset_state_transition_failed");
    }

    private string? UpdateStateAndGetPath(string uploadId, string state)
    {
        using SqliteConnection connection = database.OpenConnection();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = "SELECT spool_path FROM channel_outbound_assets WHERE upload_id=$upload AND state='DELIVERING'";
        select.Parameters.AddWithValue("$upload", uploadId);
        string? path = select.ExecuteScalar() as string;
        if (path is null) return null;
        using SqliteCommand update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE channel_outbound_assets SET state=$state WHERE upload_id=$upload AND state='DELIVERING'";
        update.Parameters.AddWithValue("$state", state);
        update.Parameters.AddWithValue("$upload", uploadId);
        _ = update.ExecuteNonQuery();
        transaction.Commit();
        return path;
    }

    private void DeleteState(string uploadId)
    {
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM channel_outbound_assets WHERE upload_id=$upload";
        command.Parameters.AddWithValue("$upload", uploadId);
        _ = command.ExecuteNonQuery();
    }

    private void EnsureSchema()
    {
        using SqliteConnection connection = database.OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS channel_outbound_assets(
                upload_id TEXT PRIMARY KEY, asset_id TEXT NOT NULL, attachment_id TEXT NOT NULL,
                owner_user_id TEXT NOT NULL, tenant_id TEXT NOT NULL, conversation_id TEXT NOT NULL,
                conversation_thread_id TEXT NOT NULL, safe_file_name TEXT NOT NULL, content_type TEXT NOT NULL,
                declared_size_bytes INTEGER NOT NULL, sha256_hex TEXT NOT NULL, idempotency_key TEXT NOT NULL,
                metadata_digest TEXT NOT NULL, spool_path TEXT NOT NULL, state TEXT NOT NULL,
                created_at_utc TEXT NOT NULL, expires_at_utc TEXT NOT NULL,
                UNIQUE(attachment_id,idempotency_key));
            CREATE INDEX IF NOT EXISTS idx_channel_outbound_assets_expiry ON channel_outbound_assets(state,expires_at_utc);
            """;
        _ = command.ExecuteNonQuery();
    }

    private static string ComputeMetadataDigest(UploadOutboundAssetMetadata value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',
        value.AssetId, value.AttachmentId, value.Context.ActorUserId, value.Context.TenantId, value.ConversationId,
        value.ConversationThreadId, value.SafeFileName, value.ContentType, value.DeclaredSizeBytes, value.Sha256Hex))));

    private static void RestrictSpoolDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().User!,
                FileSystemRights.Modify | FileSystemRights.ReadAndExecute | FileSystemRights.DeleteSubdirectoriesAndFiles,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(security);
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void SetOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().User!,
                FileSystemRights.Read | FileSystemRights.Write,
                InheritanceFlags.None,
                PropagationFlags.None,
                AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

internal sealed record ChannelOutboundAssetUploadResult(string UploadId, bool Accepted, string Status, string Detail);

internal sealed class ChannelOutboundAssetLease(
    string uploadId,
    string safeFileName,
    string contentType,
    long sizeBytes,
    string sha256Hex,
    string path) : IDisposable
{
    public string UploadId { get; } = uploadId;
    public string SafeFileName { get; } = safeFileName;
    public string ContentType { get; } = contentType;
    public long SizeBytes { get; } = sizeBytes;
    public string Sha256Hex { get; } = sha256Hex;
    public Stream Content { get; } = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
        ChannelOutboundAssetStore.MaximumChunkBytes, FileOptions.Asynchronous);
    public void Dispose() => Content.Dispose();
}
