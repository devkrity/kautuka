import { Button } from '@/components/ui/button';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';
import { PageState } from '@/components/layout/PageState';
import { usePackageCacheStats } from '../hooks/usePackageCacheStats';
import type { PkgsProxyCacheStats } from '../schemas/packageCacheStatsSchema';
import { apiError } from '../utils/settingsUtils';

function formatBytes(bytes: number): string {
  if (bytes < 1024) {
    return `${bytes} B`;
  }

  const units = ['KB', 'MB', 'GB', 'TB'];
  let value = bytes / 1024;
  let unitIndex = 0;
  while (value >= 1024 && unitIndex < units.length - 1) {
    value /= 1024;
    unitIndex += 1;
  }

  const digits = 1;
  return `${value.toFixed(digits)} ${units[unitIndex]}`;
}

function Stat({ label, value }: { label: string; value: string }) {
  return (
    <div className="rounded-lg border border-border/80 p-3">
      <div className="text-xs text-muted-foreground">{label}</div>
      <div className="mt-1 text-sm font-medium">{value}</div>
    </div>
  );
}

function CacheTables({ stats }: { stats: PkgsProxyCacheStats }) {
  const ecosystems = Object.entries(stats.ecosystems);

  return (
    <div className="space-y-4">
      <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-5">
        <Stat label="Packages" value={String(stats.packages)} />
        <Stat label="Versions" value={String(stats.versions)} />
        <Stat label="Artifacts" value={String(stats.artifacts)} />
        <Stat label="Cache size" value={formatBytes(stats.totalSizeBytes)} />
        <Stat label="Total hits" value={String(stats.totalHits)} />
      </div>

      {ecosystems.length > 0 ? (
        <div className="flex flex-wrap gap-2">
          {ecosystems.map(([ecosystem, count]) => (
            <span key={ecosystem} className="badge border-border bg-muted text-foreground">
              {ecosystem}: {count}
            </span>
          ))}
        </div>
      ) : null}

      {stats.popular.length > 0 ? (
        <div className="space-y-2">
          <div className="text-sm font-medium">Popular packages</div>
          <div className="max-h-[25.5rem] overflow-auto rounded-lg border border-border/80">
            <table className="w-full text-left text-sm">
              <thead className="sticky top-0 bg-muted text-xs text-muted-foreground">
                <tr>
                  <th className="px-3 py-2 font-medium">Ecosystem</th>
                  <th className="px-3 py-2 font-medium">Package</th>
                  <th className="px-3 py-2 font-medium">Hits</th>
                  <th className="px-3 py-2 font-medium">Size</th>
                </tr>
              </thead>
              <tbody>
                {stats.popular.slice(0, 10).map((pkg) => (
                  <tr key={`${pkg.ecosystem}:${pkg.name}`} className="border-t border-border/80">
                    <td className="px-3 py-2">{pkg.ecosystem}</td>
                    <td className="px-3 py-2 font-medium">{pkg.name}</td>
                    <td className="px-3 py-2">{pkg.hits}</td>
                    <td className="px-3 py-2">{formatBytes(pkg.sizeBytes)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      ) : null}

      {stats.recent.length > 0 ? (
        <div className="space-y-2">
          <div className="text-sm font-medium">Recently cached</div>
          <div className="max-h-[25.5rem] overflow-auto rounded-lg border border-border/80">
            <table className="w-full text-left text-sm">
              <thead className="sticky top-0 bg-muted text-xs text-muted-foreground">
                <tr>
                  <th className="px-3 py-2 font-medium">Ecosystem</th>
                  <th className="px-3 py-2 font-medium">Package</th>
                  <th className="px-3 py-2 font-medium">Cached at</th>
                  <th className="px-3 py-2 font-medium">Size</th>
                </tr>
              </thead>
              <tbody>
                {stats.recent.slice(0, 10).map((pkg, index) => (
                  <tr
                    key={`${pkg.ecosystem}:${pkg.name}:${pkg.version}:${pkg.cachedAt}:${index}`}
                    className="border-t border-border/80"
                  >
                    <td className="px-3 py-2">{pkg.ecosystem}</td>
                    <td className="px-3 py-2 font-medium">
                      {pkg.name}
                      {pkg.version ? `@${pkg.version}` : ''}
                    </td>
                    <td className="px-3 py-2">{pkg.cachedAt || '—'}</td>
                    <td className="px-3 py-2">{formatBytes(pkg.sizeBytes)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      ) : null}
    </div>
  );
}

export function PackageCacheStatsSection() {
  const { data, error, isFetching, refetch } = usePackageCacheStats();

  return (
    <Card className="card-solid">
      <CardHeader className="flex flex-col gap-3 sm:flex-row sm:items-start sm:justify-between">
        <div>
          <CardTitle>Package cache</CardTitle>
          <CardDescription>
            Live inventory from the unpublished npm/PyPI proxy. Nothing is stored in Kautuka; this
            reads sidecar stats when this page opens.
          </CardDescription>
        </div>
        <Button variant="outline" onClick={() => void refetch()} disabled={isFetching}>
          {isFetching ? 'Loading…' : 'Refresh'}
        </Button>
      </CardHeader>
      <CardContent className="space-y-4">
        {isFetching && !data ? (
          <PageState message="Reading package proxy cache stats…" minHeightClassName="min-h-24" />
        ) : null}
        {error ? <p className="text-sm text-destructive">{apiError(error)}</p> : null}
        {data && !data.ok ? <p className="text-sm text-destructive">{data.message}</p> : null}
        {data?.ok ? <CacheTables stats={data} /> : null}
      </CardContent>
    </Card>
  );
}
