import { expect, type Page, type Request } from '@playwright/test';

function observeBearerAuthorization(page: Page, onAuthorization: (value: string) => void): () => void {
  const observe = (request: Request): void => {
    const header = request.headers().authorization;
    if (header?.startsWith('Bearer ')) onAuthorization(header);
  };
  page.on('request', observe);
  return () => page.off('request', observe);
}

/** Wait until a full reload has finished cookie refresh and the UI is issuing bearer requests. */
export async function waitForAuthenticatedBrowserSession(page: Page): Promise<void> {
  let authorization: string | undefined;
  const stopObserving = observeBearerAuthorization(page, (header) => {
    authorization = header;
  });
  try {
    await expect(page, 'authenticated navigation must not land on the login page').not.toHaveURL(/\/login/i, { timeout: 60_000 });
    await expect
      .poll(() => authorization, {
        message: 'the authenticated browser must finish refresh bootstrap before the next navigation',
        timeout: 60_000,
      })
      .toMatch(/^Bearer\s+.+/);
  } finally {
    stopObserving();
  }
}

/**
 * Reuse the browser's established application session instead of racing the
 * refresh endpoint from a second API client. The token is observed only on a
 * normal authenticated UI request and is never written to acceptance evidence.
 */
export async function authenticatedBrowserHeaders(page: Page, baseUrl: URL): Promise<Record<string, string>> {
  let authorization: string | undefined;
  const stopObserving = observeBearerAuthorization(page, (header) => {
    authorization = header;
  });
  try {
    await page.goto(new URL('/settings', baseUrl).toString());
    await expect.poll(() => authorization, 'the authenticated browser must issue its own bearer request').toMatch(/^Bearer\s+.+/);
    return { Authorization: authorization! };
  } finally {
    stopObserving();
  }
}
