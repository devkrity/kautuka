using System.Globalization;

using Kautuka.Gateway.Domain.Assets;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Infrastructure.Assets;

internal sealed class SqliteParsedAssetStore(
    KautukaDatabase database) : IParsedAssetStore
{
    public async Task<ParsedAsset> StoreAsync(ParsedAssetCreationCommand command, CancellationToken cancellationToken)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();

        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        cmd.CommandText =
            """
            INSERT OR REPLACE INTO parsed_artifacts 
            (asset_id, session_id, status, parser_name, parser_version, normalized_markdown, metadata_json, warning, error, created_at, updated_at)
            VALUES ($assetId, $sessionId, $status, $parserName, $parserVersion, $normalizedMarkdown, $metadataJson, $warning, $error, $createdAt, $updatedAt);
            """;

        cmd.Parameters.AddWithValue("$assetId", command.AssetId);
        cmd.Parameters.AddWithValue("$sessionId", command.SessionId);
        cmd.Parameters.AddWithValue("$status", (int)command.Status);
        cmd.Parameters.AddWithValue("$parserName", command.ParserName);
        cmd.Parameters.AddWithValue("$parserVersion", command.ParserVersion);
        cmd.Parameters.AddWithValue("$normalizedMarkdown", (object?)command.NormalizedMarkdown ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$metadataJson", (object?)command.MetadataJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$warning", (object?)command.Warning ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$error", (object?)command.Error ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$createdAt", now);
        cmd.Parameters.AddWithValue("$updatedAt", now);

        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        return new ParsedAsset(
            command.AssetId,
            command.SessionId,
            command.Status,
            command.ParserName,
            command.ParserVersion,
            command.NormalizedMarkdown,
            command.MetadataJson,
            command.Warning,
            command.Error,
            now,
            now);
    }

    public async Task<ParsedAsset?> GetAsync(string sessionId, string assetId, CancellationToken cancellationToken)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();

        cmd.CommandText =
            """
            SELECT asset_id, session_id, status, parser_name, parser_version, normalized_markdown, metadata_json, warning, error, created_at, updated_at
            FROM parsed_artifacts
            WHERE asset_id = $assetId AND session_id = $sessionId
            LIMIT 1;
            """;

        cmd.Parameters.AddWithValue("$assetId", assetId);
        cmd.Parameters.AddWithValue("$sessionId", sessionId);

        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadParsedAsset(reader);
    }

    public async Task<IReadOnlyList<ParsedAsset>> GetBySessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();

        cmd.CommandText =
            """
            SELECT asset_id, session_id, status, parser_name, parser_version, normalized_markdown, metadata_json, warning, error, created_at, updated_at
            FROM parsed_artifacts
            WHERE session_id = $sessionId
            ORDER BY created_at DESC;
            """;

        cmd.Parameters.AddWithValue("$sessionId", sessionId);

        var results = new List<ParsedAsset>();
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadParsedAsset(reader));
        }
        return results;
    }

    public async Task DeleteBySessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        using var connection = new SqliteConnection(database.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();

        cmd.CommandText = "DELETE FROM parsed_artifacts WHERE session_id = $sessionId;";
        cmd.Parameters.AddWithValue("$sessionId", sessionId);

        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ParsedAsset ReadParsedAsset(SqliteDataReader reader)
    {
        return new ParsedAsset(
            AssetId: reader.GetString(0),
            SessionId: reader.GetString(1),
            Status: (ParsedAssetStatus)reader.GetInt32(2),
            ParserName: reader.GetString(3),
            ParserVersion: reader.GetString(4),
            NormalizedMarkdown: reader.IsDBNull(5) ? null : reader.GetString(5),
            MetadataJson: reader.IsDBNull(6) ? null : reader.GetString(6),
            Warning: reader.IsDBNull(7) ? null : reader.GetString(7),
            Error: reader.IsDBNull(8) ? null : reader.GetString(8),
            CreatedAt: reader.GetString(9),
            UpdatedAt: reader.GetString(10));
    }
}
