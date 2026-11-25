import { useSettingsStatus } from './useSettingsStatus';
import { isActiveOnboardingHandoff } from '../utils/onboardingHandoff';
import { useEffect } from 'react';
import { useNavigate, useLocation } from 'react-router-dom';

/**
 * Keeps first-run setup available until the administrator has configured a helper.
 */
export function useOnboardingGuard(authenticated: boolean, isAdmin: boolean) {
  const navigate = useNavigate();
  const location = useLocation();
  const isSuccessfulWizardHandoff = isActiveOnboardingHandoff(location.state);

  const { data: status, isLoading, isFetching } = useSettingsStatus(authenticated && isAdmin);

  useEffect(() => {
    // Skip while any fetch is in-flight (initial load OR background refetch after invalidation).
    // Without this, the guard fires with stale cached data (0 providers) while the
    // post-wizard refetch is still running, causing an infinite redirect loop.
    if (!authenticated || !isAdmin || isLoading || isFetching || !status) return;

    // The wizard's "Start Using Kautuka Now" action is an intentional one-time handoff
    // to /dashboard after a successful save flow. Respect that explicit navigation.
    if (isSuccessfulWizardHandoff) {
      return;
    }

    const isSystemEmpty = status.providersCount === 0 || status.hasConfiguredHelper === false;
    const isSetupCapableRoute =
      location.pathname === '/onboarding' || location.pathname === '/settings';

    if (isSystemEmpty && !isSetupCapableRoute) {
      navigate('/onboarding', { replace: true });
    }
  }, [
    authenticated,
    isAdmin,
    status,
    isLoading,
    isFetching,
    location.pathname,
    navigate,
    isSuccessfulWizardHandoff,
  ]);

  return { isLoading };
}
