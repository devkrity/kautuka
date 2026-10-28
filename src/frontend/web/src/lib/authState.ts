const PROACTIVE_REFRESH_LEAD_MS = 60_000;

export interface AuthRefreshPayload {
  accessToken: string;
  tokenType: string;
  expiresInSeconds: number;
}

type AccessSession = Pick<AuthRefreshPayload, 'accessToken' | 'tokenType'> & {
  expiresAt: number;
};

let accessSession: AccessSession | null = null;
let authFailureHandler: ((error: Error) => void) | null = null;
let authFailureReported = false;
let refreshSessionPromise: Promise<AuthRefreshPayload | null> | null = null;
let proactiveRefreshTimer: ReturnType<typeof globalThis.setTimeout> | null = null;

export function getAccessToken(): string | null {
  return accessSession?.accessToken ?? null;
}

function cancelProactiveRefresh(): void {
  if (proactiveRefreshTimer !== null) {
    globalThis.clearTimeout(proactiveRefreshTimer);
    proactiveRefreshTimer = null;
  }
}

function scheduleProactiveRefresh(expiresAt: number): void {
  cancelProactiveRefresh();
  const delay = Math.max(0, expiresAt - Date.now() - PROACTIVE_REFRESH_LEAD_MS);
  proactiveRefreshTimer = globalThis.setTimeout(() => {
    proactiveRefreshTimer = null;
    void refreshAuthSession().then((refreshed) => {
      if (!refreshed) {
        reportAuthFailure(new Error('Authentication session expired.'));
      }
    });
  }, delay);
}

export function setAccessToken(token: null): void;
export function setAccessToken(token: string, expiresAt: number): void;
export function setAccessToken(token: string | null, expiresAt?: number): void {
  if (token === null) {
    accessSession = null;
    cancelProactiveRefresh();
  } else {
    if (expiresAt === undefined || !Number.isFinite(expiresAt)) {
      throw new Error('An explicit access-token expiry is required.');
    }
    accessSession = { accessToken: token, tokenType: 'bearer', expiresAt };
    authFailureReported = false;
    scheduleProactiveRefresh(expiresAt);
  }
}

export function reportAuthFailure(error: Error): void {
  if (authFailureReported) {
    return;
  }
  authFailureReported = true;
  authFailureHandler?.(error);
}

export function refreshAuthSession(): Promise<AuthRefreshPayload | null> {
  refreshSessionPromise ??= fetch('/api/auth/refresh', {
    method: 'POST',
    credentials: 'include',
    headers: { 'Content-Type': 'application/json' },
  })
    .then(async (response) => {
      if (!response.ok) {
        setAccessToken(null);
        return null;
      }

      const payload: unknown = await response.json();
      if (
        !payload ||
        typeof payload !== 'object' ||
        typeof (payload as Record<string, unknown>).accessToken !== 'string' ||
        typeof (payload as Record<string, unknown>).expiresInSeconds !== 'number' ||
        !Number.isInteger((payload as Record<string, unknown>).expiresInSeconds) ||
        ((payload as Record<string, number>).expiresInSeconds ?? 0) <= 0
      ) {
        setAccessToken(null);
        return null;
      }

      const refreshed = {
        accessToken: (payload as Record<string, string>).accessToken,
        expiresInSeconds: (payload as Record<string, number>).expiresInSeconds,
        tokenType:
          typeof (payload as Record<string, unknown>).tokenType === 'string'
            ? (payload as Record<string, string>).tokenType
            : 'bearer',
      };
      setAccessToken(refreshed.accessToken, Date.now() + refreshed.expiresInSeconds * 1000);
      return refreshed;
    })
    .catch(() => {
      setAccessToken(null);
      return null;
    })
    .finally(() => {
      refreshSessionPromise = null;
    });

  return refreshSessionPromise;
}

export function setAuthFailureHandler(handler: ((error: Error) => void) | null): void {
  authFailureHandler = handler;
}
