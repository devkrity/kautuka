using System.Globalization;
using System.Text.Json;

using Kautuka.Gateway.Domain.AgentFiles;
using Kautuka.Gateway.Domain.Sandbox;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Domain.Assets;

internal sealed record StoredConversationAsset(
    GatewayConversationAsset Public,
    string SessionId,
    string? OwnerUserId,
    string StoragePath);

internal sealed record AssetUploadCommand(
    string FileName,
    string ContentType,
    long SizeBytes,
    string Origin,
    string Disposition,
    Stream Content);

internal sealed record AssetDownloadResult(
    string FileName,
    string ContentType,
    Stream Content,
    bool ForceDownload);

internal interface IConversationAssetStore
{
    Task<GatewayConversationAsset> CreateUploadAsync(string sessionId, string? userId, AssetUploadCommand command, CancellationToken cancellationToken);
    IReadOnlyDictionary<string, IReadOnlyList<GatewayConversationAsset>> ListAssetsByMessage(string sessionId, IReadOnlyList<string> messageIds);
    IReadOnlyList<GatewayConversationAsset> ListSessionAssets(string sessionId, string? userId, IReadOnlyList<string>? assetIds = null);
    IReadOnlyList<StoredConversationAsset> GetAssetsByIds(string sessionId, IReadOnlyList<string> assetIds, string? userId);
    bool HasRetainedWorkspaceReference(string agentId, string relativePath, string ownerUserId);
    IReadOnlyList<WorkspaceAttachmentReferenceSummary> ListRetainedWorkspaceAttachmentReferences(string agentId);
    IReadOnlyList<WorkspaceAttachmentFileSummary> ListUnreferencedWorkspaceAttachmentFiles(string agentId);
    string? FindLatestAssetIdByFileName(string sessionId, string fileName, string? userId);
    (string SessionId, string AssetId)? FindLatestAssetReferenceByFileName(string fileName, string? userId);
    void BindAssetsToMessage(string sessionId, string messageId, IReadOnlyList<string> assetIds, string? userId);
    bool TryOpenAsset(string sessionId, string assetId, string? userId, out AssetDownloadResult? result);
    void DeleteSessionAssets(IReadOnlyList<string> sessionIds);
}

