using System.Globalization;

using Dapper;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Infrastructure.ProviderPathPolicy;

internal sealed class SqliteProviderPathObservationStore(KautukaDatabase db) : IProviderPathObservationStore
{
    internal sealed record ObservationRow(
        string ProviderType,
        string EndpointKey,
        string ClientType,
        string ModelKey,
        int NativeToolSuccessCount,
        int NativeToolFailureCount,
        int StrictJsonSuccessCount,
        int RelaxedJsonSuccessCount,
        int JsonFailureCount,
        int ReasoningOnlyCount,
        int VisionSuccessCount,
        int VisionFailureCount,
        string? FirstObservedAt,
        string LastObservedAt);

    internal sealed record EffectiveModeRow(
        string ToolMode,
        string JsonMode,
        string VisionMode,
        string ReasoningMode,
        string ResolvedAt);

    public async ValueTask<ProviderPathObservation?> GetAsync(ProviderPathKey key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        using var conn = new SqliteConnection(db.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        var row = await conn.QueryFirstOrDefaultAsync<ObservationRow>(
            """
            SELECT providerType AS ProviderType, endpointKey AS EndpointKey,
                   clientType AS ClientType, modelKey AS ModelKey,
                   nativeToolSuccessCount AS NativeToolSuccessCount,
                   nativeToolFailureCount AS NativeToolFailureCount,
                   strictJsonSuccessCount AS StrictJsonSuccessCount,
                   relaxedJsonSuccessCount AS RelaxedJsonSuccessCount,
                   jsonFailureCount AS JsonFailureCount,
                   reasoningOnlyCount AS ReasoningOnlyCount,
                   visionSuccessCount AS VisionSuccessCount,
                   visionFailureCount AS VisionFailureCount,
                   firstObservedAt AS FirstObservedAt,
                   lastObservedAt AS LastObservedAt
                 FROM provider_path_observations
            WHERE pathKey = @Key
            """,
            new { Key = key.ToStoreKey() }).ConfigureAwait(false);

        return row is null ? null : MapObservation(row, key);
    }

    public async ValueTask<IReadOnlyList<ProviderPathObservation>> ListAsync(CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(db.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        var rows = await conn.QueryAsync<ObservationRow>(
            """
            SELECT providerType AS ProviderType, endpointKey AS EndpointKey,
                   clientType AS ClientType, modelKey AS ModelKey,
                   nativeToolSuccessCount AS NativeToolSuccessCount,
                   nativeToolFailureCount AS NativeToolFailureCount,
                   strictJsonSuccessCount AS StrictJsonSuccessCount,
                   relaxedJsonSuccessCount AS RelaxedJsonSuccessCount,
                   jsonFailureCount AS JsonFailureCount,
                   reasoningOnlyCount AS ReasoningOnlyCount,
                   visionSuccessCount AS VisionSuccessCount,
                   visionFailureCount AS VisionFailureCount,
                   firstObservedAt AS FirstObservedAt,
                   lastObservedAt AS LastObservedAt
              FROM provider_path_observations
             ORDER BY lastObservedAt DESC
            """).ConfigureAwait(false);

        return rows
            .Select(r => MapObservation(r, new ProviderPathKey(r.ProviderType, r.EndpointKey, r.ClientType, r.ModelKey)))
            .ToList();
    }

    public async ValueTask<ProviderPathEffectiveMode?> GetEffectiveModeAsync(
        ProviderPathKey key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        using var conn = new SqliteConnection(db.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        var row = await conn.QueryFirstOrDefaultAsync<EffectiveModeRow>(
            """
            SELECT toolMode AS ToolMode, jsonMode AS JsonMode,
                   visionMode AS VisionMode, reasoningMode AS ReasoningMode,
                 resolved_at AS ResolvedAt
             FROM provider_path_effective_modes
            WHERE pathKey = @Key
            """,
            new { Key = key.ToStoreKey() }).ConfigureAwait(false);

        if (row is null) return null;

        return new ProviderPathEffectiveMode
        {
            Key = key,
            ToolMode = Enum.Parse<ToolCompatibilityMode>(row.ToolMode),
            JsonMode = Enum.Parse<JsonCompatibilityMode>(row.JsonMode),
            VisionMode = Enum.Parse<VisionCompatibilityMode>(row.VisionMode),
            ReasoningMode = Enum.Parse<ReasoningCompatibilityMode>(row.ReasoningMode),
            ResolvedAt = DateTimeOffset.Parse(row.ResolvedAt, CultureInfo.InvariantCulture),
        };
    }

    public async ValueTask SaveEffectiveModeAsync(ProviderPathEffectiveMode mode, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mode);

        using var conn = new SqliteConnection(db.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await conn.ExecuteAsync(
            """
            INSERT INTO provider_path_effective_modes
                (pathKey, toolMode, jsonMode, visionMode, reasoningMode, resolved_at)
            VALUES
                (@Key, @ToolMode, @JsonMode, @VisionMode, @ReasoningMode, @ResolvedAt)
            ON CONFLICT(pathKey) DO UPDATE SET
                toolMode      = excluded.toolMode,
                jsonMode      = excluded.jsonMode,
                visionMode    = excluded.visionMode,
                reasoningMode = excluded.reasoningMode,
                resolved_at   = excluded.resolved_at
            """,
            new
            {
                Key = mode.Key.ToStoreKey(),
                ToolMode = mode.ToolMode.ToString(),
                JsonMode = mode.JsonMode.ToString(),
                VisionMode = mode.VisionMode.ToString(),
                ReasoningMode = mode.ReasoningMode.ToString(),
                ResolvedAt = mode.ResolvedAt.ToString("O"),
            }).ConfigureAwait(false);
    }

    public ValueTask RecordNativeToolSuccessAsync(ProviderPathKey key, CancellationToken ct = default) =>
        IncrementAsync(key, "nativeToolSuccessCount", ct);

    public ValueTask RecordNativeToolFailureAsync(ProviderPathKey key, CancellationToken ct = default) =>
        IncrementAsync(key, "nativeToolFailureCount", ct);

    public ValueTask RecordStrictJsonSuccessAsync(ProviderPathKey key, CancellationToken ct = default) =>
        IncrementAsync(key, "strictJsonSuccessCount", ct);

    public ValueTask RecordRelaxedJsonSuccessAsync(ProviderPathKey key, CancellationToken ct = default) =>
        IncrementAsync(key, "relaxedJsonSuccessCount", ct);

    public ValueTask RecordJsonFailureAsync(ProviderPathKey key, CancellationToken ct = default) =>
        IncrementAsync(key, "jsonFailureCount", ct);

    public ValueTask RecordReasoningOnlyAsync(ProviderPathKey key, CancellationToken ct = default) =>
        IncrementAsync(key, "reasoningOnlyCount", ct);

    public ValueTask RecordVisionSuccessAsync(ProviderPathKey key, CancellationToken ct = default) =>
        IncrementAsync(key, "visionSuccessCount", ct);

    public ValueTask RecordVisionFailureAsync(ProviderPathKey key, CancellationToken ct = default) =>
        IncrementAsync(key, "visionFailureCount", ct);

    // ── helpers ──────────────────────────────────────────────────────────────

    private async ValueTask IncrementAsync(ProviderPathKey key, string column, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);

        string now = DateTimeOffset.UtcNow.ToString("O");

        using var conn = new SqliteConnection(db.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        await conn.ExecuteAsync(
            $"""
            INSERT INTO provider_path_observations
                (pathKey, providerType, endpointKey, clientType, modelKey,
                 {column}, firstObservedAt, lastObservedAt)
            VALUES
                (@Key, @ProviderType, @EndpointKey, @ClientType, @ModelKey,
                 1, @Now, @Now)
            ON CONFLICT(pathKey) DO UPDATE SET
                {column}         = {column} + 1,
                lastObservedAt = @Now
            """,
            new
            {
                Key = key.ToStoreKey(),
                ProviderType = key.ProviderType,
                EndpointKey = key.EndpointKey,
                ClientType = key.ClientType,
                ModelKey = key.ModelKey,
                Now = now,
            }).ConfigureAwait(false);
    }

    private static ProviderPathObservation MapObservation(ObservationRow row, ProviderPathKey key) =>
        new()
        {
            Key = key,
            NativeToolSuccessCount = row.NativeToolSuccessCount,
            NativeToolFailureCount = row.NativeToolFailureCount,
            StrictJsonSuccessCount = row.StrictJsonSuccessCount,
            RelaxedJsonSuccessCount = row.RelaxedJsonSuccessCount,
            JsonFailureCount = row.JsonFailureCount,
            ReasoningOnlyCount = row.ReasoningOnlyCount,
            VisionSuccessCount = row.VisionSuccessCount,
            VisionFailureCount = row.VisionFailureCount,
            FirstObservedAt = row.FirstObservedAt is null ? null : DateTimeOffset.Parse(row.FirstObservedAt, CultureInfo.InvariantCulture),
            LastObservedAt = DateTimeOffset.Parse(row.LastObservedAt, CultureInfo.InvariantCulture),
        };
}
