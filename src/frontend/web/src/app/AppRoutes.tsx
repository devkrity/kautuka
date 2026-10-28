import { PageState } from '@/components/layout/PageState';
import { StartupScreen } from '@/components/layout/StartupScreen';
import { useCurrentUser } from '@/features/auth';
import { ROLE_ADMIN } from '@/features/settings';
import { lazyImportWithRecovery } from '@/lib/lazyImportWithRecovery';
import React from 'react';
import { Navigate, Route, Routes, useLocation } from 'react-router-dom';
const LoginPage = React.lazy(() =>
  lazyImportWithRecovery('page:login', () =>
    import('@/pages/LoginPage').then((module) => ({ default: module.LoginPage }))
  )
);
const LoginCallbackPage = React.lazy(() =>
  lazyImportWithRecovery('page:login-callback', () =>
    import('@/pages/LoginCallbackPage').then((module) => ({ default: module.LoginCallbackPage }))
  )
);
const Dashboard = React.lazy(() =>
  lazyImportWithRecovery('page:dashboard', () =>
    import('@/pages/Dashboard').then((module) => ({ default: module.Dashboard }))
  )
);
const UsagePage = React.lazy(() =>
  lazyImportWithRecovery('page:usage', () =>
    import('@/pages/UsagePage').then((module) => ({ default: module.UsagePage }))
  )
);
const WorkspacePage = React.lazy(() =>
  lazyImportWithRecovery('page:workspace', () =>
    import('@/pages/AgentPageV2').then((module) => ({
      default: module.AgentPageV2,
    }))
  )
);
const ApprovalsPage = React.lazy(() =>
  lazyImportWithRecovery('page:approvals', () =>
    import('@/pages/ApprovalsPage').then((module) => ({ default: module.ApprovalsPage }))
  )
);
const ConnectorsPage = React.lazy(() =>
  lazyImportWithRecovery('page:connectors', () =>
    import('@/pages/ConnectorsPage').then((module) => ({ default: module.ConnectorsPage }))
  )
);
const SkillsPage = React.lazy(() =>
  lazyImportWithRecovery('page:skills', () =>
    import('@/pages/SkillsPage').then((module) => ({ default: module.SkillsPage }))
  )
);
const MemoriesPage = React.lazy(() =>
  lazyImportWithRecovery('page:memories', () =>
    import('@/pages/MemoriesPage').then((module) => ({ default: module.MemoriesPage }))
  )
);
const AutopilotPage = React.lazy(() =>
  lazyImportWithRecovery('page:autopilot', () =>
    import('@/pages/AutopilotPage').then((module) => ({ default: module.AutopilotPage }))
  )
);
const WorkflowsPage = React.lazy(() =>
  lazyImportWithRecovery('page:workflows', () =>
    import('@/pages/WorkflowsPage').then((module) => ({ default: module.WorkflowsPage }))
  )
);
const SettingsPage = React.lazy(() =>
  lazyImportWithRecovery('page:settings', () =>
    import('@/pages/SettingsPage').then((module) => ({ default: module.SettingsPage }))
  )
);
const OnboardingWizard = React.lazy(() =>
  lazyImportWithRecovery('page:onboarding', () =>
    import('@/features/onboarding/routes/OnboardingWizard').then((module) => ({
      default: module.OnboardingWizard,
    }))
  )
);

interface GuardProps {
  children: React.ReactElement;
}

function RequireAuth({
  authenticated,
  needsSetup,
  children,
}: Readonly<GuardProps & { authenticated: boolean; needsSetup: boolean | null }>) {
  const location = useLocation();

  if (needsSetup === null) {
    return <StartupScreen />;
  }

  if (!authenticated) {
    return (
      <Navigate
        to={needsSetup ? '/first-run' : '/login'}
        replace
        state={{ from: location.pathname }}
      />
    );
  }

  return children;
}

function RequireGuest({
  authenticated,
  children,
}: Readonly<GuardProps & { authenticated: boolean }>) {
  return authenticated ? <Navigate to="/dashboard" replace /> : children;
}

