using System.Globalization;
using System.Text.Json;

using Dapper;

using Microsoft.Data.Sqlite;

namespace Kautuka.Gateway.Infrastructure.ModelCapability;

internal sealed class SqliteModelCapabilityStore(KautukaDatabase db) : IModelCapabilityStore
{
    private static readonly KautukaJsonContext jsonContext = KautukaJsonContext.Default;

    internal sealed record SaveProfileParameters(
        string Key,
        string ProviderId,
        string BaseUrl,
        string ModelId,
        string? ModelDigest,
        int ProbeSchemaVersion,
        string DeclaredJson,
        string ProbedJson,
        string ObservedJson,
        string? ProbedAt,
        string CreatedAt,
        string UpdatedAt);

    internal sealed record CapabilityRow(
        string DeclaredJson,
        string ProbedJson,
        string ObservedJson,
        string? ProbedAt,
        string CreatedAt,
        string UpdatedAt);

    public async ValueTask<ModelCapabilityProfile?> TryGetAsync(
        ModelCapabilityKey key, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(key);

        using var conn = new SqliteConnection(db.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        var row = await conn.QueryFirstOrDefaultAsync<CapabilityRow>(
            """
            SELECT declaredJson AS DeclaredJson, probedJson AS ProbedJson,
                   observedJson AS ObservedJson, probedAt AS ProbedAt,
                 created_at AS CreatedAt, updated_at AS UpdatedAt
             FROM model_capability_profiles
            WHERE profileKey = @Key
            """,
            new { Key = key.ToStoreKey() }).ConfigureAwait(false);

        return row is null ? null : Deserialize(row, key);
    }

    public async ValueTask SaveAsync(ModelCapabilityProfile profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        using var conn = new SqliteConnection(db.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        string? probedAt = profile.Probed.ProbedAt?.ToString("O");

        await conn.ExecuteAsync(
            """
            INSERT INTO model_capability_profiles
                (profileKey, provider_id, baseUrl, model_id, modelDigest, probeSchemaVersion,
                 declaredJson, probedJson, observedJson, probedAt, created_at, updated_at)
            VALUES
                (@Key, @ProviderId, @BaseUrl, @ModelId, @ModelDigest, @ProbeSchemaVersion,
                 @DeclaredJson, @ProbedJson, @ObservedJson, @ProbedAt, @CreatedAt, @UpdatedAt)
            ON CONFLICT(profileKey) DO UPDATE SET
                declaredJson       = excluded.declaredJson,
                probedJson         = excluded.probedJson,
                observedJson       = excluded.observedJson,
                probedAt           = excluded.probedAt,
                updated_at         = excluded.updated_at
            """,
            new SaveProfileParameters(
                profile.Key.ToStoreKey(),
                profile.Key.ProviderId,
                profile.Key.BaseUrl.AbsoluteUri,
                profile.Key.ModelId,
                profile.Key.ModelDigest,
                profile.Key.ProbeSchemaVersion,
                JsonSerializer.Serialize(profile.Declared, jsonContext.DeclaredCapabilities),
                JsonSerializer.Serialize(profile.Probed, jsonContext.ProbedCapabilities),
                JsonSerializer.Serialize(profile.Observed, jsonContext.ObservedBehaviors),
                probedAt,
                profile.CreatedAt.ToString("O"),
                profile.UpdatedAt.ToString("O"))).ConfigureAwait(false);
    }

    private static ModelCapabilityProfile Deserialize(CapabilityRow row, ModelCapabilityKey key) =>
        new()
        {
            Key = key,
            Declared = JsonSerializer.Deserialize(row.DeclaredJson, jsonContext.DeclaredCapabilities)
                       ?? new DeclaredCapabilities(),
            Probed = JsonSerializer.Deserialize(row.ProbedJson, jsonContext.ProbedCapabilities)
                       ?? new ProbedCapabilities(),
            Observed = JsonSerializer.Deserialize(row.ObservedJson, jsonContext.ObservedBehaviors)
                       ?? new ObservedBehaviors(),
            CreatedAt = DateTimeOffset.Parse(row.CreatedAt, CultureInfo.InvariantCulture),
            UpdatedAt = DateTimeOffset.Parse(row.UpdatedAt, CultureInfo.InvariantCulture),
        };
}
