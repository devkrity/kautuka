import { expect, type Page } from '@playwright/test';

export async function authenticateAcceptanceUser(
  page: Page,
  baseUrl: URL,
  credentials: Readonly<{ username: string; password: string }>,
): Promise<Record<string, string>> {
  await page.goto(baseUrl.toString());
  const setup = page.locator('#setup-username');
  const username = page.locator('#username');
  const loginVisible = await Promise.race([
    setup.waitFor({ state: 'visible', timeout: 15_000 }).then(() => 'setup' as const),
    username.waitFor({ state: 'visible', timeout: 15_000 }).then(() => 'login' as const),
  ]).catch(() => null);
  if (loginVisible === null) {
    await page.waitForURL((url) => !/(login|first-run)/i.test(url.pathname), { timeout: 15_000 });
    return loginAuthorization(
      await (
        await page.request.post(`${baseUrl.origin}/api/auth/login`, {
          data: credentials,
        })
      ).json(),
    );
  }
  const login = page.waitForResponse((response) => response.request().method() === 'POST' && new URL(response.url()).pathname === '/api/auth/login');
  if (await setup.isVisible()) {
    await setup.fill(credentials.username);
    await page.locator('#setup-password').fill(credentials.password);
    await page.locator('#setup-confirm').fill(credentials.password);
    await page.getByRole('button', { name: /create admin account/i }).click();
  } else {
    await page.locator('#username').fill(credentials.username);
    await page.locator('#secret-phrase').fill(credentials.password);
    await page.getByRole('button', { name: /sign in/i }).click();
  }
  const loginResponse = await login;
  await page.waitForURL((url) => !/(login|first-run)/i.test(url.pathname));
  await expect
    .poll(async () => (await page.context().cookies(baseUrl.origin)).some((cookie) => cookie.name === 'kautuka_refresh_token'), {
      message: 'browser login must establish a refresh cookie for reload-safe acceptance navigation',
    })
    .toBe(true);
  return loginAuthorization(await loginResponse.json());
}

export function loginAuthorization(payload: unknown): Record<string, string> {
  const token = typeof payload === 'object' && payload !== null ? (payload as { accessToken?: unknown }).accessToken : undefined;
  if (typeof token !== 'string' || !token.trim()) throw new Error('Normal browser login did not return an access token.');
  return { Authorization: `Bearer ${token}` };
}
