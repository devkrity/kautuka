import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { renderHook, waitFor } from '@testing-library/react';
import type { PropsWithChildren } from 'react';
import React from 'react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const { fetchProvidersMock, apiClientGetMock } = vi.hoisted(() => ({
  fetchProvidersMock: vi.fn(),
  apiClientGetMock: vi.fn(),
}));

vi.mock('@/features/settings', () => ({
  fetchProviders: fetchProvidersMock,
}));

vi.mock('@/lib/apiClient', () => ({
  apiClient: {
    get: apiClientGetMock,
  },
}));

import { useSettingsStatus } from '@/features/onboarding/hooks/useSettingsStatus';

describe('useSettingsStatus', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    apiClientGetMock.mockResolvedValue({
      data: {
        lifecycle: {
          status: 'agents_pending',
          coreReady: true,
          agentsPending: true,
          agentsReady: false,
        },
        actionHints: [],
      },
    });
  });

  function createWrapper() {
    const queryClient = new QueryClient({
      defaultOptions: {
        queries: {
          retry: false,
        },
      },
    });

    return function Wrapper({ children }: PropsWithChildren) {
      return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
    };
  }

  it('reports an empty setup when no providers are configured', async () => {
    fetchProvidersMock.mockResolvedValue([]);

    const { result } = renderHook(() => useSettingsStatus(true), { wrapper: createWrapper() });

    await waitFor(() => expect(result.current.isLoading).toBe(false));
    expect(result.current.data?.providersCount).toBe(0);
    expect(result.current.data?.hasProviders).toBe(false);
  });

  it('counts configured providers toward onboarding readiness', async () => {
    fetchProvidersMock.mockResolvedValue([
      {
        id: 'groq-primary',
        type: 'groq',
        settings: {},
        hasApiKey: true,
      },
    ]);

    const { result } = renderHook(() => useSettingsStatus(true), { wrapper: createWrapper() });

    await waitFor(() => expect(result.current.isLoading).toBe(false));
    expect(result.current.data?.providersCount).toBe(1);
    expect(result.current.data?.hasProviders).toBe(true);
  });
});
