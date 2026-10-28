import App from '@/app/App';
import { queryClient } from '@/lib/queryClient';
import { QueryProvider } from '@/providers/QueryProvider';
import { fireEvent, screen, waitFor } from '@testing-library/dom';
import { render } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const {
  useCurrentUserMock,
  refreshViaCookieMock,
  fetchUpdateStatusMock,
  refreshUpdateDiscoveryMock,
  revertUpdateMock,
  setAccessTokenMock,
  setAuthFailureHandlerMock,
  localStorageMock,
  sessionStorageMock,
} = vi.hoisted(() => ({
  useCurrentUserMock: vi.fn(),
  refreshViaCookieMock: vi.fn(),
  fetchUpdateStatusMock: vi.fn(),
  refreshUpdateDiscoveryMock: vi.fn(),
  revertUpdateMock: vi.fn(),
  setAccessTokenMock: vi.fn(),
  setAuthFailureHandlerMock: vi.fn(),
  localStorageMock: {
    getItem: vi.fn<(key: string) => string | null>((key: string) =>
      key === 'kautuka-theme' ? 'light' : null
    ),
    setItem: vi.fn(),
    removeItem: vi.fn(),
  },
  sessionStorageMock: {
    getItem: vi.fn<(key: string) => string | null>(() => null),
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
    getAccessToken: () => 'token-1',
    getProfile: vi.fn(),
    logout: vi.fn(),
  };
});

vi.mock('@/features/dashboard', async () => {
  const actual =
    await vi.importActual<typeof import('@/features/dashboard')>('@/features/dashboard');
  return {
    ...actual,
    fetchUpdateStatus: fetchUpdateStatusMock,
    refreshUpdateDiscovery: refreshUpdateDiscoveryMock,
    applyUpdate: vi.fn(),
    revertUpdate: revertUpdateMock,
  };
});

vi.mock('@/features/onboarding', () => ({
  useOnboardingGuard: vi.fn(),
}));

vi.mock('@/components/Header', () => ({
  Header: ({
    updateTitle,
    updateDescription,
    canApplyUpdate,
    onApplyUpdate,
    canRollbackUpdate,
    onRollbackUpdate,
    onCheckForUpdates,
  }: {
    updateTitle?: string;
    updateDescription?: string;
    canApplyUpdate?: boolean;
    onApplyUpdate?: () => void;
    canRollbackUpdate?: boolean;
    onRollbackUpdate?: () => void;
    onCheckForUpdates?: () => void;
  }) => (
    <div>
      Mock Header
      {updateTitle ? <div data-testid="sidebar-update-title">{updateTitle}</div> : null}
      {updateDescription ? <div>{updateDescription}</div> : null}
      {canApplyUpdate ? <button onClick={onApplyUpdate}>Apply</button> : null}
      {canRollbackUpdate ? <button onClick={onRollbackUpdate}>Roll back</button> : null}
      {onCheckForUpdates ? <button onClick={onCheckForUpdates}>Check for updates</button> : null}
    </div>
  ),
}));

vi.mock('@/components/ExecutionReadinessBanner', () => ({
  ExecutionReadinessBanner: () => null,
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

describe('App update banner', () => {
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

    const adminRole = ['ad', 'min'].join('');
    useCurrentUserMock.mockReturnValue({
      data: { role: adminRole },
      isLoading: false,
      isError: false,
    });
    refreshViaCookieMock.mockResolvedValue({ accessToken: 'token-1' });
    fetchUpdateStatusMock.mockResolvedValue({
      ok: true,
      message: 'Current',
      state: 'Current',
      effectiveReleaseChannel: 'stable',
      eligibilityState: 'Blocked',
      eligibilityReason: 'NewerStableRequired',
      currentRelease: {
        releaseId: 'release-preview-120',
        version: '1.2.0-preview.2',
        channel: 'preview',
        releaseFingerprint: 'fp-current',
        sourceUrl: null,
        publishedAtUtc: null,
        tag: 'v1.2.0-preview.2',
      },
      availableRelease: null,
      lastKnownGoodRelease: null,
      lastDiscoveryAttemptUtc: null,
      lastDiscoverySuccessUtc: null,
      managerVersion: '1.2.0-preview.2',
      hostReady: true,
      activeExecCount: 0,
      lastError: null,
    });
    refreshUpdateDiscoveryMock.mockReset();
    revertUpdateMock.mockReset();

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
    vi.unstubAllGlobals();
  });

  it('shows waiting-for-stable status in the sidebar without a page banner', async () => {
    renderApp();

    await waitFor(() => {
      expect(screen.getByText('Waiting for newer stable release')).toBeInTheDocument();
    });

    expect(
      screen.getByText(
        'Update track is set to stable. The current release stays active until a stable release newer than 1.2.0-preview.2 is available.'
      )
    ).toBeInTheDocument();
    expect(screen.queryByTitle('Dismiss update notice')).not.toBeInTheDocument();
  });

  it('shows the update banner when discovery fails during a refresh', async () => {
    fetchUpdateStatusMock.mockResolvedValueOnce({
      ok: true,
      message: 'update-refresh',
      state: 'Current',
      effectiveReleaseChannel: 'preview',
      eligibilityState: 'None',
      eligibilityReason: 'AlreadyCurrent',
      currentRelease: {
        releaseId: 'release-preview-13043308',
        version: '0.1.1-preview.20260513043308',
        channel: 'preview',
        releaseFingerprint: 'fp-current',
        sourceUrl: null,
        publishedAtUtc: null,
        tag: 'v0.1.1-preview.20260513043308',
      },
      availableRelease: null,
      lastKnownGoodRelease: null,
      lastDiscoveryAttemptUtc: '2026-05-13T05:00:00Z',
      lastDiscoverySuccessUtc: null,
      managerVersion: '0.1.1-preview.20260513043308',
      hostReady: true,
      activeExecCount: 0,
      lastError: 'Manifest release discovery failed with HTTP 404.',
    });

    renderApp();

    await waitFor(() => {
      expect(screen.getByText('Manager update status')).toBeInTheDocument();
    });

    expect(
      screen.getByText('Manifest release discovery failed with HTTP 404.')
    ).toBeInTheDocument();
  });

  it('names the rejected source when a discovered release cannot be downloaded', async () => {
    // Discovery blocks a release whose asset URLs the trust policy would reject at apply,
    // so the operator has to be told which URL is at fault rather than shown a generic
    // status or an Apply button that is certain to fail.
    fetchUpdateStatusMock.mockResolvedValue({
      ok: true,
      message: 'update-refresh',
      state: 'Current',
      effectiveReleaseChannel: 'stable',
      eligibilityState: 'Blocked',
      eligibilityReason: 'UntrustedSourceUrl',
      currentRelease: {
        releaseId: 'release-stable-120',
        version: '1.2.0',
        channel: 'stable',
        releaseFingerprint: 'fp-current',
        sourceUrl: null,
        publishedAtUtc: null,
        tag: 'v1.2.0',
      },
      availableRelease: null,
      lastKnownGoodRelease: null,
      lastDiscoveryAttemptUtc: '2026-05-13T05:00:00Z',
      lastDiscoverySuccessUtc: '2026-05-13T05:00:00Z',
      managerVersion: '1.2.0',
      hostReady: true,
      activeExecCount: 0,
      lastError:
        'Platform release signature URL must be https or loopback http. Rejected: http://192.168.1.20:20300/kautuka-platform-release.sig',
    });

    renderApp();

    await waitFor(() => {
      expect(
        screen.getByText('Release found but its download source is not trusted')
      ).toBeInTheDocument();
    });

    expect(
      screen.getByText(/Rejected: http:\/\/192\.168\.1\.20:20300/)
    ).toBeInTheDocument();
    expect(
      screen.getByText(/downloads releases only over https or loopback http/)
    ).toBeInTheDocument();
  });

  it('shows a short-lived sidebar confirmation after checking when already current', async () => {
    refreshUpdateDiscoveryMock.mockResolvedValueOnce({
      ok: true,
      message: 'update-refresh',
      state: 'Current',
      effectiveReleaseChannel: 'stable',
      eligibilityState: 'None',
      eligibilityReason: 'AlreadyCurrent',
      currentRelease: {
        releaseId: 'release-stable-120',
        version: '1.2.0',
        channel: 'stable',
        releaseFingerprint: 'fp-current',
        sourceUrl: null,
        publishedAtUtc: null,
        tag: 'v1.2.0',
      },
      availableRelease: null,
      lastKnownGoodRelease: null,
      lastDiscoveryAttemptUtc: null,
      lastDiscoverySuccessUtc: null,
      managerVersion: '1.2.0',
      hostReady: true,
      activeExecCount: 0,
      lastError: null,
    });

    renderApp();

    await waitFor(() => {
      expect(fetchUpdateStatusMock).toHaveBeenCalled();
    });

    fireEvent.click(screen.getByRole('button', { name: 'Check for updates' }));

    await waitFor(() => {
      expect(screen.getByText('You’re up to date')).toBeInTheDocument();
    });

    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('shows updater operation failures even when the status payload is not ok', async () => {
    fetchUpdateStatusMock.mockResolvedValueOnce({
      ok: false,
      message: 'update-apply-failed',
      state: 'Current',
      effectiveReleaseChannel: 'preview',
      eligibilityState: 'None',
      eligibilityReason: 'AlreadyCurrent',
      currentRelease: {
        releaseId: 'release-preview-13043308',
        version: '0.1.1-preview.20260513043308',
        channel: 'preview',
        releaseFingerprint: 'fp-current',
        sourceUrl: null,
        publishedAtUtc: null,
        tag: 'v0.1.1-preview.20260513043308',
      },
      availableRelease: null,
      lastKnownGoodRelease: null,
      lastDiscoveryAttemptUtc: '2026-05-13T05:00:00Z',
      lastDiscoverySuccessUtc: null,
      managerVersion: '0.1.1-preview.20260513043308',
      hostReady: true,
      activeExecCount: 0,
      lastError: 'No prepared update is available.',
    });

    renderApp();

    await waitFor(() => {
      expect(screen.getByText('Update apply failed')).toBeInTheDocument();
    });

    expect(screen.getByText('No prepared update is available.')).toBeInTheDocument();

    fireEvent.click(screen.getByTitle('Dismiss update notice'));

    await waitFor(() => {
      expect(localStorageMock.setItem).toHaveBeenCalledWith(
        'kautuka-dismissed-update-notice-key',
        'error:update-apply-failed:release-preview-13043308'
      );
    });
  });

  it('shows preflight progress messaging while apply is in progress', async () => {
    fetchUpdateStatusMock.mockResolvedValueOnce({
      ok: true,
      message: 'update-apply-accepted',
      state: 'Applying',
      effectiveReleaseChannel: 'preview',
      eligibilityState: 'None',
      eligibilityReason: 'None',
      currentRelease: {
        releaseId: 'release-preview-current',
        version: '0.1.1-preview.20260529004354',
        channel: 'preview',
        releaseFingerprint: 'fp-current',
        sourceUrl: null,
        publishedAtUtc: null,
        tag: 'v0.1.1-preview.20260529004354',
      },
      availableRelease: {
        releaseId: 'release-preview-next',
        version: '0.1.1-preview.20260529015015',
        channel: 'preview',
        releaseFingerprint: 'fp-next',
        sourceUrl: null,
        publishedAtUtc: null,
        tag: 'v0.1.1-preview.20260529015015',
      },
      lastKnownGoodRelease: {
        releaseId: 'release-preview-current',
        version: '0.1.1-preview.20260529004354',
        channel: 'preview',
        releaseFingerprint: 'fp-current',
        sourceUrl: null,
        publishedAtUtc: null,
        tag: 'v0.1.1-preview.20260529004354',
      },
      lastDiscoveryAttemptUtc: null,
      lastDiscoverySuccessUtc: null,
      managerVersion: '0.1.1-preview.20260529004354',
      hostReady: true,
      activeExecCount: 0,
      lastError: null,
    });

    renderApp();

    await waitFor(() => {
      expect(screen.getByText('Update preflight checks in progress')).toBeInTheDocument();
    });

    expect(
      screen.getByText(
        'Apply has been requested. The staged updater is running preflight checks before it switches the appliance to the new release.'
      )
    ).toBeInTheDocument();
  });

  it('shows preflight failure messaging when activation checks fail and the release remains available', async () => {
    fetchUpdateStatusMock.mockResolvedValueOnce({
      ok: true,
      message: 'update-status',
      state: 'Available',
      effectiveReleaseChannel: 'preview',
      eligibilityState: 'Available',
      eligibilityReason: 'None',
      currentRelease: {
        releaseId: 'release-preview-current',
        version: '0.1.1-preview.20260529004354',
        channel: 'preview',
        releaseFingerprint: 'fp-current',
        sourceUrl: null,
        publishedAtUtc: null,
        tag: 'v0.1.1-preview.20260529004354',
      },
      availableRelease: {
        releaseId: 'release-preview-next',
        version: '0.1.1-preview.20260529015015',
        channel: 'preview',
        releaseFingerprint: 'fp-next',
        sourceUrl: null,
        publishedAtUtc: null,
        tag: 'v0.1.1-preview.20260529015015',
      },
      lastKnownGoodRelease: {
        releaseId: 'release-preview-current',
        version: '0.1.1-preview.20260529004354',
        channel: 'preview',
        releaseFingerprint: 'fp-current',
        sourceUrl: null,
        publishedAtUtc: null,
        tag: 'v0.1.1-preview.20260529004354',
      },
      lastDiscoveryAttemptUtc: null,
      lastDiscoverySuccessUtc: null,
      managerVersion: '0.1.1-preview.20260529004354',
      hostReady: true,
      activeExecCount: 0,
      lastError:
        "Update preflight checks failed: Provisioned configuration destination '/etc/kautuka/update-source.json' is not writable.",
    });

    renderApp();

    await waitFor(() => {
      expect(screen.getByText('Update preflight checks failed')).toBeInTheDocument();
    });

    expect(
      screen.getByText(
        "Update preflight checks failed: Provisioned configuration destination '/etc/kautuka/update-source.json' is not writable. Fix the reported preflight issue and retry the update."
      )
    ).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Apply update' })).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Roll back' })).not.toBeInTheDocument();
  });

  it('shows applied update state in the sidebar even when an old banner dismissal exists', async () => {
    localStorageMock.getItem.mockImplementation((key: string) => {
      if (key === 'kautuka-theme') {
        return 'light';
      }

      if (key === 'kautuka-dismissed-applied-release-id') {
        return 'release-preview-13045137';
      }

      return null;
    });

    fetchUpdateStatusMock.mockResolvedValueOnce({
      ok: true,
      message: 'Current',
      state: 'Current',
      effectiveReleaseChannel: 'preview',
      eligibilityState: 'None',
      eligibilityReason: 'None',
      currentRelease: {
        releaseId: 'release-preview-13045137',
        version: '0.1.1-preview.20260513045137',
        channel: 'preview',
        releaseFingerprint: 'fp-current',
        sourceUrl: null,
        publishedAtUtc: null,
        tag: 'v0.1.1-preview.20260513045137',
      },
      availableRelease: null,
      lastKnownGoodRelease: {
        releaseId: 'release-preview-13043308',
        version: '0.1.1-preview.20260513043308',
        channel: 'preview',
        releaseFingerprint: 'fp-previous',
        sourceUrl: null,
        publishedAtUtc: null,
        tag: 'v0.1.1-preview.20260513043308',
      },
      lastDiscoveryAttemptUtc: null,
      lastDiscoverySuccessUtc: null,
      managerVersion: '0.1.1-preview.20260513045137',
      hostReady: true,
      activeExecCount: 0,
      lastError: null,
    });

    renderApp();

    await waitFor(() => {
      expect(screen.getByText('Update applied: 0.1.1-preview.20260513045137')).toBeInTheDocument();
    });

    expect(screen.queryByTitle('Dismiss update notice')).not.toBeInTheDocument();
  });

  it('keeps applied update state in the sidebar instead of a dismissible page banner', async () => {
    fetchUpdateStatusMock.mockResolvedValueOnce({
      ok: true,
      message: 'Current',
      state: 'Current',
      effectiveReleaseChannel: 'preview',
      eligibilityState: 'None',
      eligibilityReason: 'None',
      currentRelease: {
        releaseId: 'release-preview-13051143',
        version: '0.1.1-preview.20260513051143',
        channel: 'preview',
        releaseFingerprint: 'fp-current',
        sourceUrl: null,
        publishedAtUtc: null,
        tag: 'v0.1.1-preview.20260513051143',
      },
      availableRelease: null,
      lastKnownGoodRelease: {
        releaseId: 'release-preview-13045137',
        version: '0.1.1-preview.20260513045137',
        channel: 'preview',
        releaseFingerprint: 'fp-previous',
        sourceUrl: null,
        publishedAtUtc: null,
        tag: 'v0.1.1-preview.20260513045137',
      },
      lastDiscoveryAttemptUtc: null,
      lastDiscoverySuccessUtc: null,
      managerVersion: '0.1.1-preview.20260513051143',
      hostReady: true,
      activeExecCount: 0,
      lastError: null,
    });

    renderApp();

    await waitFor(() => {
      expect(screen.getByText('Update applied: 0.1.1-preview.20260513051143')).toBeInTheDocument();
    });

    expect(screen.queryByTitle('Dismiss update notice')).not.toBeInTheDocument();
  });

  it('does not promote historical apply errors into a banner after the release is current', async () => {
    fetchUpdateStatusMock.mockResolvedValueOnce({
      ok: true,
      message: 'Current',
      state: 'Current',
      effectiveReleaseChannel: 'preview',
      eligibilityState: 'None',
      eligibilityReason: 'None',
      currentRelease: {
        releaseId: 'release-preview-13051143',
        version: '0.1.1-preview.20260513051143',
        channel: 'preview',
        releaseFingerprint: 'fp-current',
        sourceUrl: null,
        publishedAtUtc: null,
        tag: 'v0.1.1-preview.20260513051143',
      },
      availableRelease: null,
      lastKnownGoodRelease: {
        releaseId: 'release-preview-13045137',
        version: '0.1.1-preview.20260513045137',
        channel: 'preview',
        releaseFingerprint: 'fp-previous',
        sourceUrl: null,
        publishedAtUtc: null,
        tag: 'v0.1.1-preview.20260513045137',
      },
      lastDiscoveryAttemptUtc: null,
      lastDiscoverySuccessUtc: null,
      managerVersion: '0.1.1-preview.20260513051143',
      hostReady: true,
      activeExecCount: 0,
      lastError: 'Historical preflight detail retained by the updater.',
    });

    renderApp();

    await waitFor(() => {
      expect(screen.getByText('Update applied: 0.1.1-preview.20260513051143')).toBeInTheDocument();
    });

    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.queryByTitle('Dismiss update notice')).not.toBeInTheDocument();
  });

  it('keeps applied update state in the sidebar across reload when transport messages change', async () => {
    const storage = new Map<string, string>();
    storage.set('kautuka-theme', 'light');

    localStorageMock.getItem.mockImplementation((key: string) => storage.get(key) ?? null);
    localStorageMock.setItem.mockImplementation((key: string, value: string) => {
      storage.set(key, value);
    });
    localStorageMock.removeItem.mockImplementation((key: string) => {
      storage.delete(key);
    });

    fetchUpdateStatusMock.mockResolvedValueOnce({
      ok: true,
      message: 'update-apply',
      state: 'Current',
      effectiveReleaseChannel: 'preview',
      eligibilityState: 'None',
      eligibilityReason: 'None',
      currentRelease: {
        releaseId: 'release-preview-13051143',
        version: '0.1.1-preview.20260513051143',
        channel: 'preview',
        releaseFingerprint: 'fp-current',
        sourceUrl: null,
        publishedAtUtc: null,
        tag: 'v0.1.1-preview.20260513051143',
      },
      availableRelease: null,
      lastKnownGoodRelease: {
        releaseId: 'release-preview-13045137',
        version: '0.1.1-preview.20260513045137',
        channel: 'preview',
        releaseFingerprint: 'fp-previous',
        sourceUrl: null,
        publishedAtUtc: null,
        tag: 'v0.1.1-preview.20260513045137',
      },
      lastDiscoveryAttemptUtc: null,
      lastDiscoverySuccessUtc: null,
      managerVersion: '0.1.1-preview.20260513051143',
      hostReady: true,
      activeExecCount: 0,
      lastError: null,
    });

    const firstRender = renderApp();

    await waitFor(() => {
      expect(screen.getByText('Update applied: 0.1.1-preview.20260513051143')).toBeInTheDocument();
    });

    firstRender.unmount();

    fetchUpdateStatusMock.mockResolvedValueOnce({
      ok: true,
      message: 'update-status',
      state: 'Current',
      effectiveReleaseChannel: 'preview',
      eligibilityState: 'None',
      eligibilityReason: 'None',
      currentRelease: {
        releaseId: 'release-preview-13051143',
        version: '0.1.1-preview.20260513051143',
        channel: 'preview',
        releaseFingerprint: 'fp-current',
        sourceUrl: null,
        publishedAtUtc: null,
        tag: 'v0.1.1-preview.20260513051143',
      },
      availableRelease: null,
      lastKnownGoodRelease: {
        releaseId: 'release-preview-13045137',
        version: '0.1.1-preview.20260513045137',
        channel: 'preview',
        releaseFingerprint: 'fp-previous',
        sourceUrl: null,
        publishedAtUtc: null,
        tag: 'v0.1.1-preview.20260513045137',
      },
      lastDiscoveryAttemptUtc: null,
      lastDiscoverySuccessUtc: null,
      managerVersion: '0.1.1-preview.20260513051143',
      hostReady: true,
      activeExecCount: 0,
      lastError: null,
    });

    renderApp();

    await waitFor(() => {
      expect(fetchUpdateStatusMock).toHaveBeenCalledTimes(2);
    });

    await waitFor(() => {
      expect(screen.getByText('Update applied: 0.1.1-preview.20260513051143')).toBeInTheDocument();
    });
  });

  it('does not show the update banner for non-admin users', async () => {
    useCurrentUserMock.mockReturnValue({
      data: { role: 'user' },
      isLoading: false,
      isError: false,
    });

    fetchUpdateStatusMock.mockResolvedValueOnce({
      ok: true,
      message: 'Current',
      state: 'Current',
      effectiveReleaseChannel: 'preview',
      eligibilityState: 'None',
      eligibilityReason: 'None',
      currentRelease: {
        releaseId: 'release-preview-13051143',
        version: '0.1.1-preview.20260513051143',
        channel: 'preview',
        releaseFingerprint: 'fp-current',
        sourceUrl: null,
        publishedAtUtc: null,
        tag: 'v0.1.1-preview.20260513051143',
      },
      availableRelease: null,
      lastKnownGoodRelease: {
        releaseId: 'release-preview-13045137',
        version: '0.1.1-preview.20260513045137',
        channel: 'preview',
        releaseFingerprint: 'fp-previous',
        sourceUrl: null,
        publishedAtUtc: null,
        tag: 'v0.1.1-preview.20260513045137',
      },
      lastDiscoveryAttemptUtc: null,
      lastDiscoverySuccessUtc: null,
      managerVersion: '0.1.1-preview.20260513051143',
      hostReady: true,
      activeExecCount: 0,
      lastError: null,
    });

    renderApp();

    await waitFor(() => {
      expect(fetchUpdateStatusMock).toHaveBeenCalled();
    });

    expect(
      screen.queryByText('Update applied: 0.1.1-preview.20260513051143')
    ).not.toBeInTheDocument();
    expect(screen.queryByTitle('Dismiss update notice')).not.toBeInTheDocument();
  });

  it('requires confirmation before rollback and shows rollback complete after the previous release returns', async () => {
    const sessionStorageData = new Map<string, string>();
    sessionStorageMock.getItem.mockImplementation(
      (key: string) => sessionStorageData.get(key) ?? null
    );
    sessionStorageMock.setItem.mockImplementation((key: string, value: string) => {
      sessionStorageData.set(key, value);
    });
    sessionStorageMock.removeItem.mockImplementation((key: string) => {
      sessionStorageData.delete(key);
    });

    fetchUpdateStatusMock.mockResolvedValueOnce({
      ok: true,
      message: 'Current',
      state: 'Current',
      effectiveReleaseChannel: 'preview',
      eligibilityState: 'None',
      eligibilityReason: 'None',
      currentRelease: {
        releaseId: '0.1.1-preview.20260525090000',
        version: '0.1.1-preview.20260525090000',
        channel: 'preview',
        releaseFingerprint: 'fp-current',
        sourceUrl: null,
        publishedAtUtc: null,
        tag: 'v0.1.1-preview.20260525090000',
      },
      availableRelease: null,
      lastKnownGoodRelease: {
        releaseId: '0.1.1-preview.20260525083000',
        version: '0.1.1-preview.20260525083000',
        channel: 'preview',
        releaseFingerprint: 'fp-previous',
        sourceUrl: null,
        publishedAtUtc: null,
        tag: 'v0.1.1-preview.20260525083000',
      },
      lastDiscoveryAttemptUtc: null,
      lastDiscoverySuccessUtc: null,
      managerVersion: '0.1.1-preview.20260525090000',
      hostReady: true,
      activeExecCount: 0,
      lastError: null,
    });

    revertUpdateMock.mockResolvedValueOnce({
      ok: true,
      message: 'update-revert-accepted',
      state: 'Applying',
      effectiveReleaseChannel: 'preview',
      eligibilityState: 'None',
      eligibilityReason: 'None',
      currentRelease: {
        releaseId: '0.1.1-preview.20260525090000',
        version: '0.1.1-preview.20260525090000',
        channel: 'preview',
        releaseFingerprint: 'fp-current',
        sourceUrl: null,
        publishedAtUtc: null,
        tag: 'v0.1.1-preview.20260525090000',
      },
      availableRelease: null,
      lastKnownGoodRelease: {
        releaseId: '0.1.1-preview.20260525083000',
        version: '0.1.1-preview.20260525083000',
        channel: 'preview',
        releaseFingerprint: 'fp-previous',
        sourceUrl: null,
        publishedAtUtc: null,
        tag: 'v0.1.1-preview.20260525083000',
      },
      lastDiscoveryAttemptUtc: null,
      lastDiscoverySuccessUtc: null,
      managerVersion: '0.1.1-preview.20260525090000',
      hostReady: true,
      activeExecCount: 0,
      lastError: null,
    });

    const firstRender = renderApp();

    await waitFor(() => {
      expect(screen.getByRole('button', { name: 'Roll back' })).toBeInTheDocument();
    });

    fireEvent.click(screen.getByRole('button', { name: 'Roll back' }));

    expect(revertUpdateMock).not.toHaveBeenCalled();
    expect(
      screen.getByRole('heading', {
        name: 'Confirm rollback',
      })
    ).toBeInTheDocument();
    expect(
      screen.getByText(/restore shared Kautuka state from the latest rollback backup/i)
    ).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Confirm rollback' }));

    await waitFor(() => {
      expect(revertUpdateMock).toHaveBeenCalledTimes(1);
    });

    firstRender.unmount();

    fetchUpdateStatusMock.mockReset();

    fetchUpdateStatusMock.mockResolvedValue({
      ok: true,
      message: 'Current',
      state: 'Current',
      effectiveReleaseChannel: 'preview',
      eligibilityState: 'None',
      eligibilityReason: 'None',
      currentRelease: {
        releaseId: '0.1.1-preview.20260525083000',
        version: '0.1.1-preview.20260525083000',
        channel: 'preview',
        releaseFingerprint: 'fp-previous',
        sourceUrl: null,
        publishedAtUtc: null,
        tag: 'v0.1.1-preview.20260525083000',
      },
      availableRelease: null,
      lastKnownGoodRelease: null,
      lastDiscoveryAttemptUtc: null,
      lastDiscoverySuccessUtc: null,
      managerVersion: '0.1.1-preview.20260525083000',
      hostReady: true,
      activeExecCount: 0,
      lastError: null,
    });

    renderApp();

    await waitFor(() => {
      expect(screen.getByText('Rollback complete')).toBeInTheDocument();
    });

    expect(
      screen.getByText(
        'Release 0.1.1-preview.20260525083000 is active again. The appliance restored the latest rollback backup, switched bundled services back to the previous release, and returned to normal operation.'
      )
    ).toBeInTheDocument();
  });
});
