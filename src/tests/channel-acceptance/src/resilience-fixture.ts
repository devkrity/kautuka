import { expect, type APIResponse, type Page } from '@playwright/test';

export async function authenticateResilienceUser(
  page: Page,
  baseUrl: URL,
  credentials: { username: string; password: string },
): Promise<Record<string, string>> {
  await page.goto(baseUrl.toString());
  const setup = page.locator('#setup-username');
  await Promise.race([setup.waitFor({ state: 'visible' }), page.locator('#username').waitFor({ state: 'visible' })]);
  const loginResponse = page.waitForResponse((response) =>
    response.request().method() === 'POST' && new URL(response.url()).pathname === '/api/auth/login');
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
  await page.waitForURL((url) => !/login/i.test(url.pathname));
  const payload = await (await loginResponse).json() as { accessToken?: unknown };
  if (typeof payload.accessToken !== 'string' || !payload.accessToken) {
    throw new Error('Browser login response did not contain an access token.');
  }
  return { Authorization: `Bearer ${payload.accessToken}` };
}

export async function ensureResilienceAgent(page: Page, baseUrl: URL, headers: Record<string, string>): Promise<string> {
  const agentId = 'acceptance-assistant';
  const request = page.context().request;
  await requireSuccess(await request.post(new URL('/api/settings/providers/save', baseUrl).toString(), {
    headers,
    data: { id: 'acceptance-ollama', type: 'ollama', settings: { base_url: 'http://127.0.0.1:11434' } },
  }), 'create acceptance provider');
  await requireSuccess(await request.post(new URL('/api/settings/models/save', baseUrl).toString(), {
    headers,
    data: { id: 'acceptance-model', providerId: 'acceptance-ollama', modelName: 'acceptance-model', settings: {} },
  }), 'create acceptance model');
  await requireSuccess(await request.put(new URL(`/api/settings/agents/${agentId}`, baseUrl).toString(), {
    headers,
    data: {
      name: 'Assistant', modelId: 'acceptance-model', systemPrompt: 'Acceptance fixture', mcpBindings: [], skillAttachments: [],
      session: { historyEnabled: true, maxHistoryRuns: 5, summariesEnabled: false },
      sandbox: { enabled: false, allowedLanguages: [], maxMemory: '2g', maxTimeout: 120 },
      approvalConfig: { requireMcpIds: [], requireMcpFunctionFilters: {}, disabledMcpFunctions: {}, timeoutSeconds: 300 },
    },
  }), 'create acceptance agent');
  return agentId;
}

export async function openProfile(page: Page): Promise<void> {
  if (!await page.getByRole('heading', { name: 'Your Profile' }).isVisible()) await page.getByTitle('Open profile').click();
  await expect(page.getByRole('heading', { name: 'Your Profile' })).toBeVisible();
}

export function channelCard(page: Page, displayName: string) {
  return page.getByRole('heading', { name: displayName, exact: true }).locator('..').locator('..').locator('..');
}

async function requireSuccess(response: APIResponse, operation: string): Promise<void> {
  if (!response.ok()) throw new Error(`${operation} failed with ${response.status()}: ${(await response.text()).slice(0, 500)}`);
}
