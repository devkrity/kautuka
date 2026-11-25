import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook, waitFor } from '@testing-library/react';
import type { PropsWithChildren } from 'react';
import React from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const { settingsAdminMock } = vi.hoisted(() => ({
  settingsAdminMock: {
    listProviders: vi.fn(),
    testProvider: vi.fn(),
  },
}));

vi.mock('@/features/settings', () => ({ settingsAdmin: settingsAdminMock }));
import { useProvidersTab } from '@/features/settings/hooks/useProvidersTab';

describe('useProvidersTab systemwide provider test approval', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    settingsAdminMock.listProviders.mockResolvedValue({ data: { providers: [] } });
    settingsAdminMock.testProvider.mockResolvedValue({ data: { success: true, message: 'ok' } });
  });

  function createWrapper() {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    return function Wrapper({ children }: PropsWithChildren) {
      return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
    };
  }

  it('requests a temporary allowance when testing', async () => {
    const { result } = renderHook(() => useProvidersTab(), { wrapper: createWrapper() });
    await waitFor(() => expect(result.current.loading).toBe(false));

    act(() => {
      result.current.startCreate();
      result.current.setForm({
        id: 'openai-test',
        type: 'openai',
        base_url: 'https://api.openai.com/v1',
        api_key: '',
        http_timeout_seconds: '30',
      });
    });

    const target = { host: 'canonical.provider.example', port: 9443 };
    settingsAdminMock.testProvider.mockResolvedValueOnce({ data: { confirmationTarget: target } });
    await act(async () => result.current.testConnection());
    expect(result.current.pendingNetworkApproval).toEqual(target);
    expect(settingsAdminMock.testProvider).toHaveBeenCalledTimes(1);

    act(() => result.current.cancelNetworkApproval());
    expect(result.current.pendingNetworkApproval).toBeNull();
    expect(settingsAdminMock.testProvider).toHaveBeenCalledTimes(1);

    settingsAdminMock.testProvider.mockResolvedValueOnce({ data: { confirmationTarget: target } });
    await act(async () => result.current.testConnection());
    await act(async () => result.current.confirmNetworkApprovalAndTest());
    expect(settingsAdminMock.testProvider).toHaveBeenLastCalledWith(expect.objectContaining({ confirmedTarget: target }));
    expect(result.current.result?.success).toBe(true);
  });
});
