import { renderHook, waitFor } from '@testing-library/react';
import { beforeEach, expect, it, vi } from 'vitest';
import type { NetworkAccessRequestDto } from '@/features/network-access/networkAccessTypes';
import { useNetworkRequestNotifications } from '@/features/network-access/useNetworkRequestNotifications';

const mocks = vi.hoisted(() => ({ warning: vi.fn(), navigate: vi.fn() }));
vi.mock('sonner', () => ({ toast: { warning: mocks.warning } }));
vi.mock('react-router-dom', () => ({ useNavigate: () => mocks.navigate }));

beforeEach(() => vi.clearAllMocks());

it('announces new pending requests once and opens Network Access', async () => {
  const request = { id: 'one', status: 'pending', destination: 'auth.kautuka.io' } as NetworkAccessRequestDto;
  const { rerender } = renderHook(({ authenticated, requests }) => useNetworkRequestNotifications(authenticated, requests), {
    initialProps: { authenticated: true, requests: [] as NetworkAccessRequestDto[] },
  });
  expect(mocks.warning).not.toHaveBeenCalled();
  rerender({ authenticated: true, requests: [request] });
  await waitFor(() => expect(mocks.warning).toHaveBeenCalledTimes(1));
  expect(mocks.warning.mock.calls[0][0]).toContain('auth.kautuka.io');
  mocks.warning.mock.calls[0][1].action.onClick();
  expect(mocks.navigate).toHaveBeenCalledWith('/network-access');
  rerender({ authenticated: true, requests: [{ ...request }] });
  expect(mocks.warning).toHaveBeenCalledTimes(1);
  rerender({ authenticated: true, requests: [{ ...request }, { ...request, id: 'two', destination: 'example.com' }] });
  await waitFor(() => expect(mocks.warning).toHaveBeenCalledTimes(2));
  rerender({ authenticated: false, requests: [] });
  rerender({ authenticated: true, requests: [request] });
  await waitFor(() => expect(mocks.warning).toHaveBeenCalledTimes(3));
});
