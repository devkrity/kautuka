import React from 'react';
import { render } from '@testing-library/react';
import { screen } from '@testing-library/dom';
import userEvent from '@testing-library/user-event';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { NetworkAudit } from '@/features/network-access/NetworkAudit';

const { get } = vi.hoisted(() => ({ get: vi.fn() }));
vi.mock('@/lib/apiClient', () => ({ apiClient: { get } }));
const row = (id: string, destination: string) => ({
  sequence: Number(id),
  id,
  startedAtUtc: '2026-11-07T00:00:00Z',
  observedAtUtc: '2026-11-07T00:01:00Z',
  identity: null,
  containerId: null,
  sourceAddress: '10.89.1.10:50000',
  destination,
  port: 443,
  outcome: 'denied',
  connected: false,
  closed: true,
  bytesSent: 42,
  bytesReceived: 64,
  durationMilliseconds: 123,
});
const renderAudit = () =>
  render(
    <QueryClientProvider
      client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}
    >
      <NetworkAudit />
    </QueryClientProvider>
  );

describe('NetworkAudit', () => {
  beforeEach(() => vi.resetAllMocks());
  it('shows bounded-history limitations and known coverage gaps without inventing source identity', async () => {
    get.mockResolvedValue({
      data: {
        events: [row('2', 'blocked.example.com')],
        nextBefore: 0,
        coverageGaps: 1,
        unattributedConnections: 1,
        lastError: null,
      },
    });
    renderAudit();
    expect(await screen.findByText('Blocked · blocked.example.com:443')).toBeInTheDocument();
    expect(screen.getByText('Unattributed source')).toBeInTheDocument();
    expect(screen.getByText(/Some connection records are missing/)).toBeInTheDocument();
    expect(screen.getByText(/64 bytes from component/)).toBeInTheDocument();
    expect(screen.getByText(/downtime or log rotation can omit records/)).toBeInTheDocument();
  });
  it('loads older rows and preserves the newest copy of a correlated connection', async () => {
    get.mockImplementation(async (_path: string, options: { params: { before: number } }) => ({
      data: {
        events:
          options.params.before === 2
            ? [row('2', 'stale.example.com'), row('1', 'older.example.com')]
            : [row('2', 'newest.example.com')],
        nextBefore: options.params.before === 2 ? 0 : 2,
        coverageGaps: 0,
        unattributedConnections: 0,
        lastError: null,
      },
    }));
    renderAudit();
    await userEvent.click(await screen.findByRole('button', { name: 'Load older connections' }));
    expect(await screen.findByText('Blocked · older.example.com:443')).toBeInTheDocument();
    expect(screen.getByText('Blocked · newest.example.com:443')).toBeInTheDocument();
    expect(screen.queryByText('Blocked · stale.example.com:443')).not.toBeInTheDocument();
  });
});
