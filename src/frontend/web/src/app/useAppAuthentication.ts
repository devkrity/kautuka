import {
  logout as authLogout,
  refreshViaCookie,
  setAccessToken,
  setAuthFailureHandler,
} from '@/features/auth';
import { queryClient } from '@/lib/queryClient';
import React from 'react';
import { useNavigate } from 'react-router-dom';

interface GatewayAvailability {
  reachable: boolean;
  needsSetup: boolean | null;
}

async function probeGatewayAvailability(signal: AbortSignal): Promise<GatewayAvailability> {
  const response = await fetch('/api/auth/setup/status', { signal });
  if (!response.ok) {
    throw new Error(`Gateway probe failed with HTTP ${response.status}`);
  }

  const body = (await response.json()) as { needsSetup?: boolean };
  return {
    reachable: typeof body.needsSetup === 'boolean',
    needsSetup: typeof body.needsSetup === 'boolean' ? body.needsSetup : null,
  };
}

export interface AppAuthenticationState {
  authenticated: boolean;
  authReady: boolean;
  serviceReady: boolean;
  needsSetup: boolean | null;
  loginSucceeded: () => void;
  logout: () => Promise<void>;
}

export function useAppAuthentication(): AppAuthenticationState {
  const navigate = useNavigate();
  const [serviceReady, setServiceReady] = React.useState(false);
  const [authReady, setAuthReady] = React.useState(false);
  const [authenticated, setAuthenticated] = React.useState(false);
  const [needsSetup, setNeedsSetup] = React.useState<boolean | null>(null);

  React.useEffect(() => {
    let cancelled = false;
    let consecutiveFailures = 0;

    const poll = async () => {
      while (!cancelled) {
        const controller = new AbortController();
        const timeoutId = window.setTimeout(() => controller.abort(), 8000);

        try {
          const gateway = await probeGatewayAvailability(controller.signal);
          window.clearTimeout(timeoutId);
          consecutiveFailures = 0;
          if (!cancelled && gateway.reachable) {
            setNeedsSetup(gateway.needsSetup);
            setServiceReady(true);
          }
        } catch {
          window.clearTimeout(timeoutId);
          consecutiveFailures += 1;
          if (!cancelled && consecutiveFailures >= 4) {
            setServiceReady(false);
          }
        }

        await new Promise<void>((resolve) => window.setTimeout(resolve, 2000));
      }
    };

    void poll();
    return () => {
      cancelled = true;
    };
  }, []);

  React.useEffect(() => {
    setAuthFailureHandler(() => {
      setAccessToken(null);
      setAuthenticated(false);
      navigate(needsSetup ? '/first-run' : '/login', { replace: true });
    });
    return () => setAuthFailureHandler(null);
  }, [navigate, needsSetup]);

  React.useEffect(() => {
    if (!serviceReady) {
      return undefined;
    }

    let active = true;
    const bootstrapAuth = async () => {
      if (needsSetup === true) {
        if (active) {
          setAuthenticated(false);
          setAuthReady(true);
        }
        return;
      }

      try {
        const response = await refreshViaCookie();
        if (response.accessToken && active) {
          setAuthenticated(true);
          setAuthReady(true);
          return;
        }
      } catch {
        // An absent or expired refresh cookie is the normal guest path.
      }

      if (active) {
        setAuthenticated(false);
        setAuthReady(true);
      }
    };

    void bootstrapAuth();
    return () => {
      active = false;
    };
  }, [serviceReady, needsSetup]);

  const logout = React.useCallback(async () => {
    try {
      await authLogout();
    } catch (error) {
      console.warn('Logout request failed', error);
    }
    queryClient.clear();
    setAccessToken(null);
    setAuthenticated(false);
    navigate('/login', { replace: true });
  }, [navigate]);

  const loginSucceeded = React.useCallback(() => {
    queryClient.clear();
    setAuthenticated(true);
  }, []);

  return {
    authenticated,
    authReady,
    serviceReady,
    needsSetup,
    loginSucceeded,
    logout,
  };
}
