import React from 'react';
import { render } from '@testing-library/react';
import { screen, waitFor } from '@testing-library/dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import App from '@/app/App';
import { QueryProvider } from '@/providers/QueryProvider';
import { queryClient } from '@/lib/queryClient';

const {
  useCurrentUserMock,
  refreshViaCookieMock,
  setAccessTokenMock,
  setAuthFailureHandlerMock,
  localStorageMock,
  sessionStorageMock,
} = vi.hoisted(() => ({
  useCurrentUserMock: vi.fn(),
  refreshViaCookieMock: vi.fn(),
  setAccessTokenMock: vi.fn(),
  setAuthFailureHandlerMock: vi.fn(),
  localStorageMock: {
    getItem: vi.fn(() => null),
    setItem: vi.fn(),
    removeItem: vi.fn(),
  },
  sessionStorageMock: {
    getItem: vi.fn(() => null),
    setItem: vi.fn(),
    removeItem: vi.fn(),
  },
}));

vi.mock('@/features/auth', async () => {
  const actual = await vi.importActual<typeof import('@/features/auth')>('@/features/auth');
  return {
    ...actual,
    useCurrentUser: useCurrentUserMock,
    refreshViaCookie: refreshViaCookieMock,
    setAccessToken: setAccessTokenMock,
    setAuthFailureHandler: setAuthFailureHandlerMock,
    getAccessToken: () => null,
    getProfile: vi.fn(),
    logout: vi.fn(),
  };
});

vi.mock('@/features/dashboard', async () => {
  const actual =
    await vi.importActual<typeof import('@/features/dashboard')>('@/features/dashboard');
  return {
    ...actual,
    fetchUpdateStatus: vi.fn(),
    refreshUpdateDiscovery: vi.fn(),
    applyUpdate: vi.fn(),
    revertUpdate: vi.fn(),
  };
});

vi.mock('@/features/onboarding', () => ({
  useOnboardingGuard: vi.fn(),
}));

vi.mock('@/components/Header', () => ({
  Header: () => <div>Mock Header</div>,
}));

vi.mock('@/pages/LoginPage', () => ({
  LoginPage: () => <div>Mock Login Page</div>,
}));

vi.mock('@/pages/Dashboard', () => ({
  Dashboard: () => <div>Mock Dashboard</div>,
}));

vi.mock('@/pages/AgentPageV2', () => ({
  AgentPageV2: () => <div>Mock Workspace Page</div>,
}));

vi.mock('@/pages/ApprovalsPage', () => ({
  ApprovalsPage: () => <div>Mock Approvals Page</div>,
}));

vi.mock('@/pages/ConnectorsPage', () => ({
  ConnectorsPage: () => <div>Mock Connectors Page</div>,
}));

vi.mock('@/pages/SkillsPage', () => ({
  SkillsPage: () => <div>Mock Skills Page</div>,
}));

vi.mock('@/pages/MemoriesPage', () => ({
  MemoriesPage: () => <div>Mock Memories Page</div>,
}));

vi.mock('@/pages/AutopilotPage', () => ({
  AutopilotPage: () => <div>Mock Autopilot Page</div>,
}));

vi.mock('@/pages/SettingsPage', () => ({
  SettingsPage: () => <div>Mock Settings Page</div>,
}));

vi.mock('@/features/onboarding/routes/OnboardingWizard', () => ({
  OnboardingWizard: () => <div>Mock Onboarding Wizard</div>,
}));

function renderApp() {
  return render(
    <QueryProvider>
      <App />
    </QueryProvider>
  );
}

describe('App startup flow', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    queryClient.clear();

    Object.defineProperty(window, 'localStorage', {
      value: localStorageMock,
      configurable: true,
    });

    Object.defineProperty(window, 'sessionStorage', {
      value: sessionStorageMock,
      configurable: true,
    });

    window.history.replaceState(null, '', '/');

    useCurrentUserMock.mockReturnValue({ data: null, isLoading: false, isError: false });
    refreshViaCookieMock.mockResolvedValue(false);

    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue({
        ok: true,
        json: async () => ({ needsSetup: false }),
      })
    );
  });

  afterEach(() => {
    queryClient.clear();
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('survives the startup-to-login transition without crashing', async () => {
    renderApp();

    expect(screen.getByText('Waking up your AI services...')).toBeInTheDocument();

    await waitFor(() => {
      expect(screen.getByText('Mock Login Page')).toBeInTheDocument();
    });

    expect(screen.queryByText('GLOBAL CRASH')).not.toBeInTheDocument();
    expect(localStorageMock.getItem).not.toHaveBeenCalledWith('kautuka-access-token');
    expect(localStorageMock.setItem).not.toHaveBeenCalledWith(
      'kautuka-access-token',
      expect.anything()
    );
    expect(useCurrentUserMock).toHaveBeenCalled();
    expect(
      useCurrentUserMock.mock.calls.every(([value]) => {
        const options = value as { enabled?: boolean } | undefined;
        return options?.enabled === false;
      })
    ).toBe(true);
  });

  it('shows the reconnecting startup screen when the backend drops after initial load', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce({
        ok: true,
        json: async () => ({ needsSetup: false }),
      })
      .mockRejectedValueOnce(new Error('gateway restarting'))
      .mockRejectedValueOnce(new Error('gateway restarting'))
      .mockRejectedValueOnce(new Error('gateway restarting'))
      .mockRejectedValueOnce(new Error('gateway restarting'));

    vi.stubGlobal('fetch', fetchMock);

    renderApp();

    await waitFor(() => {
      expect(screen.getByText('Mock Login Page')).toBeInTheDocument();
    });

    await waitFor(
      () => {
        expect(screen.getByText('Reconnecting to your AI services...')).toBeInTheDocument();
      },
      { timeout: 12000 }
    );
  }, 15000);
});
