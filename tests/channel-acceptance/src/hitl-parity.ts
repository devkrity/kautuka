import { expect, type Page } from '@playwright/test';
import { authenticatedBrowserHeaders } from './browser-auth.js';
import { assertDiceMentionVisible } from './dice-fixture.js';
import type { ApprovalAction, Provider } from './model.js';
import { approvalActionSelector, expectedDecision } from './approval-action-vocabulary.js';
export { approvalActionSelector, expectedDecision } from './approval-action-vocabulary.js';

export type ApprovalSurface = 'chat' | 'approvals';

interface PublicApprovalProjection {
  readonly id: string;
  readonly runId?: string;
  readonly decision?: 'approved' | 'denied' | 'stopped' | 'expired';
  readonly decidedBy?: string;
  readonly decidedByDisplayName?: string;
  readonly resolutionSource?: string;
}

export async function publicApprovalProjection(
  page: Page,
  baseUrl: URL,
  threadId: string,
  headers?: Record<string, string>,
): Promise<readonly PublicApprovalProjection[]> {
  const authHeaders = headers ?? await authenticatedBrowserHeaders(page, baseUrl);
  const response = await page.context().request.get(
    new URL(`/api/approvals/session/${encodeURIComponent(threadId)}`, baseUrl).toString(),
    { headers: authHeaders },
  );
  expect(response.ok(), `approval projection for ${threadId} returned HTTP ${response.status()}`).toBeTruthy();
  return await response.json() as readonly PublicApprovalProjection[];
}

export async function waitForResolvedPublicApproval(
  page: Page,
  baseUrl: URL,
  threadId: string,
  action: ApprovalAction,
  source: string,
  headers?: Record<string, string>,
): Promise<PublicApprovalProjection> {
  let resolved: PublicApprovalProjection | undefined;
  await expect.poll(async () => {
    const approvals = await publicApprovalProjection(page, baseUrl, threadId, headers);
    resolved = approvals.find((approval) => approval.decision === expectedDecision(action));
    return resolved?.decision;
  }, {
    timeout: 180_000,
    message: 'the public approval projection must expose exactly the canonical decision',
  }).toBe(expectedDecision(action));

  expect(resolved!.resolutionSource, 'the public projection must retain the resolution surface').toBe(source);
  expect(resolved!.decidedBy ?? resolved!.decidedByDisplayName,
    'the public projection must retain the canonical actor').toBeTruthy();
  return resolved!;
}

export async function resolveApprovalFromSurface(
  page: Page,
  baseUrl: URL,
  surface: ApprovalSurface,
  action: ApprovalAction,
): Promise<void> {
  const selector = approvalActionSelector(action);
  if (surface === 'approvals') {
    await page.goto(new URL('/approvals', baseUrl).toString());
    const card = page.locator('[data-testid="pending-approval"]').first();
    await expect(card).toBeVisible();
    await card.locator(selector).click();
    await expect(card).toBeHidden();
    return;
  }

  await expect(page.locator(selector).first()).toBeVisible({ timeout: 180_000 });
  await page.locator(selector).first().click();
  await expect(page.locator(selector).first()).toBeHidden();
}

export async function assertTerminalBrowserState(page: Page, action: ApprovalAction, dice: number): Promise<void> {
  if (expectedDecision(action) === 'approved') {
    await assertDiceMentionVisible(page, dice);
  } else if (expectedDecision(action) === 'denied') {
    // Deny feeds the tool outcome and lets the model continue — no forced terminal phrase.
    await expect(page.locator('[data-approval-action]').first()).toBeHidden();
  } else {
    await expect(page.getByText(/stopped/i).last()).toBeVisible();
  }
  await expect(page.locator('[data-approval-action]').first()).toBeHidden();
  await expect(page.locator('textarea').last()).toBeEnabled();
}

export function approvalResolutionActivity(action: ApprovalAction, source: string): RegExp {
  const decision = expectedDecision(action);
  return new RegExp(`${decision}.*via ${source}`, 'i');
}

export function providerSource(provider: Provider): string {
  return provider;
}
