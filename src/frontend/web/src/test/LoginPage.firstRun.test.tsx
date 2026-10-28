/**
 * Regression tests for LoginPage first-run detection.
 *
 * REGRESSION HISTORY:
 *   - LoginPage had its own getSetupStatus() fetch with .catch(() => setNeedsSetup(false)).
 *     Any fetch failure silently showed the login form instead of the setup form on clean install.
 *   - Fix: App.tsx startup poll reads needsSetup from /api/auth/setup/status and passes it as
 *     initialNeedsSetup prop. LoginPage uses it directly, skipping the redundant second fetch.
 *
 * These tests MUST fail if the initialNeedsSetup prop is removed or ignored, or if the
 * fallback fetch is allowed to override a provided prop value.
 */
import React from 'react';
import { render, act, cleanup } from '@testing-library/react';
import { screen, waitFor } from '@testing-library/dom';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { vi, describe, it, expect, beforeEach, afterEach } from 'vitest';
import { LoginPage } from '@/pages/LoginPage';

// ── Mock the auth module at source level ───────────────────────────────────────
// Mock the concrete auth API module so `getSetupStatus` never reaches fetch.
// LoginPage imports from '@/features/auth' (barrel) which re-exports from here.

const {
  getSetupStatusMock,
  loginMock,
  createFirstAdminMock,
  setAccessTokenMock,
  listExternalProvidersMock,
} = vi.hoisted(() => ({
  getSetupStatusMock: vi.fn<() => Promise<{ needsSetup: boolean }>>(),
  loginMock: vi.fn(),
  createFirstAdminMock: vi.fn(),
  setAccessTokenMock: vi.fn(),
  listExternalProvidersMock: vi.fn<() => Promise<{ google: boolean; microsoft: boolean }>>(),
}));

vi.mock('@/features/auth/api/auth', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/features/auth/api/auth')>();
  return {
    ...actual,
    getSetupStatus: getSetupStatusMock,
    login: loginMock,
    createFirstAdmin: createFirstAdminMock,
    setAccessToken: setAccessTokenMock,
    listExternalProviders: listExternalProvidersMock,
  };
});

// ── helpers ────────────────────────────────────────────────────────────────────

function renderLoginPage(props: { initialNeedsSetup?: boolean | null } = {}) {
  return render(
    <MemoryRouter>
      <LoginPage onLoginSuccess={vi.fn()} {...props} />
    </MemoryRouter>
  );
}

// ── tests ─────────────────────────────────────────────────────────────────────