function RequireSetupResolved({
  needsSetup,
  children,
}: Readonly<GuardProps & { needsSetup: boolean | null }>) {
  return needsSetup === null ? <StartupScreen /> : children;
}

function RequireAdmin({ children }: Readonly<GuardProps>) {
  const { data: userProfile, isLoading, isError } = useCurrentUser();

  if (isLoading) {
    return <StartupScreen />;
  }

  const isAdmin = userProfile?.role?.toLowerCase() === ROLE_ADMIN.toLowerCase();
  return isError || !isAdmin ? <Navigate to="/dashboard" replace /> : children;
}

interface AppRoutesProps {
  authenticated: boolean;
  needsSetup: boolean | null;
  onLoginSuccess: () => void;
}

export function AppRoutes({ authenticated, needsSetup, onLoginSuccess }: Readonly<AppRoutesProps>) {
  const requireAuth = (page: React.ReactElement) => (
    <RequireAuth authenticated={authenticated} needsSetup={needsSetup}>
      {page}
    </RequireAuth>
  );
  const fallbackPath = authenticated ? '/dashboard' : needsSetup === true ? '/first-run' : '/login';

  return (
    <React.Suspense
      fallback={
        <div className="flex min-h-[50vh] items-center justify-center">
          <PageState message="Loading…" />
        </div>
      }
    >
      <Routes>
        <Route
          path="/login/callback"
          element={<LoginCallbackPage onLoginSuccess={onLoginSuccess} />}
        />
        <Route
          path="/first-run"
          element={
            <RequireSetupResolved needsSetup={needsSetup}>
              {authenticated ? (
                <Navigate to="/dashboard" replace />
              ) : (
                <LoginPage onLoginSuccess={onLoginSuccess} initialNeedsSetup />
              )}
            </RequireSetupResolved>
          }
        />
        {['/login', '/auth'].map((path) => (
          <Route
            key={path}
            path={path}
            element={
              <RequireSetupResolved needsSetup={needsSetup}>
                {needsSetup ? (
                  <Navigate to="/first-run" replace />
                ) : (
                  <RequireGuest authenticated={authenticated}>
                    <LoginPage onLoginSuccess={onLoginSuccess} initialNeedsSetup={false} />
                  </RequireGuest>
                )}
              </RequireSetupResolved>
            }
          />
        ))}
        <Route path="/dashboard" element={requireAuth(<Dashboard />)} />
        <Route path="/usage" element={requireAuth(<UsagePage />)} />
        <Route path="/workspace" element={requireAuth(<WorkspacePage />)} />
        <Route path="/workspace/:agentId" element={requireAuth(<WorkspacePage />)} />
        <Route path="/workflows" element={requireAuth(<WorkflowsPage />)} />
        <Route path="/workflows/:workflowId" element={requireAuth(<WorkflowsPage />)} />
        <Route path="/workflows/:workflowId/edit" element={requireAuth(<WorkflowsPage />)} />
        <Route
          path="/workflows/:workflowId/instances/:instanceId"
          element={requireAuth(<WorkflowsPage />)}
        />
        <Route path="/approvals" element={requireAuth(<ApprovalsPage />)} />
        <Route path="/connectors" element={requireAuth(<ConnectorsPage />)} />
        <Route path="/skills" element={requireAuth(<SkillsPage />)} />
        <Route path="/memories" element={requireAuth(<MemoriesPage />)} />
        <Route path="/autopilot" element={requireAuth(<AutopilotPage />)} />
        <Route
          path="/settings"
          element={requireAuth(
            <RequireAdmin>
              <SettingsPage />
            </RequireAdmin>
          )}
        />
        <Route
          path="/onboarding"
          element={requireAuth(
            <RequireAdmin>
              <OnboardingWizard />
            </RequireAdmin>
          )}
        />
        <Route path="/" element={<Navigate to={fallbackPath} replace />} />
        <Route path="*" element={<Navigate to={fallbackPath} replace />} />
      </Routes>
    </React.Suspense>
  );
}