internal sealed class ConversationAssetStore(
    KautukaDatabase database,
    GatewayState state,
    IWorkspaceRuntimeTool workspaceTool,
    ILogger<ConversationAssetStore> logger) : IConversationAssetStore
{
    private const long maxAssetBytes = 15L * 1024L * 1024L;
    private const string attachmentsFolderName = "attachments";
    private const string sessionIdParameter = "$sessionId";
    private const string assetIdsJsonParameter = "$assetIdsJson";
    private const string messageIdsJsonParameter = "$messageIdsJson";
    private const string sessionIdsJsonParameter = "$sessionIdsJson";
    private static readonly HashSet<string> reservedInternalEntryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".kautuka",
    };

    public async Task<GatewayConversationAsset> CreateUploadAsync(string sessionId, string? userId, AssetUploadCommand command, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(command);

        if (command.SizeBytes <= 0 || command.SizeBytes > maxAssetBytes)
        {
            throw new InvalidOperationException($"Asset size must be between 1 byte and {maxAssetBytes.ToString(CultureInfo.InvariantCulture)} bytes.");
        }

        string assetId = $"asset-{Guid.NewGuid():N}";
        string safeFileName = string.IsNullOrWhiteSpace(command.FileName)
            ? assetId
            : WorkspaceAttachmentUploadPolicy.NormalizeLeafFileName(command.FileName, reservedNames: reservedInternalEntryNames);
        string normalizedOrigin = NormalizeOrigin(command.Origin);
        string normalizedDisposition = NormalizeDisposition(command.Disposition);
        string normalizedContentType = WorkspaceAttachmentUploadPolicy.NormalizeContentType(
            command.ContentType,
            safeFileName,
            enforceBlockedUploadPolicy: !string.Equals(normalizedOrigin, "agentOutput", StringComparison.Ordinal));
        AssetStorageLocation storageLocation = ResolveStorageLocation(sessionId, safeFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(storageLocation.FullPath)!);
        string createdAt = DateTimeOffset.UtcNow.ToString("O");
        GatewayConversationAsset publicAsset = ToPublicAsset(new ConversationAssetRecord(
            assetId,
            sessionId,
            storageLocation.FileName,
            normalizedContentType,
            command.SizeBytes,
            normalizedOrigin,
            normalizedDisposition,
            createdAt));

        await StageUploadAsync(assetId, sessionId, storageLocation.StoragePath, createdAt, cancellationToken).ConfigureAwait(false);
        try
        {
            using (FileStream fileStream = new(
                storageLocation.FullPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await command.Content.CopyToAsync(fileStream, cancellationToken).ConfigureAwait(false);
                await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1849 // Flush(true) is the only FileStream API that requests an OS flush to stable storage.
                fileStream.Flush(flushToDisk: true);
#pragma warning restore CA1849
            }

            WorkspaceAttachmentUploadPolicy.SetSafeHostFilePermissions(storageLocation.FullPath);
            await CommitUploadAsync(
                assetId,
                sessionId,
                userId,
                storageLocation,
                normalizedContentType,
                command.SizeBytes,
                normalizedOrigin,
                normalizedDisposition,
                createdAt,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            bool? metadataCommitted = await TryAssetMetadataExistsAsync(assetId).ConfigureAwait(false);
            if (metadataCommitted == true)
            {
                return publicAsset;
            }

            if (metadataCommitted == false && TryDeleteAssetFile(storageLocation.FullPath))
            {
                await TryDeleteStagingRecordAsync(assetId).ConfigureAwait(false);
            }
            throw;
        }

        return publicAsset;
    }

    internal async Task ReconcileStagedUploadsAsync(CancellationToken cancellationToken)
    {
        List<StagedAssetUpload> stagedUploads = [];
        using (var connection = new SqliteConnection(database.ConnectionString))
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT asset_id, session_id, storage_path FROM conversation_asset_upload_staging ORDER BY created_at;";
            using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                stagedUploads.Add(new StagedAssetUpload(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        foreach (StagedAssetUpload staged in stagedUploads)
        {
            bool? metadataCommitted = await TryAssetMetadataExistsAsync(staged.AssetId).ConfigureAwait(false);
            if (metadataCommitted == true)
            {
                await TryDeleteStagingRecordAsync(staged.AssetId).ConfigureAwait(false);
                continue;
            }

            if (metadataCommitted is null)
            {
                continue;
            }

            if (TryResolveStagedAssetFullPath(staged, out string fullPath))
            {
                if (TryDeleteAssetFile(fullPath))
                {
                    await TryDeleteStagingRecordAsync(staged.AssetId).ConfigureAwait(false);
                }
            }
            else
            {
                ConversationAssetStoreLog.StagedAssetReconciliationDeferred(logger, staged.AssetId, staged.SessionId);
            }
        }
    }

    private async Task StageUploadAsync(
        string assetId,
        string sessionId,
        string storagePath,
        string createdAt,
        CancellationToken cancellationToken)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO conversation_asset_upload_staging (asset_id, session_id, storage_path, created_at) "
            + "VALUES ($assetId, $sessionId, $storagePath, $createdAt);";
        command.Parameters.AddWithValue("$assetId", assetId);
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$storagePath", storagePath);
        command.Parameters.AddWithValue("$createdAt", createdAt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task CommitUploadAsync(
        string assetId,
        string sessionId,
        string? userId,
        AssetStorageLocation storageLocation,
        string contentType,
        long sizeBytes,
        string origin,
        string disposition,
        string createdAt,
        CancellationToken cancellationToken)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = (SqliteTransaction)await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO conversation_assets (
                    id, session_id, owner_user_id, file_name, content_type, size_bytes, origin, disposition, storage_path, created_at
                ) VALUES (
                    $id, $sessionId, $ownerUserId, $fileName, $contentType, $sizeBytes, $origin, $disposition, $storagePath, $createdAt
                );
                """;
            insert.Parameters.AddWithValue("$id", assetId);
            insert.Parameters.AddWithValue(sessionIdParameter, sessionId);
            insert.Parameters.AddWithValue("$ownerUserId", (object?)userId ?? DBNull.Value);
            insert.Parameters.AddWithValue("$fileName", storageLocation.FileName);
            insert.Parameters.AddWithValue("$contentType", contentType);
            insert.Parameters.AddWithValue("$sizeBytes", sizeBytes);
            insert.Parameters.AddWithValue("$origin", origin);
            insert.Parameters.AddWithValue("$disposition", disposition);
            insert.Parameters.AddWithValue("$storagePath", storageLocation.StoragePath);
            insert.Parameters.AddWithValue("$createdAt", createdAt);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        using (var deleteStaging = connection.CreateCommand())
        {
            deleteStaging.Transaction = transaction;
            deleteStaging.CommandText = "DELETE FROM conversation_asset_upload_staging WHERE asset_id = $assetId;";
            deleteStaging.Parameters.AddWithValue("$assetId", assetId);
            await deleteStaging.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task TryDeleteStagingRecordAsync(string assetId)
    {
        try
        {
            using var connection = new SqliteConnection(database.ConnectionString);
            await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM conversation_asset_upload_staging WHERE asset_id = $assetId;";
            command.Parameters.AddWithValue("$assetId", assetId);
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            ConversationAssetStoreLog.StagingRecordDeleteFailed(logger, ex, assetId);
        }
    }

    private async Task<bool?> TryAssetMetadataExistsAsync(string assetId)
    {
        try
        {
            using var connection = new SqliteConnection(database.ConnectionString);
            await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM conversation_assets WHERE id = $assetId;";
            command.Parameters.AddWithValue("$assetId", assetId);
            object? result = await command.ExecuteScalarAsync(CancellationToken.None).ConfigureAwait(false);
            return Convert.ToInt64(result, CultureInfo.InvariantCulture) == 1L;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            ConversationAssetStoreLog.AssetCommitResolutionFailed(logger, ex, assetId);
            return null;
        }
    }

    private bool TryResolveStagedAssetFullPath(StagedAssetUpload staged, out string fullPath)
    {
        fullPath = string.Empty;
        if (!staged.StoragePath.StartsWith($"{attachmentsFolderName}/", StringComparison.Ordinal)
            || !TryGetWorkspaceAttachmentsRoot(staged.SessionId, out string attachmentsRoot))
        {
            return false;
        }

        string relativePath = staged.StoragePath[(attachmentsFolderName.Length + 1)..].Replace('/', Path.DirectorySeparatorChar);
        string candidate = Path.GetFullPath(Path.Combine(attachmentsRoot, relativePath));
        string root = Path.GetFullPath(attachmentsRoot) + Path.DirectorySeparatorChar;
        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!candidate.StartsWith(root, pathComparison))
        {
            return false;
        }

        fullPath = candidate;
        return true;
    }

    private bool TryDeleteAssetFile(string fullPath)
    {
        try
        {
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ConversationAssetStoreLog.AssetDeleteFailed(logger, ex, fullPath);
            return false;
        }
    }

    public IReadOnlyDictionary<string, IReadOnlyList<GatewayConversationAsset>> ListAssetsByMessage(string sessionId, IReadOnlyList<string> messageIds)
    {
        if (messageIds.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<GatewayConversationAsset>>(StringComparer.Ordinal);
        }

        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
                "SELECT r.message_id, a.id, a.file_name, a.content_type, a.size_bytes, a.origin, a.disposition, a.created_at "
                + "FROM message_asset_refs r "
                + "INNER JOIN conversation_assets a ON a.id = r.asset_id "
                + "WHERE r.session_id = $sessionId "
                + "AND r.message_id IN (SELECT value FROM json_each($messageIdsJson)) "
                + "ORDER BY r.created_at ASC;";
        command.Parameters.AddWithValue(sessionIdParameter, sessionId);
        command.Parameters.AddWithValue(messageIdsJsonParameter, SerializeStringArray(messageIds));

        var result = new Dictionary<string, List<GatewayConversationAsset>>(StringComparer.Ordinal);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string messageId = reader.GetString(0);
            GatewayConversationAsset asset = ToPublicAsset(new ConversationAssetRecord(
                reader.GetString(1),
                sessionId,
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7)));

            if (!result.TryGetValue(messageId, out List<GatewayConversationAsset>? assets))
            {
                assets = [];
                result[messageId] = assets;
            }

            assets.Add(asset);
        }

        return result.ToDictionary(static pair => pair.Key, static pair => (IReadOnlyList<GatewayConversationAsset>)pair.Value, StringComparer.Ordinal);
    }

    public IReadOnlyList<GatewayConversationAsset> ListSessionAssets(string sessionId, string? userId, IReadOnlyList<string>? assetIds = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        if (assetIds is { Count: > 0 })
        {
            command.CommandText =
                "SELECT id, session_id, owner_user_id, file_name, content_type, size_bytes, origin, disposition, created_at "
                + "FROM conversation_assets "
                + "WHERE session_id = $sessionId "
                + "AND id IN (SELECT value FROM json_each($assetIdsJson)) "
                + "ORDER BY created_at ASC;";
            command.Parameters.AddWithValue(assetIdsJsonParameter, SerializeStringArray(assetIds));
        }
        else
        {
            command.CommandText =
                "SELECT id, session_id, owner_user_id, file_name, content_type, size_bytes, origin, disposition, created_at "
                + "FROM conversation_assets "
                + "WHERE session_id = $sessionId "
                + "ORDER BY created_at ASC;";
        }

        command.Parameters.AddWithValue(sessionIdParameter, sessionId);

        List<GatewayConversationAsset> assets = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string? ownerUserId = reader.IsDBNull(2) ? null : reader.GetString(2);
            if (!string.IsNullOrWhiteSpace(ownerUserId) && !string.Equals(ownerUserId, userId, StringComparison.Ordinal))
            {
                continue;
            }

            assets.Add(ToPublicAsset(new ConversationAssetRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8))));
        }

        return assets;
    }

    public IReadOnlyList<StoredConversationAsset> GetAssetsByIds(string sessionId, IReadOnlyList<string> assetIds, string? userId)
    {
        if (assetIds.Count == 0)
        {
            return [];
        }

        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
                "SELECT id, session_id, owner_user_id, file_name, content_type, size_bytes, origin, disposition, storage_path, created_at "
                + "FROM conversation_assets "
                + "WHERE session_id = $sessionId "
                + "AND id IN (SELECT value FROM json_each($assetIdsJson));";
        command.Parameters.AddWithValue(sessionIdParameter, sessionId);
        command.Parameters.AddWithValue(assetIdsJsonParameter, SerializeStringArray(assetIds));

        List<StoredConversationAsset> assets = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string? ownerUserId = reader.IsDBNull(2) ? null : reader.GetString(2);
            if (!string.IsNullOrWhiteSpace(ownerUserId) && !string.Equals(ownerUserId, userId, StringComparison.Ordinal))
            {
                continue;
            }

            string assetId = reader.GetString(0);
            string assetSessionId = reader.GetString(1);
            string fileName = reader.GetString(3);
            string contentType = reader.GetString(4);
            long sizeBytes = reader.GetInt64(5);
            string origin = reader.GetString(6);
            string disposition = reader.GetString(7);
            string storagePath = reader.GetString(8);
            string createdAt = reader.GetString(9);

            assets.Add(new StoredConversationAsset(
                ToPublicAsset(new ConversationAssetRecord(assetId, assetSessionId, fileName, contentType, sizeBytes, origin, disposition, createdAt)),
                assetSessionId,
                ownerUserId,
                storagePath));
        }

        return assets;
    }

    public string? FindLatestAssetIdByFileName(string sessionId, string fileName, string? userId)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        string normalized = Path.GetFileName(fileName.Trim());
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, owner_user_id
            FROM conversation_assets
            WHERE session_id = $sessionId
              AND lower(file_name) = lower($fileName)
            ORDER BY created_at DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$fileName", normalized);

        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        string? ownerUserId = reader.IsDBNull(1) ? null : reader.GetString(1);
        if (!string.IsNullOrWhiteSpace(ownerUserId) && !string.Equals(ownerUserId, userId, StringComparison.Ordinal))
        {
            return null;
        }

        return reader.GetString(0);
    }

    public bool HasRetainedWorkspaceReference(string agentId, string relativePath, string ownerUserId)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(ownerUserId))
        {
            return true;
        }

        string normalizedPath = relativePath.Replace('\\', '/').Trim('/');
        return ListRetainedWorkspaceAttachmentReferences(agentId, ownerUserId)
            .Any(reference => string.Equals(reference.RelativePath, normalizedPath, StringComparison.Ordinal));
    }

    public IReadOnlyList<WorkspaceAttachmentReferenceSummary> ListRetainedWorkspaceAttachmentReferences(string agentId)
        => ListRetainedWorkspaceAttachmentReferences(agentId, ownerUserId: null);

    private IReadOnlyList<WorkspaceAttachmentReferenceSummary> ListRetainedWorkspaceAttachmentReferences(string agentId, string? ownerUserId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return [];
        }

        Dictionary<string, WorkspaceAttachmentReferenceSummary> summaries = new(StringComparer.Ordinal);

        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT session_id, storage_path, file_name
            FROM conversation_assets
            WHERE storage_path LIKE $storagePathPattern;
            """;
        command.Parameters.AddWithValue("$storagePathPattern", $"{attachmentsFolderName}/%");

        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            string sessionId = reader.GetString(0);
            if (!state.Sessions.TryGetValue(sessionId, out GatewaySession? session)
                || session is null
                || !string.Equals(session.TargetType, "agent", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(session.TargetId, agentId, StringComparison.Ordinal)
                || (!string.IsNullOrWhiteSpace(ownerUserId)
                    && !string.Equals(session.OwnerUserId, ownerUserId, StringComparison.Ordinal)))
            {
                continue;
            }

            string storagePath = reader.GetString(1);
            string fileName = reader.GetString(2);
            if (summaries.TryGetValue(storagePath, out WorkspaceAttachmentReferenceSummary? existing))
            {
                summaries[storagePath] = existing with { ReferenceCount = existing.ReferenceCount + 1 };
                continue;
            }

            summaries[storagePath] = new WorkspaceAttachmentReferenceSummary(storagePath, fileName, 1);
        }

        return [.. summaries.Values.OrderBy(static item => item.RelativePath, StringComparer.Ordinal)];
    }

    public IReadOnlyList<WorkspaceAttachmentFileSummary> ListUnreferencedWorkspaceAttachmentFiles(string agentId)
    {
        if (!TryGetAgentAttachmentsRoot(agentId, out string attachmentsRoot) || !Directory.Exists(attachmentsRoot))
        {
            return [];
        }

        HashSet<string> referencedPaths = new(
            ListRetainedWorkspaceAttachmentReferences(agentId)
                .Select(static item => item.RelativePath),
            StringComparer.Ordinal);

        return [.. WorkspaceReparseGuard.EnumerateFiles(attachmentsRoot)
            .Select(fullPath =>
            {
                string relativeFilePath = Path.GetRelativePath(attachmentsRoot, fullPath).Replace('\\', '/');
                return new
                {
                    RelativePath = $"{attachmentsFolderName}/{relativeFilePath}",
                    FullPath = fullPath,
                };
            })
            .Where(item => !referencedPaths.Contains(item.RelativePath))
            .Select(item =>
            {
                FileInfo fileInfo = new(item.FullPath);
                return new WorkspaceAttachmentFileSummary(
                    item.RelativePath,
                    fileInfo.Name,
                    fileInfo.Length,
                    new DateTimeOffset(fileInfo.LastWriteTimeUtc, TimeSpan.Zero));
            })
            .OrderBy(static item => item.RelativePath, StringComparer.Ordinal)];
    }

    public (string SessionId, string AssetId)? FindLatestAssetReferenceByFileName(string fileName, string? userId)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        string normalized = Path.GetFileName(fileName.Trim());
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT session_id, id, owner_user_id
            FROM conversation_assets
            WHERE lower(file_name) = lower($fileName)
            ORDER BY created_at DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$fileName", normalized);

        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        string? ownerUserId = reader.IsDBNull(2) ? null : reader.GetString(2);
        if (!string.IsNullOrWhiteSpace(ownerUserId) && !string.Equals(ownerUserId, userId, StringComparison.Ordinal))
        {
            return null;
        }

        return (reader.GetString(0), reader.GetString(1));
    }

    public void BindAssetsToMessage(string sessionId, string messageId, IReadOnlyList<string> assetIds, string? userId)
    {
        IReadOnlyList<StoredConversationAsset> assets = GetAssetsByIds(sessionId, assetIds, userId);
        if (assets.Count == 0)
        {
            return;
        }

        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        using SqliteTransaction transaction = connection.BeginTransaction();

        foreach (string assetId in assets.Select(static asset => asset.Public.Id))
        {
            using var deleteExisting = connection.CreateCommand();
            deleteExisting.Transaction = transaction;
            deleteExisting.CommandText = "DELETE FROM message_asset_refs WHERE asset_id = $assetId;";
            deleteExisting.Parameters.AddWithValue("$assetId", assetId);
            deleteExisting.ExecuteNonQuery();

            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO message_asset_refs (asset_id, session_id, message_id, created_at)
                VALUES ($assetId, $sessionId, $messageId, $createdAt);
                """;
            insert.Parameters.AddWithValue("$assetId", assetId);
            insert.Parameters.AddWithValue("$sessionId", sessionId);
            insert.Parameters.AddWithValue("$messageId", messageId);
            insert.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
            insert.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public bool TryOpenAsset(string sessionId, string assetId, string? userId, out AssetDownloadResult? result)
    {
        IReadOnlyList<StoredConversationAsset> assets = GetAssetsByIds(sessionId, [assetId], userId);
        StoredConversationAsset? asset = assets.Count > 0 ? assets[0] : null;
        if (asset is null)
        {
            result = null;
            return false;
        }

        string fullPath = ResolveStoredAssetFullPath(asset);
        if (!TryGetWorkspaceAttachmentsRoot(asset.SessionId, out string attachmentsRoot)
            || WorkspaceReparseGuard.ContainsReparsePoint(Path.GetDirectoryName(attachmentsRoot) ?? attachmentsRoot, fullPath))
        {
            result = null;
            return false;
        }

        if (!File.Exists(fullPath))
        {
            ConversationAssetStoreLog.AssetMissingOnDisk(logger, asset.Public.Id);
            result = null;
            return false;
        }

        FileStream? stream = null;
        try
        {
            stream = WorkspaceFile.OpenRead(fullPath);
            result = new AssetDownloadResult(
                asset.Public.FileName,
                asset.Public.ContentType,
                stream,
                string.Equals(asset.Public.Disposition, "download", StringComparison.OrdinalIgnoreCase));
            stream = null;
            return true;
        }
        catch (IOException)
        {
            result = null;
            return false;
        }
        finally
        {
            stream?.Dispose();
        }
    }

    public void DeleteSessionAssets(IReadOnlyList<string> sessionIds)
    {
        if (sessionIds.Count == 0)
        {
            return;
        }

        using var connection = new SqliteConnection(database.ConnectionString);
        connection.Open();
        using SqliteTransaction transaction = connection.BeginTransaction();

        using var deleteRefs = connection.CreateCommand();
        deleteRefs.Transaction = transaction;
        deleteRefs.CommandText =
            """
            DELETE FROM message_asset_refs
            WHERE session_id IN (SELECT value FROM json_each($sessionIdsJson));
            """;
        deleteRefs.Parameters.AddWithValue(sessionIdsJsonParameter, SerializeStringArray(sessionIds));
        deleteRefs.ExecuteNonQuery();

        using var deleteAssets = connection.CreateCommand();
        deleteAssets.Transaction = transaction;
        deleteAssets.CommandText =
            """
            DELETE FROM conversation_assets
            WHERE session_id IN (SELECT value FROM json_each($sessionIdsJson));
            """;
        deleteAssets.Parameters.AddWithValue(sessionIdsJsonParameter, SerializeStringArray(sessionIds));
        deleteAssets.ExecuteNonQuery();

        transaction.Commit();
    }

    private AssetStorageLocation ResolveStorageLocation(string sessionId, string fileName)
    {
        if (!TryGetWorkspaceAttachmentsRoot(sessionId, out string attachmentsRoot))
        {
            throw new InvalidOperationException($"Session '{sessionId}' is not backed by an agent workspace attachments root.");
        }

        if (WorkspaceReparseGuard.ContainsReparsePoint(
            Path.GetDirectoryName(attachmentsRoot) ?? attachmentsRoot,
            attachmentsRoot))
        {
            throw new IOException("Requested path is not accessible.");
        }

        string resolvedFileName = ResolveAvailableFileName(attachmentsRoot, fileName);
        return new AssetStorageLocation(
            resolvedFileName,
            $"{attachmentsFolderName}/{resolvedFileName}",
            Path.Combine(attachmentsRoot, resolvedFileName));
    }

    private string ResolveStoredAssetFullPath(StoredConversationAsset asset)
    {
        if (!asset.StoragePath.StartsWith($"{attachmentsFolderName}/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Asset '{asset.Public.Id}' has unsupported storage path '{asset.StoragePath}'.");
        }

        if (!TryGetWorkspaceAttachmentsRoot(asset.SessionId, out string attachmentsRoot))
        {
            throw new InvalidOperationException($"Session '{asset.SessionId}' is not backed by an agent workspace attachments root.");
        }

        string relativePath = asset.StoragePath[(attachmentsFolderName.Length + 1)..].Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(attachmentsRoot, relativePath);
    }

    private bool TryGetWorkspaceAttachmentsRoot(string sessionId, out string attachmentsRoot)
    {
        attachmentsRoot = string.Empty;

        if (!state.Sessions.TryGetValue(sessionId, out GatewaySession? session) || session is null)
        {
            return false;
        }

        if (!string.Equals(session.TargetType, "agent", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(session.TargetId))
        {
            return false;
        }

        WorkspaceRuntimeContext workspace = workspaceTool.DescribeAgentWorkspace(session.TargetId, session.OwnerUserId);
        if (string.IsNullOrWhiteSpace(workspace.HostWorkspacePath))
        {
            return false;
        }

        attachmentsRoot = Path.Combine(workspace.HostWorkspacePath, attachmentsFolderName);
        return true;
    }

    private bool TryGetAgentAttachmentsRoot(string agentId, out string attachmentsRoot)
    {
        attachmentsRoot = string.Empty;
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return false;
        }

        WorkspaceRuntimeContext workspace = workspaceTool.DescribeAgentWorkspace(agentId);
        if (string.IsNullOrWhiteSpace(workspace.HostWorkspacePath))
        {
            return false;
        }

        attachmentsRoot = Path.Combine(workspace.HostWorkspacePath, attachmentsFolderName);
        return true;
    }

    private static string ResolveAvailableFileName(string attachmentsRoot, string fileName)
    {
        string nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        string candidate = fileName;
        int suffix = 1;

        while (File.Exists(Path.Combine(attachmentsRoot, candidate)))
        {
            candidate = $"{nameWithoutExtension} ({suffix.ToString(CultureInfo.InvariantCulture)}){extension}";
            suffix++;
        }

        return candidate;
    }

    private static GatewayConversationAsset ToPublicAsset(ConversationAssetRecord asset)
        => new(
            asset.AssetId,
            asset.FileName,
            asset.ContentType,
            asset.SizeBytes,
            asset.Origin,
            asset.Disposition,
            $"/api/sessions/{Uri.EscapeDataString(asset.SessionId)}/assets/{Uri.EscapeDataString(asset.AssetId)}",
            asset.CreatedAt);

    private static string NormalizeOrigin(string origin)
    {
        if (string.Equals(origin, "userUpload", StringComparison.Ordinal)
            || string.Equals(origin, "clipboardPaste", StringComparison.Ordinal)
            || string.Equals(origin, "agentOutput", StringComparison.Ordinal))
        {
            return origin;
        }

        throw new InvalidOperationException($"Unsupported asset origin '{origin}'.");
    }

    private static string NormalizeDisposition(string disposition)
    {
        if (string.Equals(disposition, "inlinePreview", StringComparison.Ordinal)
            || string.Equals(disposition, "download", StringComparison.Ordinal))
        {
            return disposition;
        }

        throw new InvalidOperationException($"Unsupported asset disposition '{disposition}'.");
    }

    private static string SerializeStringArray(IReadOnlyList<string> values)
        => JsonSerializer.Serialize(values.ToArray(), KautukaJsonContext.Default.StringArray);

    private readonly record struct AssetStorageLocation(string FileName, string StoragePath, string FullPath);
    private readonly record struct StagedAssetUpload(string AssetId, string SessionId, string StoragePath);

    private readonly record struct ConversationAssetRecord(
        string AssetId,
        string SessionId,
        string FileName,
        string ContentType,
        long SizeBytes,
        string Origin,
        string Disposition,
        string CreatedAt);
}

internal static partial class ConversationAssetStoreLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Conversation asset file missing on disk for asset {AssetId}")]
    public static partial void AssetMissingOnDisk(ILogger logger, string assetId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Failed to delete conversation asset file {AssetPath}")]
    public static partial void AssetDeleteFailed(ILogger logger, Exception exception, string assetPath);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Failed to delete conversation asset staging record {AssetId}")]
    public static partial void StagingRecordDeleteFailed(ILogger logger, Exception exception, string assetId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Deferred interrupted asset upload reconciliation for asset {AssetId}; session {SessionId} is not currently resolvable")]
    public static partial void StagedAssetReconciliationDeferred(ILogger logger, string assetId, string sessionId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Warning, Message = "Could not determine whether conversation asset metadata committed for asset {AssetId}; retaining the staged file for restart reconciliation")]
    public static partial void AssetCommitResolutionFailed(ILogger logger, Exception exception, string assetId);
}
