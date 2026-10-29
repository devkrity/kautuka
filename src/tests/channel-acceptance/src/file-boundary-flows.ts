import { expect, type Page } from '@playwright/test';
import { authenticatedBrowserHeaders } from './browser-auth.js';

export async function assertUnauthenticatedFileBoundary(baseUrl: URL, agentId: string): Promise<void> {
  const endpoint = new URL(`/api/agents/${encodeURIComponent(agentId)}/files`, baseUrl);
  const read = await fetch(new URL('?path=..%2F..%2F', endpoint), { credentials: 'omit' });
  const write = await fetch(endpoint, { method: 'POST', credentials: 'omit' });
  expect({ read: read.status, write: write.status }).toEqual({ read: 401, write: 401 });
}

export async function assertAuthenticatedFileSafety(page: Page, baseUrl: URL, agentId: string): Promise<void> {
  const headers = await authenticatedBrowserHeaders(page, baseUrl);
  const response = await page.context().request.get(new URL(`/api/agents/${encodeURIComponent(agentId)}/files/content?path=..%2F..%2Foutside.txt`, baseUrl).toString(), { headers });
  const result = { status: response.status(), body: (await response.text()).slice(0, 400) };
  expect(result.status, `path traversal must not expose content: ${result.body}`).toBeGreaterThanOrEqual(400);
  expect(result.body).not.toMatch(/[A-Za-z]:\\|\/etc\/|BEGIN [A-Z ]+ KEY/);
}
