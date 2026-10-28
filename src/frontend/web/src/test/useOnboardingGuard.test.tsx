/**
 * Regression tests for useOnboardingGuard.
 *
 * Key regression: the guard previously checked only `isLoading` to decide whether
 * data was settled. Background refetches (triggered by invalidateQueries after wizard
 * completion) set `isFetching=true` but leave `isLoading=false`. The guard fired during
 * that window with stale cached data (providersCount=0) and redirected every navigation
 * back to /onboarding.
 *
 * Fix: guard now also skips when `isFetching=true`.
 */
import { useOnboardingGuard } from '@/features/onboarding';
import { createOnboardingHandoffState } from '@/features/onboarding/utils/onboardingHandoff';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { act, render } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';

// ── mock useSettingsStatus to fully control isLoading / isFetching / data ─────

type StatusResult = {
  data: { providersCount: number; hasProviders: boolean } | undefined;
  isLoading: boolean;
  isFetching: boolean;
};

const statusOverride: StatusResult = {
  data: undefined,
  isLoading: true,
  isFetching: true,
};

vi.mock('@/features/onboarding/hooks/useSettingsStatus', () => ({
  useSettingsStatus: () => statusOverride,
}));

// ── capture navigate calls ────────────────────────────────────────────────────

const navigateSpy = vi.fn();

vi.mock('react-router-dom', async () => {
  const actual = await vi.importActual<typeof import('react-router-dom')>('react-router-dom');
  return { ...actual, useNavigate: () => navigateSpy };
});

// ── component under test ──────────────────────────────────────────────────────

function TestGuard({
  authenticated,
  isAdmin = true,
}: {
  authenticated: boolean;
  isAdmin?: boolean;
}) {
  useOnboardingGuard(authenticated, isAdmin);
  return <div data-testid="content" />;
}

function setup(initialPath = '/dashboard', authenticated = true, isAdmin = true) {
  const client = new QueryClient();
  render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[initialPath]}>
        <TestGuard authenticated={authenticated} isAdmin={isAdmin} />
      </MemoryRouter>
    </QueryClientProvider>
  );
}

// ── tests ─────────────────────────────────────────────────────────────────────

describe('useOnboardingGuard', () => {
  beforeEach(() => {
    navigateSpy.mockReset();
    // Reset to a safe "loading" default so each test starts clean
    statusOverride.data = undefined;
    statusOverride.isLoading = true;
    statusOverride.isFetching = true;
  });

  it('redirects to /onboarding when providers count is 0 and data has fully settled', async () => {
    statusOverride.data = { providersCount: 0, hasProviders: false };
    statusOverride.isLoading = false;
    statusOverride.isFetching = false;

    setup('/dashboard');

    await act(async () => {});
    expect(navigateSpy).toHaveBeenCalledWith('/onboarding', { replace: true });
  });

  it('does NOT redirect when providers exist and data has settled', async () => {
    statusOverride.data = { providersCount: 1, hasProviders: true };
    statusOverride.isLoading = false;
    statusOverride.isFetching = false;

    setup('/dashboard');

    await act(async () => {});
    expect(navigateSpy).not.toHaveBeenCalled();
  });

  it('does NOT redirect while the initial fetch is loading (isLoading=true)', async () => {
    statusOverride.data = undefined;
    statusOverride.isLoading = true;
    statusOverride.isFetching = true;

    setup('/dashboard');

    await act(async () => {});
    expect(navigateSpy).not.toHaveBeenCalled();
  });

  /**
   * THE KEY REGRESSION TEST.
   *
   * After wizard completes, queryClient.invalidateQueries triggers a background
   * refetch. During that window:
   *   - isLoading = false  (not the first fetch)
   *   - isFetching = true  (background refetch in-flight)
   *   - data = { providersCount: 0 }  (stale cached value before any provider was added)
   *
   * The old guard only checked `isLoading`, so it fired with stale data → redirect loop.
   * The fixed guard also checks `isFetching` and skips the redirect.
   */
  it('does NOT redirect during a background refetch even when stale data shows 0 providers', async () => {
    statusOverride.data = { providersCount: 0, hasProviders: false };
    statusOverride.isLoading = false; // NOT the first load
    statusOverride.isFetching = true; // background refetch triggered by invalidateQueries

    setup('/dashboard');

    await act(async () => {});
    expect(navigateSpy).not.toHaveBeenCalled();
  });

  it('does NOT redirect during the explicit successful wizard handoff to /dashboard', async () => {
    statusOverride.data = { providersCount: 0, hasProviders: false };
    statusOverride.isLoading = false;
    statusOverride.isFetching = false;

    const client = new QueryClient();
    render(
      <QueryClientProvider client={client}>
        <MemoryRouter
          initialEntries={[{ pathname: '/dashboard', state: createOnboardingHandoffState() }]}
        >
          <TestGuard authenticated />
        </MemoryRouter>
      </QueryClientProvider>
    );

    await act(async () => {});
    expect(navigateSpy).not.toHaveBeenCalled();
  });

  it('redirects after restart when browser history contains a stale wizard handoff', async () => {
    statusOverride.data = { providersCount: 0, hasProviders: false };
    statusOverride.isLoading = false;
    statusOverride.isFetching = false;

    const client = new QueryClient();
    render(
      <QueryClientProvider client={client}>
        <MemoryRouter
          initialEntries={[
            {
              pathname: '/dashboard',
              state: { onboardingHandoffToken: ['stale', 'process', 'token'].join('-') },
            },
          ]}
        >
          <TestGuard authenticated />
        </MemoryRouter>
      </QueryClientProvider>
    );

    await act(async () => {});
    expect(navigateSpy).toHaveBeenCalledWith('/onboarding', { replace: true });
  });

  it('does NOT redirect when already on the /onboarding route (avoids loop)', async () => {
    statusOverride.data = { providersCount: 0, hasProviders: false };
    statusOverride.isLoading = false;
    statusOverride.isFetching = false;

    setup('/onboarding');

    await act(async () => {});
    expect(navigateSpy).not.toHaveBeenCalled();
  });

  it('allows the settings workspace while an empty system is being configured manually', async () => {
    statusOverride.data = { providersCount: 0, hasProviders: false };
    statusOverride.isLoading = false;
    statusOverride.isFetching = false;

    setup('/settings?tab=providers');

    await act(async () => {});
    expect(navigateSpy).not.toHaveBeenCalled();
  });

  it('does NOT run the guard when not authenticated', async () => {
    statusOverride.data = { providersCount: 0, hasProviders: false };
    statusOverride.isLoading = false;
    statusOverride.isFetching = false;

    setup('/dashboard', false);

    await act(async () => {});
    expect(navigateSpy).not.toHaveBeenCalled();
  });

  it('does NOT redirect non-admin users even when the system is empty', async () => {
    statusOverride.data = { providersCount: 0, hasProviders: false };
    statusOverride.isLoading = false;
    statusOverride.isFetching = false;

    setup('/dashboard', true, false);

    await act(async () => {});
    expect(navigateSpy).not.toHaveBeenCalled();
  });
});
