import { expect, type Page } from '@playwright/test';
import { authenticatedBrowserHeaders } from './browser-auth.js';
import { NARAD_BASE } from './narad-types.js';

export async function assertOneTerminalParentResult(page: Page, toolName: string, expectedStatus: 'complete' | 'cancelled' | 'stopped'): Promise<void> {
  const parentTools = page.locator(`[data-testid="copilot-tool-render"][data-tool-name="${toolName}"]`);
  await expect(parentTools, 'one parent tool invocation is projected into the chat').toHaveCount(1);
  await expect(parentTools.first(), 'the parent invocation must reach one terminal result').toHaveAttribute('data-status', expectedStatus);
  await expect(page.locator('textarea').last(), 'terminal nested outcomes must return chat to an interactive state').toBeEnabled();
  await expect(page.locator('[data-testid="hitl-approval"]'), 'a terminal nested outcome cannot leave a stale approval projection').toHaveCount(0);
}

export async function assertNestedProjection(page: Page, baseUrl: URL, parentToolName: string, childToolName: string, expectedChildCount: number): Promise<Readonly<{ parentId: string; childIds: readonly string[] }>> {
  const sessionId = new URL(page.url()).searchParams.get('sessionId');
  if (!sessionId) throw new Error('Nested acceptance session id is missing from the visible browser URL.');
  const headers = await authenticatedBrowserHeaders(page, baseUrl);
  const agentId = new URL(page.url()).pathname.split('/').pop() ?? '';
  const response = await page.context().request.get(
    new URL(`${NARAD_BASE}/sessions/${encodeURIComponent(sessionId)}/events?agentId=${encodeURIComponent(agentId)}`, baseUrl).toString(),
    { headers },
  );
  expect(response.ok(), 'the Narad session history must be readable by its owner').toBeTruthy();
  const payload = await response.json() as { events?: Array<{ type?: string; toolCallId?: string; toolName?: string; parentToolCallId?: string | null }> };
  const calls = (payload.events ?? []).filter((event) => event.type === 'tool.call.started');
  const parents = calls.filter((call) => call.toolName === parentToolName);
  const children = calls.filter((call) => call.toolName === childToolName);
  expect(parents, 'the Narad journal has one parent tool call').toHaveLength(1);
  expect(children, 'the Narad journal has the expected sequential child skill calls').toHaveLength(expectedChildCount);
  expect(children.map((child) => child.parentToolCallId), 'every child remains attached to the same parent tool call').toEqual(Array(expectedChildCount).fill(parents[0]?.toolCallId));
  const parentId = parents[0]?.toolCallId;
  const childIds = children.map((child) => child.toolCallId);
  if (!parentId || childIds.some((id) => !id)) throw new Error('Narad journal omitted a terminal parent or child tool call id.');
  return { parentId, childIds: childIds as string[] };
}
