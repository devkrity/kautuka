using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kautuka.Manager.RuntimeContainer.Hosting;

public sealed record PkgsProxyPopularPackageSnapshot(
    string Ecosystem,
    string Name,
    long Hits,
    long SizeBytes);

public sealed record PkgsProxyRecentPackageSnapshot(
    string Ecosystem,
    string Name,
    string Version,
    string CachedAt,
    long SizeBytes);

public sealed record PkgsProxyCacheStatsSnapshot(
    long Packages,
    long Versions,
    long Artifacts,
    long TotalSizeBytes,
    long TotalHits,
    IReadOnlyDictionary<string, long> Ecosystems,
    IReadOnlyList<PkgsProxyPopularPackageSnapshot> Popular,
    IReadOnlyList<PkgsProxyRecentPackageSnapshot> Recent);

public sealed record PkgsProxyCacheStatsResult(
    bool Ok,
    string Message,
    PkgsProxyCacheStatsSnapshot? Stats);

public static class PkgsProxyCacheStatsParser
{
    public const int PackageListLimit = 10;
    public static bool TryParse(
        string? stdout,
        [NotNullWhen(true)] out PkgsProxyCacheStatsSnapshot? stats,
        [NotNullWhen(false)] out string? error)
    {
        stats = null;
        error = null;

        if (string.IsNullOrWhiteSpace(stdout))
        {
            error = "Package proxy cache stats were empty.";
            return false;
        }

        int jsonStart = stdout.AsSpan().IndexOf('{');
        if (jsonStart < 0)
        {
            error = "Package proxy cache stats were not valid JSON.";
            return false;
        }

        try
        {
            PkgsProxyCliStatsDocument? document = JsonSerializer.Deserialize(
                stdout.AsSpan(jsonStart),
                PkgsProxyCacheStatsJsonContext.Default.PkgsProxyCliStatsDocument);
            if (document is null)
            {
                error = "Package proxy cache stats were not valid JSON.";
                return false;
            }

            stats = new PkgsProxyCacheStatsSnapshot(
                document.Packages,
                document.Versions,
                document.Artifacts,
                document.TotalSizeBytes,
                document.TotalHits,
                NormalizeEcosystems(document.Ecosystems),
                NormalizePopular(document.Popular),
                NormalizeRecent(document.Recent));
            return true;
        }
        catch (JsonException)
        {
            error = "Package proxy cache stats were not valid JSON.";
            return false;
        }
    }

    private static SortedDictionary<string, long> NormalizeEcosystems(Dictionary<string, long>? ecosystems)
    {
        var normalized = new SortedDictionary<string, long>(StringComparer.Ordinal);
        if (ecosystems is null || ecosystems.Count == 0)
        {
            return normalized;
        }

        foreach ((string key, long value) in ecosystems)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            normalized[key.Trim()] = value;
        }

        return normalized;
    }

    private static IReadOnlyList<PkgsProxyPopularPackageSnapshot> NormalizePopular(
        List<PkgsProxyCliPopularPackage>? packages)
    {
        if (packages is null || packages.Count == 0)
        {
            return [];
        }

        return
        [
            .. packages
                .Where(static package => !string.IsNullOrWhiteSpace(package.Name))
                .Select(static package => new PkgsProxyPopularPackageSnapshot(
                    package.Ecosystem?.Trim() ?? string.Empty,
                    package.Name.Trim(),
                    package.Hits,
                    package.SizeBytes))
                .Take(PackageListLimit)
        ];
    }

    private static IReadOnlyList<PkgsProxyRecentPackageSnapshot> NormalizeRecent(
        List<PkgsProxyCliRecentPackage>? packages)
    {
        if (packages is null || packages.Count == 0)
        {
            return [];
        }

        return
        [
            .. packages
                .Where(static package => !string.IsNullOrWhiteSpace(package.Name))
                .Select(static package => new PkgsProxyRecentPackageSnapshot(
                    package.Ecosystem?.Trim() ?? string.Empty,
                    package.Name.Trim(),
                    package.Version?.Trim() ?? string.Empty,
                    package.CachedAt?.Trim() ?? string.Empty,
                    package.SizeBytes))
                .Take(PackageListLimit)
        ];
    }
}

internal sealed class PkgsProxyCliStatsDocument
{
    [JsonPropertyName("packages")]
    public long Packages { get; set; }

    [JsonPropertyName("versions")]
    public long Versions { get; set; }

    [JsonPropertyName("artifacts")]
    public long Artifacts { get; set; }

    [JsonPropertyName("total_size_bytes")]
    public long TotalSizeBytes { get; set; }

    [JsonPropertyName("total_hits")]
    public long TotalHits { get; set; }

    [JsonPropertyName("ecosystems")]
    public Dictionary<string, long>? Ecosystems { get; set; }

    [JsonPropertyName("popular")]
    public List<PkgsProxyCliPopularPackage>? Popular { get; set; }

    [JsonPropertyName("recent")]
    public List<PkgsProxyCliRecentPackage>? Recent { get; set; }
}

internal sealed class PkgsProxyCliPopularPackage
{
    [JsonPropertyName("ecosystem")]
    public string? Ecosystem { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("hits")]
    public long Hits { get; set; }

    [JsonPropertyName("size_bytes")]
    public long SizeBytes { get; set; }
}

internal sealed class PkgsProxyCliRecentPackage
{
    [JsonPropertyName("ecosystem")]
    public string? Ecosystem { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("cached_at")]
    public string? CachedAt { get; set; }

    [JsonPropertyName("size_bytes")]
    public long SizeBytes { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(PkgsProxyCliStatsDocument))]
[JsonSerializable(typeof(PkgsProxyCliPopularPackage))]
[JsonSerializable(typeof(PkgsProxyCliRecentPackage))]
[JsonSerializable(typeof(List<PkgsProxyCliPopularPackage>))]
[JsonSerializable(typeof(List<PkgsProxyCliRecentPackage>))]
[JsonSerializable(typeof(Dictionary<string, long>))]
internal sealed partial class PkgsProxyCacheStatsJsonContext : JsonSerializerContext;
