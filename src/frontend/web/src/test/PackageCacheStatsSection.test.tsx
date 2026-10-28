/**
 * @vitest-environment jsdom
 */
import { PackageCacheStatsSection } from '@/features/settings/components/PackageCacheStatsSection';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const { fetchPkgsProxyCacheStatsMock } = vi.hoisted(() => ({
  fetchPkgsProxyCacheStatsMock: vi.fn(),
}));

vi.mock('@/features/settings/api/packageCacheStats', () => ({
  fetchPkgsProxyCacheStats: fetchPkgsProxyCacheStatsMock,
}));

const liveStats = {
  ok: true,
  message: 'Package proxy cache stats.',
  packages: 86,
  versions: 87,
  artifacts: 165,
  totalSizeBytes: 109554665,
  totalHits: 1012,
  ecosystems: { npm: 1, pypi: 85 },
  popular: [{ ecosystem: 'pypi', name: 'uv', hits: 30, sizeBytes: 24884515 }],
  recent: [
    {
      ecosystem: 'npm',
      name: 'ms',
      version: '2.1.3',
      cachedAt: '2026-09-18 13:36:26',
      sizeBytes: 2967,
    },
  ],
};

function renderSection() {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(
    <QueryClientProvider client={queryClient}>
      <PackageCacheStatsSection />
    </QueryClientProvider>
  );
}

describe('PackageCacheStatsSection', () => {
  beforeEach(() => {
    fetchPkgsProxyCacheStatsMock.mockReset();
  });

  it('fetches live package proxy inventory when the page opens', async () => {
    fetchPkgsProxyCacheStatsMock.mockResolvedValue(liveStats);

    renderSection();

    expect(await screen.findByText('Packages')).toBeInTheDocument();
    expect(screen.getByText('86')).toBeInTheDocument();
    expect(screen.getByText('104.5 MB')).toBeInTheDocument();
    expect(screen.getByText((_, node) => node?.textContent === 'pypi: 85')).toBeInTheDocument();
    expect(screen.getByText('uv')).toBeInTheDocument();
    expect(screen.getByText((_, node) => node?.textContent === 'ms@2.1.3')).toBeInTheDocument();
    expect(fetchPkgsProxyCacheStatsMock).toHaveBeenCalledTimes(1);
    expect(screen.getByRole('button', { name: 'Refresh' })).toBeInTheDocument();
  });

  it('keeps popular and recent tables to ten rows with a bounded scroll area', async () => {
    fetchPkgsProxyCacheStatsMock.mockResolvedValue({
      ...liveStats,
      popular: Array.from({ length: 12 }, (_, index) => ({
        ecosystem: 'pypi',
        name: `pkg-${index + 1}`,
        hits: index + 1,
        sizeBytes: 1024,
      })),
      recent: Array.from({ length: 12 }, (_, index) => ({
        ecosystem: 'pypi',
        name: `recent-${index + 1}`,
        version: '1.0.0',
        cachedAt: '2026-09-18 13:36:26',
        sizeBytes: 1024,
      })),
    });

    renderSection();

    expect(await screen.findByText('pkg-1')).toBeInTheDocument();
    expect(screen.getByText('pkg-10')).toBeInTheDocument();
    expect(screen.queryByText('pkg-11')).not.toBeInTheDocument();
    expect(screen.getByText((_, node) => node?.textContent === 'recent-10@1.0.0')).toBeInTheDocument();
    expect(screen.queryByText((_, node) => node?.textContent === 'recent-11@1.0.0')).not.toBeInTheDocument();

    const tables = screen.getAllByRole('table');
    expect(tables).toHaveLength(2);
    for (const table of tables) {
      expect(table.parentElement).toHaveClass('overflow-auto');
      expect(table.parentElement?.className).toContain('max-h-[25.5rem]');
    }
  });

  it('refetches sidecar stats from the refresh button', async () => {
    fetchPkgsProxyCacheStatsMock.mockResolvedValue(liveStats);

    const user = userEvent.setup();
    renderSection();
    expect(await screen.findByText('Packages')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Refresh' }));

    expect(fetchPkgsProxyCacheStatsMock).toHaveBeenCalledTimes(2);
  });

  it('shows the sidecar failure message when stats are unavailable', async () => {
    fetchPkgsProxyCacheStatsMock.mockResolvedValue({
      ok: false,
      message: 'Package proxy sidecar is not in the catalog.',
      packages: 0,
      versions: 0,
      artifacts: 0,
      totalSizeBytes: 0,
      totalHits: 0,
      ecosystems: {},
      popular: [],
      recent: [],
    });

    renderSection();

    expect(
      await screen.findByText('Package proxy sidecar is not in the catalog.')
    ).toBeInTheDocument();
    expect(screen.queryByText('Packages')).not.toBeInTheDocument();
  });
});
