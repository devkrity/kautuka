import { Header } from '@/components/Header';
import { ExecutionReadinessBanner } from '@/components/ExecutionReadinessBanner';
import { NetworkPausedBanner } from '@/features/network-access';
import { PageState } from '@/components/layout/PageState';
import { StartupScreen } from '@/components/layout/StartupScreen';
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert';
import { Button } from '@/components/ui/button';
import { useCurrentUser } from '@/features/auth';
import {
  applyUpdate,
  fetchUpdateStatus,
  refreshUpdateDiscovery,
  type UpdateStatusSummary,
} from '@/features/dashboard';
import { useOnboardingGuard } from '@/features/onboarding';
import { ROLE_ADMIN } from '@/features/settings';
import { AlertTriangle, Loader2, X } from 'lucide-react';
import React from 'react';
import { BrowserRouter } from 'react-router-dom';
import { Toaster } from 'sonner';
import { captureLoginCallbackHash } from '@/pages/loginCallbackHash';
import { AppRoutes } from './AppRoutes';
import { useAppAuthentication } from './useAppAuthentication';

function AppShell() {
  captureLoginCallbackHash();
  const dismissedUpdateBannerStorageItemName = 'kautuka-dismissed-update-notice-key';
  const autoReloadVersionStorageItemName = 'kautuka-last-seen-ui-version';
  const updateRestartStorageItemName = 'kautuka-update-restart';
  const updateRestartRef = React.useRef<{ until: number; targetVersion: string | null } | null>(null);
  const updateRestartLoadedRef = React.useRef(false);
  if (!updateRestartLoadedRef.current) {
    updateRestartLoadedRef.current = true;
    try {
      const saved = sessionStorage.getItem(updateRestartStorageItemName);
      const value: unknown = saved ? JSON.parse(saved) : null;
      if (value && typeof value === 'object' && 'until' in value && typeof value.until === 'number' &&
          'targetVersion' in value && (typeof value.targetVersion === 'string' || value.targetVersion === null)) {
        updateRestartRef.current = { until: value.until, targetVersion: value.targetVersion };
      }
    } catch {
      // The update status still works if session storage is unavailable.
    }
  }
  const [theme, setTheme] = React.useState<'light' | 'dark'>(() => {
    const stored = localStorage.getItem('kautuka-theme');
    return stored === 'light' || stored === 'dark' ? stored : 'light';
  });
  const { authenticated, authReady, serviceReady, needsSetup, loginSucceeded, logout } =
    useAppAuthentication();
  // Authentication bootstrap owns token restoration. Starting this query before bootstrap
  // completes races the cookie refresh and can invalidate a newly rotated refresh session.
  const { data: currentUser } = useCurrentUser({ enabled: authenticated });
  const [updateStatus, setUpdateStatus] = React.useState<UpdateStatusSummary | null>(null);
  const [updateStatusLoading, setUpdateStatusLoading] = React.useState(false);
  const [updateActionPending, setUpdateActionPending] = React.useState<
    'refresh' | 'apply' | null
  >(null);
  const [updateCheckFeedback, setUpdateCheckFeedback] = React.useState<string | null>(null);
  const [dismissedUpdateBannerKey, setDismissedUpdateBannerKey] = React.useState<string | null>(
    () => {
      try {
        return localStorage.getItem(dismissedUpdateBannerStorageItemName);
      } catch {
        return null;
      }
    }
  );

  // ── theme sync ───────────────────────────────────────────────────────────
  React.useEffect(() => {
    document.documentElement.classList.toggle('dark', theme === 'dark');
    localStorage.setItem('kautuka-theme', theme);
  }, [theme]);

  React.useEffect(() => {
    try {
      if (dismissedUpdateBannerKey) {
        localStorage.setItem(dismissedUpdateBannerStorageItemName, dismissedUpdateBannerKey);
      } else {
        localStorage.removeItem(dismissedUpdateBannerStorageItemName);
      }
    } catch {
      // Ignore storage failures and keep dismiss state in memory.
    }
  }, [dismissedUpdateBannerKey, dismissedUpdateBannerStorageItemName]);

  const isAdmin = currentUser?.role?.toLowerCase() === ROLE_ADMIN.toLowerCase();

  // System Setup Guard
  useOnboardingGuard(authenticated, isAdmin);

  const applyObservedUpdateStatus = React.useCallback(
    (nextStatus: UpdateStatusSummary | null) => {
      const restart = updateRestartRef.current;
      if (restart && nextStatus?.message === 'update-status-unavailable' && Date.now() < restart.until) {
        setUpdateStatus((previous) => ({
          ...(previous ?? nextStatus),
          ok: true,
          state: 'Applying',
          message: 'update-restarting',
          lastError: null,
        }));
        return;
      }
      const state = nextStatus?.state.toLowerCase();
      const version = nextStatus?.currentRelease?.version ?? nextStatus?.managerVersion;
      if (restart && (Date.now() >= restart.until ||
          (nextStatus?.ok === true && state === 'current' && version === restart.targetVersion) ||
          (state === 'available' && Boolean(nextStatus?.lastError)) ||
          (nextStatus?.ok === false && nextStatus.message !== 'update-status-unavailable'))) {
        updateRestartRef.current = null;
        try { sessionStorage.removeItem(updateRestartStorageItemName); } catch { /* Storage is optional. */ }
      }
      setUpdateStatus(nextStatus);
    },
    []
  );

  const refreshUpdateStatus = React.useCallback(async () => {
    setUpdateStatusLoading(true);

    try {
      const nextStatus = await fetchUpdateStatus();
      applyObservedUpdateStatus(nextStatus);
    } catch {
      applyObservedUpdateStatus(null);
    } finally {
      setUpdateStatusLoading(false);
    }
  }, [applyObservedUpdateStatus]);

  React.useEffect(() => {
    let cancelled = false;

    if (!authenticated) {
      setUpdateStatus(null);
      setUpdateStatusLoading(false);
      return undefined;
    }

    const poll = async () => {
      setUpdateStatusLoading(true);

      try {
        const nextStatus = await fetchUpdateStatus();
        if (!cancelled) {
          applyObservedUpdateStatus(nextStatus);
        }
      } catch {
        if (!cancelled) {
          applyObservedUpdateStatus(null);
        }
      } finally {
        if (!cancelled) {
          setUpdateStatusLoading(false);
        }
      }
    };

    void poll();
    const pollInterval = updateStatus?.state.toLowerCase() === 'applying' ? 3000 : 30000;
    const intervalId = window.setInterval(() => {
      void poll();
    }, pollInterval);

    return () => {
      cancelled = true;
      window.clearInterval(intervalId);
    };
  }, [applyObservedUpdateStatus, authenticated, updateStatus?.state]);

  const runUpdateAction = React.useCallback(
    async (action: 'refresh' | 'apply') => {
      if (action === 'refresh') {
        setUpdateCheckFeedback(null);
      }

      setUpdateActionPending(action);

      if (action === 'apply') {
        const restart = { until: Date.now() + 12 * 60 * 1000, targetVersion: updateStatus?.availableRelease?.version ?? null };
        updateRestartRef.current = restart;
        try { sessionStorage.setItem(updateRestartStorageItemName, JSON.stringify(restart)); } catch { /* Storage is optional. */ }
      }

      try {
        const nextStatus =
          action === 'refresh'
            ? await refreshUpdateDiscovery()
            : await applyUpdate();
        applyObservedUpdateStatus(nextStatus);

        if (
          action === 'refresh' &&
          nextStatus.ok === true &&
          nextStatus.state.toLowerCase() === 'current' &&
          nextStatus.eligibilityState?.toLowerCase() !== 'blocked'
        ) {
          setUpdateCheckFeedback('You’re up to date');
        }

      } catch {
        await refreshUpdateStatus();
      } finally {
        setUpdateActionPending(null);
      }
    },
    [applyObservedUpdateStatus, refreshUpdateStatus, updateStatus?.availableRelease?.version]
  );

  React.useEffect(() => {
    if (!updateCheckFeedback) {
      return undefined;
    }

    const timeoutId = window.setTimeout(() => setUpdateCheckFeedback(null), 5000);
    return () => window.clearTimeout(timeoutId);
  }, [updateCheckFeedback]);

  const updateState = updateStatus?.state.toLowerCase() ?? 'current';
  const updateEligibilityState = updateStatus?.eligibilityState?.toLowerCase() ?? 'none';
  const updateEligibilityReason = updateStatus?.eligibilityReason?.toLowerCase() ?? 'none';
  const hasPreviousRelease = Boolean(updateStatus?.currentRelease && updateStatus.lastKnownGoodRelease);
  const hasUpdateStatusFailure = updateStatus?.ok === false;
  const hasUpdateError =
    Boolean(updateStatus?.lastError) &&
    !(updateStatus?.ok === true && updateState === 'current' && hasPreviousRelease);
  const hasPreflightFailure =
    updateState === 'available' &&
    (updateStatus?.lastError?.toLowerCase().startsWith('update preflight checks failed:') ?? false);
  const updateBannerKey = React.useMemo(() => {
    if (!updateStatus) {
      return null;
    }

    const releaseId =
      updateStatus.availableRelease?.releaseId ??
      updateStatus.currentRelease?.releaseId ??
      updateStatus.managerVersion;

    if (hasPreflightFailure) {
      return `error:preflight:${releaseId}`;
    }

    if (hasUpdateStatusFailure || hasUpdateError) {
      return `error:${updateStatus.message}:${releaseId}`;
    }

    if (updateState === 'unsupported' || updateState === 'verificationfailed') {
      return `error:${updateState}:${releaseId}`;
    }

    return null;
  }, [
    hasPreflightFailure,
    hasUpdateError,
    hasUpdateStatusFailure,
    updateState,
    updateStatus?.message,
    updateStatus?.availableRelease?.releaseId,
    updateStatus?.currentRelease?.releaseId,
    updateStatus?.managerVersion,
  ]);
  const bannerDismissed = updateBannerKey !== null && updateBannerKey === dismissedUpdateBannerKey;
  const canDismissBanner = updateBannerKey !== null;
  const shouldShowUpdateBanner =
    authenticated &&
    isAdmin &&
    updateStatus !== null &&
    !bannerDismissed &&
    (hasUpdateStatusFailure ||
      hasPreflightFailure ||
      hasUpdateError ||
      updateState === 'staging' ||
      updateState === 'applying' ||
      updateState === 'unsupported' ||
      updateState === 'verificationfailed');

  React.useEffect(() => {
    if (!dismissedUpdateBannerKey || !updateBannerKey) {
      return;
    }

    if (dismissedUpdateBannerKey !== updateBannerKey) {
      setDismissedUpdateBannerKey(null);
    }
  }, [dismissedUpdateBannerKey, updateBannerKey]);

  const currentVersion =
    updateStatus?.currentRelease?.version ?? updateStatus?.managerVersion ?? 'unknown';

  React.useEffect(() => {
    const url = new URL(globalThis.location.href);
    if (!url.searchParams.has('__uiRelease')) {
      return;
    }

    url.searchParams.delete('__uiRelease');
    globalThis.history.replaceState(null, '', `${url.pathname}${url.search}${url.hash}`);
  }, []);

  React.useEffect(() => {
    if (!authenticated || updateStatus?.ok !== true || updateState !== 'current') {
      return;
    }

    if (!currentVersion || currentVersion === 'unknown') {
      return;
    }

    const previousVersion = globalThis.sessionStorage.getItem(autoReloadVersionStorageItemName);
    if (!previousVersion) {
      globalThis.sessionStorage.setItem(autoReloadVersionStorageItemName, currentVersion);
      return;
    }

    if (previousVersion === currentVersion) {
      return;
    }

    globalThis.sessionStorage.setItem(autoReloadVersionStorageItemName, currentVersion);

    const url = new URL(globalThis.location.href);
    url.searchParams.set('__uiRelease', currentVersion);
    globalThis.location.replace(url.toString());
  }, [
    authenticated,
    autoReloadVersionStorageItemName,
    currentVersion,
    updateState,
    updateStatus?.ok,
  ]);

  const dismissUpdateBanner = React.useCallback(() => {
    if (updateBannerKey) {
      setDismissedUpdateBannerKey(updateBannerKey);
    }
  }, [updateBannerKey]);

  if (!serviceReady) {
    return <StartupScreen mode={needsSetup === null ? 'starting' : 'reconnecting'} />;
  }

  if (!authReady) {
    return (
      <div className="flex h-screen items-center justify-center bg-background text-foreground">
        <PageState message="Restoring session…" />
      </div>
    );
  }

  const updateBannerTitle = (() => {
    if (hasUpdateStatusFailure) {
      switch (updateStatus?.message) {
        case 'update-apply-failed':
        case 'update-apply-unavailable':
          return 'Update apply failed';
        case 'update-refresh-failed':
        case 'update-refresh-unavailable':
          return 'Update refresh failed';
        case 'update-status-unavailable':
          return 'Update service unavailable';
        default:
          return 'Manager update status';
      }
    }

    if (hasPreflightFailure) {
      return 'Update preflight checks failed';
    }

    if (hasUpdateError && (updateState === 'applying' || updateState === 'available')) {
      return 'Update requires attention';
    }

    if (updateEligibilityState === 'blocked') {
      switch (updateEligibilityReason) {
        case 'newerstablerequired':
          return 'Waiting for newer stable release';
        case 'nonewerpreview':
          return 'No newer preview release available';
        case 'comparisonerror':
          return 'Update version comparison failed';
        case 'untrustedsourceurl':
          return 'Release found but its download source is not trusted';
        default:
          return 'Manager update status';
      }
    }

    switch (updateState) {
      case 'available':
        return `Update ready: ${updateStatus?.availableRelease?.version ?? 'new release'}`;
      case 'applying':
        return updateStatus?.message === 'update-restarting'
          ? 'Update in progress'
          : 'Update preflight checks in progress';
      case 'unsupported':
        return 'Release found but cannot be applied automatically';
      case 'current':
        return hasPreviousRelease
          ? `Update applied: ${updateStatus?.currentRelease?.version ?? updateStatus?.managerVersion}`
          : 'Manager update status';
      default:
        return 'Manager update status';
    }
  })();

  const updateBannerDescription = (() => {
    if (!updateStatus) {
      return '';
    }

    if (hasUpdateStatusFailure) {
      return updateStatus.lastError ?? updateStatus.message;
    }

    if (hasPreflightFailure) {
      return `${updateStatus.lastError} Fix the reported preflight issue and retry the update.`;
    }

    if (hasUpdateError && updateState === 'available') {
      return updateStatus.lastError ?? updateStatus.message;
    }

    if (updateEligibilityState === 'blocked') {
      switch (updateEligibilityReason) {
        case 'newerstablerequired':
          return `Update track is set to stable. The current release stays active until a stable release newer than ${updateStatus?.currentRelease?.version ?? updateStatus?.managerVersion ?? 'the installed version'} is available.`;
        case 'nonewerpreview':
          return `Update track is set to preview, but no preview release newer than ${updateStatus?.currentRelease?.version ?? updateStatus?.managerVersion ?? 'the installed version'} is available yet.`;
        case 'comparisonerror':
          return 'The updater could not compare the installed version with the discovered release version.';
        case 'untrustedsourceurl':
          return `${updateStatus.lastError ?? 'The discovered release is served from an untrusted source.'} The appliance downloads releases only over https or loopback http, so this release is not offered.`;
        default:
          return updateStatus.lastError ?? updateStatus.message;
      }
    }

    if (updateState === 'available') {
      return `Release ${updateStatus.availableRelease?.version ?? 'unknown'} is available. Apply downloads and verifies the release, then restarts Kautuka services to activate it.`;
    }

    if (updateState === 'staging') {
      return 'The update package is being downloaded and validated before activation can continue.';
    }

    if (updateState === 'applying' && updateStatus.lastError) {
      return updateStatus.lastError;
    }

    if (updateState === 'applying') {
      if (updateStatus.message === 'update-restarting') {
        return 'Kautuka is restarting its services. This page will reconnect automatically.';
      }
      return 'Apply has been requested. The staged updater is running preflight checks before it switches the appliance to the new release.';
    }

    return updateStatus.lastError ?? updateStatus.message;
  })();
  const sidebarUpdateTone: 'neutral' | 'available' | 'busy' | 'error' =
    hasUpdateStatusFailure ||
    hasPreflightFailure ||
    hasUpdateError ||
    updateState === 'unsupported' ||
    updateState === 'verificationfailed'
      ? 'error'
      : updateState === 'staging' || updateState === 'applying'
        ? 'busy'
        : updateState === 'available'
          ? 'available'
          : 'neutral';
  const sidebarUpdateTitle =
    updateStatus && isAdmin && !shouldShowUpdateBanner
      ? updateState === 'current' && !hasPreviousRelease && updateEligibilityState !== 'blocked'
        ? updateCheckFeedback
        : updateBannerTitle
      : undefined;
  const sidebarUpdateDescription =
    sidebarUpdateTitle === updateCheckFeedback
      ? `Checked just now. Version ${currentVersion} is current. Kautuka services restart when an update is applied.`
      : updateBannerDescription;

  return (
    <div className="relative flex h-screen overflow-hidden bg-background text-foreground">
      {authenticated && (
        <Header
          theme={theme}
          onToggleTheme={() => setTheme((prev) => (prev === 'light' ? 'dark' : 'light'))}
          authenticated={authenticated}
          onLogout={() => void logout()}
          currentVersion={currentVersion}
          canCheckForUpdates={isAdmin}
          isCheckingForUpdates={updateActionPending === 'refresh'}
          onCheckForUpdates={() => void runUpdateAction('refresh')}
          updateTitle={sidebarUpdateTitle ?? undefined}
          updateDescription={
            sidebarUpdateTitle ? (sidebarUpdateDescription ?? undefined) : undefined
          }
          updateTone={sidebarUpdateTone}
          availableVersion={updateStatus?.availableRelease?.version}
          releaseChannel={updateStatus?.effectiveReleaseChannel ?? undefined}
          canApplyUpdate={isAdmin && updateState === 'available'}
          isApplyingUpdate={updateActionPending === 'apply'}
          onApplyUpdate={() => void runUpdateAction('apply')}
        />
      )}

      <div className="flex min-w-0 min-h-0 flex-1 flex-col">
        <ExecutionReadinessBanner authenticated={authenticated} />
        {authenticated && <NetworkPausedBanner />}
        {shouldShowUpdateBanner && updateStatus ? (
          <div className="px-3 pt-3 sm:px-6">
            <Alert variant="attention" className="shadow-sm">
              <div className="flex flex-col gap-3 md:flex-row md:items-start md:justify-between">
                <div className="flex min-w-0 gap-3">
                  <div className="mt-0.5 rounded-full bg-attention/15 p-2 text-attention">
                    <AlertTriangle className="h-4 w-4" />
                  </div>
                  <div className="min-w-0">
                    <AlertTitle>{updateBannerTitle}</AlertTitle>
                    <AlertDescription>
                      <p>{updateBannerDescription}</p>
                    </AlertDescription>
                  </div>
                </div>
                <div className="flex shrink-0 items-center gap-2">
                  {canDismissBanner ? (
                    <Button
                      type="button"
                      variant="ghost"
                      size="icon"
                      onClick={dismissUpdateBanner}
                      className="h-8 w-8 text-attention/70 hover:bg-attention/10 hover:text-attention"
                      title="Dismiss update notice"
                    >
                      <X className="h-4 w-4" />
                    </Button>
                  ) : null}
                </div>
              </div>
            </Alert>
          </div>
        ) : null}
        <main className="flex-1 min-h-0 overflow-y-auto px-3 py-4 sm:px-6">
          <AppRoutes
            authenticated={authenticated}
            needsSetup={needsSetup}
            onLoginSuccess={loginSucceeded}
          />
        </main>
      </div>
    </div>
  );
}

export function App() {
  return (
    <BrowserRouter>
      <AppShell />
      <Toaster position="bottom-right" richColors closeButton />
    </BrowserRouter>
  );
}

export default App;
