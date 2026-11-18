import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { renderHook, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import type { ReactNode } from 'react';
import { apiClient } from '@/lib/apiClient';
import { useNetworkPendingCount, useNetworkRequests } from '@/features/network-access/networkAccess';

afterEach(() => vi.restoreAllMocks());

it('shares the requests response with the navigation badge', async () => {
  const get = vi.spyOn(apiClient, 'get').mockResolvedValue({
    data: { revision: 1, requests: [{ id: 'one', status: 'pending' }, { id: 'two', status: 'blocked' }], privileges: { canApproveOwn: true }, brake: { systemPaused: false } },
  });
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  const wrapper = ({ children }: { children: ReactNode }) => <QueryClientProvider client={client}>{children}</QueryClientProvider>;
  const { result, unmount } = renderHook(() => ({ page: useNetworkRequests(), badge: useNetworkPendingCount() }), { wrapper });
  await waitFor(() => expect(result.current.badge.data?.count).toBe(1));
  expect(result.current.page.data?.requests).toHaveLength(2);
  expect(get).toHaveBeenCalledTimes(1);
  unmount();
  client.clear();
});
