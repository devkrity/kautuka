using Kautuka.Manager.RuntimeContainer.Configuration;
using Kautuka.Manager.RuntimeContainer.Hosting;

using Microsoft.Extensions.Logging.Abstractions;

namespace Kautuka.IntegrationTests;

public sealed class PkgsProxyCacheStatsParserTests
{
    [Fact]
    public void ParseReadsLiveCliJsonShape()
    {
        const string json = """
            {
              "packages": 86,
              "versions": 87,
              "artifacts": 165,
              "total_size_bytes": 109554665,
              "total_hits": 1012,
              "ecosystems": {
                "npm": 1,
                "pypi": 85
              },
              "popular": [
                {
                  "ecosystem": "pypi",
                  "name": "uv",
                  "hits": 30,
                  "size_bytes": 24884515
                }
              ],
              "recent": [
                {
                  "ecosystem": "npm",
                  "name": "ms",
                  "version": "2.1.3",
                  "cached_at": "2026-09-18 13:36:26",
                  "size_bytes": 2967
                }
              ]
            }
            """;

        Assert.True(PkgsProxyCacheStatsParser.TryParse(json, out PkgsProxyCacheStatsSnapshot? stats, out string? error));
        Assert.Null(error);
        Assert.Equal(86, stats.Packages);
        Assert.Equal(87, stats.Versions);
        Assert.Equal(165, stats.Artifacts);
        Assert.Equal(109554665, stats.TotalSizeBytes);
        Assert.Equal(1012, stats.TotalHits);
        Assert.Equal(1, stats.Ecosystems["npm"]);
        Assert.Equal(85, stats.Ecosystems["pypi"]);
        PkgsProxyPopularPackageSnapshot popular = Assert.Single(stats.Popular);
        Assert.Equal("pypi", popular.Ecosystem);
        Assert.Equal("uv", popular.Name);
        Assert.Equal(30, popular.Hits);
        Assert.Equal(24884515, popular.SizeBytes);
        PkgsProxyRecentPackageSnapshot recent = Assert.Single(stats.Recent);
        Assert.Equal("npm", recent.Ecosystem);
        Assert.Equal("ms", recent.Name);
        Assert.Equal("2.1.3", recent.Version);
        Assert.Equal("2026-09-18 13:36:26", recent.CachedAt);
        Assert.Equal(2967, recent.SizeBytes);
    }

    [Fact]
    public void ParseSkipsLeadingNoiseAndDropsUnnamedPackages()
    {
        const string stdout = """
            waiting for sqlite
            {
              "packages": 1,
              "versions": 1,
              "artifacts": 1,
              "total_size_bytes": 10,
              "total_hits": 2,
              "ecosystems": { "": 4, "pypi": 1 },
              "popular": [ { "ecosystem": "pypi", "name": " ", "hits": 1, "size_bytes": 1 }, { "ecosystem": "pypi", "name": "uv", "hits": 2, "size_bytes": 3 } ],
              "recent": []
            }
            """;

        Assert.True(PkgsProxyCacheStatsParser.TryParse(stdout, out PkgsProxyCacheStatsSnapshot? stats, out _));
        Assert.Equal(["pypi"], stats.Ecosystems.Keys);
        PkgsProxyPopularPackageSnapshot popular = Assert.Single(stats.Popular);
        Assert.Equal("uv", popular.Name);
        Assert.Empty(stats.Recent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseRejectsEmptyStdout(string? stdout)
    {
        Assert.False(PkgsProxyCacheStatsParser.TryParse(stdout, out _, out string? error));
        Assert.Equal("Package proxy cache stats were empty.", error);
    }

    [Fact]
    public void ParseRejectsNonJsonStdout()
    {
        Assert.False(PkgsProxyCacheStatsParser.TryParse("proxy: not ready", out _, out string? error));
        Assert.Equal("Package proxy cache stats were not valid JSON.", error);
    }

    [Fact]
    public void ParseCapsPopularAndRecentLists()
    {
        string popular = string.Join(
            ",",
            Enumerable.Range(1, 12).Select(static i =>
                $$"""{"ecosystem":"pypi","name":"pkg-{{i}}","hits":{{i}},"size_bytes":{{i}}}"""));
        string recent = string.Join(
            ",",
            Enumerable.Range(1, 12).Select(static i =>
                $$"""{"ecosystem":"pypi","name":"pkg-{{i}}","version":"1.0.{{i}}","cached_at":"2026-09-18 13:36:26","size_bytes":{{i}}}"""));
        string json = $$"""
            {
              "packages": 12,
              "versions": 12,
              "artifacts": 12,
              "total_size_bytes": 12,
              "total_hits": 12,
              "ecosystems": { "pypi": 12 },
              "popular": [{{popular}}],
              "recent": [{{recent}}]
            }
            """;

        Assert.True(PkgsProxyCacheStatsParser.TryParse(json, out PkgsProxyCacheStatsSnapshot? stats, out _));
        Assert.Equal(PkgsProxyCacheStatsParser.PackageListLimit, stats.Popular.Count);
        Assert.Equal(PkgsProxyCacheStatsParser.PackageListLimit, stats.Recent.Count);
        Assert.Equal("pkg-1", stats.Popular[0].Name);
        Assert.Equal("pkg-10", stats.Popular[^1].Name);
        Assert.Equal("pkg-10", stats.Recent[^1].Name);
    }

    [Fact]
    public async Task DiagnosticsFailWhenSidecarIsMissingFromCatalog()
    {
        var service = new PkgsProxyCacheDiagnosticsService(
            new PodmanCommandRunner(),
            WorkspaceSidecarCatalog.Empty,
            NullLogger<PkgsProxyCacheDiagnosticsService>.Instance);

        PkgsProxyCacheStatsResult result = await service.GetCacheStatsAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

        Assert.False(result.Ok);
        Assert.Equal("Package proxy sidecar is not in the catalog.", result.Message);
        Assert.Null(result.Stats);
    }
}
