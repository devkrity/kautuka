import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, renderHook, waitFor } from '@testing-library/react';
import type { PropsWithChildren } from 'react';
import React from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const { settingsAdminMock, checkRuleMock, grantRuleMock, resolveTargetMock } = vi.hoisted(() => ({
  settingsAdminMock: {
    listProviders: vi.fn(),
    listProviderTypes: vi.fn(),
    testProvider: vi.fn(),
  },
  checkRuleMock: vi.fn(),
  grantRuleMock: vi.fn(),
  resolveTargetMock: vi.fn(),
}));

vi.mock('@/features/settings', () => ({ settingsAdmin: settingsAdminMock }));
vi.mock('@/features/network-access/networkAccess', () => ({
  checkProviderSystemwideRule: checkRuleMock,
  grantProviderSystemwideRule: grantRuleMock,
  resolveProviderNetworkTarget: resolveTargetMock,
}));

import { useProvidersTab } from '@/features/settings/hooks/useProvidersTab';

describe('useProvidersTab systemwide provider test approval', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    settingsAdminMock.listProviders.mockResolvedValue({ data: { providers: [] } });
    settingsAdminMock.listProviderTypes.mockResolvedValue({
      data: { providerTypes: [{ type: 'openai', defaultBaseUrl: 'https://api.openai.com' }] },
    });
    settingsAdminMock.testProvider.mockResolvedValue({ data: { success: true, message: 'ok' } });
    checkRuleMock.mockResolvedValue({ alreadyAllowed: false, expectedRevision: 4 });
    grantRuleMock.mockResolvedValue(undefined);
    resolveTargetMock.mockReturnValue({ host: 'api.openai.com', port: 443 });
  });

  function createWrapper() {
    const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    return function Wrapper({ children }: PropsWithChildren) {
      return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
    };
  }

  it('requests admin confirmation before creating a persistent systemwide rule', async () => {
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

    await act(async () => result.current.testConnection());

    expect(checkRuleMock).toHaveBeenCalledWith({ host: 'api.openai.com', port: 443 });
    expect(result.current.pendingNetworkApproval).toEqual({
      target: { host: 'api.openai.com', port: 443 },
      expectedRevision: 4,
    });
    expect(grantRuleMock).not.toHaveBeenCalled();
    expect(settingsAdminMock.testProvider).not.toHaveBeenCalled();

    await act(async () => result.current.confirmNetworkApprovalAndTest());

    expect(grantRuleMock).toHaveBeenCalledWith({ host: 'api.openai.com', port: 443 }, 4);
    expect(settingsAdminMock.testProvider).toHaveBeenCalledTimes(1);
    expect(grantRuleMock.mock.invocationCallOrder[0]).toBeLessThan(
      settingsAdminMock.testProvider.mock.invocationCallOrder[0],
    );
  });
});
