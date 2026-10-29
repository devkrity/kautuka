import { expect } from '@playwright/test';

export const verifyWebBaseUrl = process.env.KAUTUKA_ACCEPTANCE_WEB_BASE_URI ?? 'http://127.0.0.1:20229/';

export async function login(page: import('@playwright/test').Page): Promise<Record<string, string>> {
  const origin = new URL(verifyWebBaseUrl).origin;
  await page.goto(verifyWebBaseUrl);
  const setup = page.locator('#setup-username');
  const username = page.locator('#username');
  const loginVisible = await Promise.race([
    setup.waitFor({ state: 'visible', timeout: 15_000 }).then(() => 'setup' as const),
    username.waitFor({ state: 'visible', timeout: 15_000 }).then(() => 'login' as const),
  ]).catch(() => null);

  if (loginVisible === 'setup') {
    await setup.fill('admin');
    await page.locator('#setup-password').fill('passw0rd');
    await page.locator('#setup-confirm').fill('passw0rd');
    await page.getByRole('button', { name: /create admin account/i }).click();
  } else if (loginVisible === 'login') {
    await username.fill('admin');
    await page.locator('#secret-phrase').fill('passw0rd');
    await page.getByRole('button', { name: /sign in/i }).click();
  }

  await page.waitForURL((url) => !/(login|first-run)/i.test(url.pathname), { timeout: 30_000 });
  const loginResponse = await page.request.post(`${origin}/api/auth/login`, {
    data: { username: 'admin', password: 'passw0rd' },
  });
  expect(loginResponse.ok()).toBeTruthy();
  const auth = await loginResponse.json() as { accessToken?: string };
  return { Authorization: `Bearer ${auth.accessToken}` };
}

export async function findMyAssistant(headers: Record<string, string>): Promise<string> {
  const origin = new URL(verifyWebBaseUrl).origin;
  const agentsResponse = await fetch(`${origin}/api/settings/agents`, { headers });
  expect(agentsResponse.ok).toBeTruthy();
  const agentsPayload = await agentsResponse.json() as { agents?: Array<{ id?: string; name?: string }> };
  const assistant = (agentsPayload.agents ?? []).find((agent) => agent.name === 'My Assistant');
  expect(assistant?.id, 'My Assistant agent must exist').toBeTruthy();
  return assistant!.id!;
}

export async function newSession(page: import('@playwright/test').Page, agentId: string): Promise<string> {
  const origin = new URL(verifyWebBaseUrl).origin;
  await page.goto(`${origin}/workspace/${encodeURIComponent(agentId)}`);
  await expect(page.getByText('Agent Workspace')).toBeVisible({ timeout: 60_000 });
  const created = page.waitForResponse(
    (response) => response.request().method() === 'POST' && new URL(response.url()).pathname === '/api/sessions',
    { timeout: 15_000 },
  );
  await page.getByRole('button', { name: 'New Session' }).click();
  const createdSession = await (await created).json() as { id?: string; sessionId?: string };
  const sessionId = createdSession.id ?? createdSession.sessionId;
  expect(sessionId).toBeTruthy();
  await expect.poll(() => new URL(page.url()).searchParams.get('sessionId')).toBe(sessionId);
  return sessionId!;
}

export async function sendPrompt(page: import('@playwright/test').Page, prompt: string): Promise<number> {
  const composer = page.locator('textarea').last();
  const start = Date.now();
  await composer.fill(prompt);
  await composer.press('Enter');
  await expect(page.getByText(prompt, { exact: false }).last()).toBeVisible({ timeout: 5_000 });
  return Date.now() - start;
}