describe('LoginPage — first-run detection', () => {
  beforeEach(() => {
    getSetupStatusMock.mockReset();
    loginMock.mockReset();
    createFirstAdminMock.mockReset();
    setAccessTokenMock.mockReset();
    listExternalProvidersMock.mockReset();
    listExternalProvidersMock.mockResolvedValue({ google: false, microsoft: false });
  });

  afterEach(() => {
    cleanup();
  });

  // ── CRITICAL REGRESSION TESTS ─────────────────────────────────────────────

  /**
   * THE KEY REGRESSION TEST.
   *
   * When initialNeedsSetup=true is passed:
   *   - setup form MUST be shown immediately
   *   - NO getSetupStatus call must happen
   *
   * Old behaviour: getSetupStatus failed → .catch(() => setNeedsSetup(false)) → login form shown.
   * New behaviour: prop is used directly, getSetupStatus is never called.
   */
  it('shows setup form when initialNeedsSetup=true without calling getSetupStatus', async () => {
    // Intentionally set wrong value — must never be used
    getSetupStatusMock.mockResolvedValue({ needsSetup: false });

    renderLoginPage({ initialNeedsSetup: true });

    await waitFor(() => {
      expect(
        screen.getByRole('heading', { name: 'Create Admin Account', level: 1 })
      ).toBeInTheDocument();
    });

    // getSetupStatus must NOT have been called — the prop is sufficient
    expect(getSetupStatusMock).not.toHaveBeenCalled();
  });

  it('shows login form when initialNeedsSetup=false without calling getSetupStatus', async () => {
    getSetupStatusMock.mockResolvedValue({ needsSetup: true }); // wrong — must not be used

    renderLoginPage({ initialNeedsSetup: false });

    await waitFor(() => {
      expect(
        screen.getByRole('heading', { name: 'Welcome to Kautuka', level: 1 })
      ).toBeInTheDocument();
    });

    expect(getSetupStatusMock).not.toHaveBeenCalled();
  });

  it('shows setup form even if getSetupStatus would fail', async () => {
    // Simulate a network failure that would have silently shown login form under old code
    getSetupStatusMock.mockRejectedValue(new Error('Network error'));

    renderLoginPage({ initialNeedsSetup: true });

    await waitFor(() => {
      expect(
        screen.getByRole('heading', { name: 'Create Admin Account', level: 1 })
      ).toBeInTheDocument();
    });

    expect(getSetupStatusMock).not.toHaveBeenCalled();
  });

  it('enters onboarding after creating the first admin instead of landing on dashboard', async () => {
    const onLoginSuccess = vi.fn();
    createFirstAdminMock.mockResolvedValue(undefined);
    loginMock.mockResolvedValue({
      accessToken: ['first', 'admin', 'token'].join('-'),
      expiresInSeconds: 900,
    });

    render(
      <MemoryRouter initialEntries={['/first-run']}>
        <Routes>
          <Route
            path="/first-run"
            element={<LoginPage onLoginSuccess={onLoginSuccess} initialNeedsSetup />}
          />
          <Route path="/onboarding" element={<div>Onboarding destination</div>} />
          <Route path="/dashboard" element={<div>Dashboard destination</div>} />
        </Routes>
      </MemoryRouter>
    );

    const user = userEvent.setup();
    await user.type(screen.getByLabelText('Username'), 'admin');
    await user.type(screen.getByLabelText('Password'), 'passw0rd');
    await user.type(screen.getByLabelText('Confirm Password'), 'passw0rd');
    await user.type(screen.getByLabelText('Setup token'), 'setup-token');
    await user.click(screen.getByRole('button', { name: 'Create Admin Account' }));

    await waitFor(() => expect(screen.getByText('Onboarding destination')).toBeInTheDocument());
    expect(screen.queryByText('Dashboard destination')).not.toBeInTheDocument();
    expect(createFirstAdminMock).toHaveBeenCalledWith('admin', 'passw0rd', 'setup-token');
    expect(loginMock).toHaveBeenCalledWith('admin', 'passw0rd');
    expect(setAccessTokenMock).toHaveBeenCalledWith(
      ['first', 'admin', 'token'].join('-'),
      expect.any(Number)
    );
    expect(onLoginSuccess).toHaveBeenCalledOnce();
  });

  it('hides provider buttons until the appliance reports them', async () => {
    renderLoginPage({ initialNeedsSetup: true });
    expect(screen.queryByRole('button', { name: 'Continue with Google' })).not.toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Continue with Microsoft work or school account' })
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('button', { name: 'Continue with Microsoft personal account' })
    ).not.toBeInTheDocument();
    expect(screen.getByLabelText('Username')).toBeInTheDocument();
  });

  it('shows provider buttons on first-run and sign-in when they are enabled', async () => {
    listExternalProvidersMock.mockResolvedValue({ google: true, microsoft: true });
    renderLoginPage({ initialNeedsSetup: false });
    expect(await screen.findByRole('button', { name: 'Continue with Google' })).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'Continue with Microsoft work or school account' })
    ).toBeInTheDocument();
    expect(
      screen.getByRole('button', { name: 'Continue with Microsoft personal account' })
    ).toBeInTheDocument();
    expect(screen.getByLabelText('Username')).toBeInTheDocument();
  });

  // ── Fallback path (no prop provided) ─────────────────────────────────────

  it('falls back to getSetupStatus when initialNeedsSetup is not provided, shows setup form on needsSetup=true', async () => {
    getSetupStatusMock.mockResolvedValue({ needsSetup: true });

    renderLoginPage(); // no prop

    await waitFor(() => {
      expect(
        screen.getByRole('heading', { name: 'Create Admin Account', level: 1 })
      ).toBeInTheDocument();
    });

    expect(getSetupStatusMock).toHaveBeenCalledOnce();
  });

  it('shows login form from fallback when needsSetup=false', async () => {
    getSetupStatusMock.mockResolvedValue({ needsSetup: false });

    renderLoginPage();

    await waitFor(() => {
      expect(
        screen.getByRole('heading', { name: 'Welcome to Kautuka', level: 1 })
      ).toBeInTheDocument();
    });
  });

  it('falls back to login form when no prop and getSetupStatus fails', async () => {
    getSetupStatusMock.mockRejectedValue(new Error('Network error'));

    renderLoginPage();

    await waitFor(() => {
      expect(
        screen.getByRole('heading', { name: 'Welcome to Kautuka', level: 1 })
      ).toBeInTheDocument();
    });
  });

  // ── Loading state ─────────────────────────────────────────────────────────

  it('shows loading state while fallback fetch is in-flight', async () => {
    let resolveFetch!: (v: { needsSetup: boolean }) => void;
    getSetupStatusMock.mockReturnValue(
      new Promise<{ needsSetup: boolean }>((res) => {
        resolveFetch = res;
      })
    );

    renderLoginPage();

    // Immediately after render: loading state, no form yet
    expect(screen.getByText('Loading…')).toBeInTheDocument();
    expect(
      screen.queryByRole('heading', { name: 'Welcome to Kautuka', level: 1 })
    ).not.toBeInTheDocument();
    expect(
      screen.queryByRole('heading', { name: 'Create Admin Account', level: 1 })
    ).not.toBeInTheDocument();

    // Resolve so no state-update leak
    await act(async () => {
      resolveFetch({ needsSetup: false });
    });
  });
});
