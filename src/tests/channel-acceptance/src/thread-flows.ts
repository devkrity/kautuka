import { expect, type Page } from '@playwright/test';
import { waitForAuthenticatedBrowserSession } from './browser-auth.js';
import { readCreatedThreadId } from './thread-creation.js';
import { runBoundedUiAction } from './ui-actions.js';

export async function openAgent(page: Page, baseUrl: URL, agentId: string): Promise<string> {
  await page.goto(new URL(`/workspace/${encodeURIComponent(agentId)}`, baseUrl).toString());
  await waitForAuthenticatedBrowserSession(page);
  await expect(page.getByText('Agent Workspace')).toBeVisible({ timeout: 60_000 });
  const agentPicker = page.locator('select').first();
  await expect(agentPicker, 'workspace agent picker must load').toBeVisible({ timeout: 60_000 });
  await expect
    .poll(async () => agentPicker.inputValue(), { message: 'workspace must resolve the requested agent' })
    .toBe(agentId);
  const newSession = page.getByRole('button', { name: 'New Session' }).first();
  await expect(newSession).toBeVisible({ timeout: 60_000 });
  const created = page.waitForResponse(
    (response) =>
      response.request().method() === 'POST'
      && new URL(response.url()).pathname === '/api/sessions',
    { timeout: 15_000 },
  );
  await runBoundedUiAction('new session', (timeout) => newSession.click({ timeout }));
  const createdSessionId = readCreatedThreadId(await (await created).json());
  await expect
    .poll(() => new URL(page.url()).searchParams.get('sessionId'), 'workspace URL must attach the created session')
    .toBe(createdSessionId);
  await expect(page.locator('textarea').last()).toBeVisible();
  await expect(page.locator('textarea').last()).toBeEnabled();
  return createdSessionId;
}

export async function openExistingThread(page: Page, baseUrl: URL, agentId: string, sessionId: string): Promise<void> {
  await page.goto(
    new URL(
      `/workspace/${encodeURIComponent(agentId)}?sessionId=${encodeURIComponent(sessionId)}`,
      baseUrl,
    ).toString(),
  );
  await expect.poll(() => new URL(page.url()).searchParams.get('sessionId')).toBe(sessionId);
  await expect(page.locator('textarea').last()).toBeVisible({ timeout: 60_000 });
}

export async function currentThreadIds(page: Page, baseUrl: URL, agentId: string, headers: Record<string, string>): Promise<Set<string>> {
  const response = await page.context().request.get(new URL(`/api/sessions/agents/${encodeURIComponent(agentId)}`, baseUrl).toString(), { headers });
  expect(response.ok(), `session catalogue returned HTTP ${response.status()}`).toBeTruthy();
  const payload = await response.json() as { sessions?: Array<{ id?: unknown }> };
  return new Set((payload.sessions ?? []).map((session) => session.id).filter((id): id is string => typeof id === 'string'));
}

export async function waitForNewThread(page: Page, baseUrl: URL, agentId: string, headers: Record<string, string>, previous: ReadonlySet<string>): Promise<string> {
  let discovered: string | undefined;
  await expect.poll(async () => {
    discovered = [...await currentThreadIds(page, baseUrl, agentId, headers)].find((id) => !previous.has(id));
    return discovered;
  }, 'provider ingress must create one canonical agent thread').toBeTruthy();
  return discovered!;
}

export async function sendPrompt(page: Page, prompt: string): Promise<void> {
  const composer = page.locator('textarea').last();
  await runBoundedUiAction('composer fill', (timeout) => composer.fill(prompt, { timeout }));
  await runBoundedUiAction('composer submit', (timeout) => composer.press('Enter', { timeout }));
  await expect(page.getByText(prompt, { exact: false }).last()).toBeVisible();
}
