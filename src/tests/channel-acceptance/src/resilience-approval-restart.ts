import { expect, type Page } from '@playwright/test';
import { connectProvider, openAgent, sendPrompt } from './flows.js';
import { OwnedStackLifecycle } from './lifecycle.js';
import { bootstrapPublicFixtures } from './public-fixture-bootstrap.js';
import { measureRestart, type RestartObservation } from './restart-evidence.js';
import type { ResilienceAcceptanceExecution } from './resilience-executor.js';
import { authenticateResilienceUser } from './resilience-fixture.js';
import { bootstrapSkillFixtures } from './skill-fixture-bootstrap.js';

export type ApprovalPendingRestartBoundary = 'Gateway' | 'ChannelHost' | 'ProviderSimulator';

/** Process restart while a skill approval is pending; request id must survive. */
export async function runApprovalPendingRestart(
  execution: ResilienceAcceptanceExecution,
  lifecycle: OwnedStackLifecycle,
  page: Page,
  boundary: ApprovalPendingRestartBoundary,
): Promise<Readonly<{ threadId: string; requestId: string; observation: RestartObservation }>> {
  const headers = await authenticateResilienceUser(page, execution.stack.baseUrl, execution.credentials);
  const fixtures = await bootstrapPublicFixtures(page, execution.stack.baseUrl, headers);
  const skills = await bootstrapSkillFixtures(page, execution.stack.baseUrl, fixtures, headers);

  await connectProvider(page, 'telegram', skills.childAgentId);
  await openAgent(page, execution.stack.baseUrl, skills.childAgentId);
  const threadId = new URL(page.url()).searchParams.get('sessionId');
  expect(threadId, 'approval-pending restart needs a canonical thread').toBeTruthy();

  await sendPrompt(page, `Roll a d23 for CRH-050-${boundary}-PENDING-RESTART. Do not simulate the roll.`);
  const panel = page.locator('[data-testid="hitl-approval"][data-tool-name="run_skill_script"]').first();
  await expect(panel).toBeVisible({ timeout: 90_000 });
  const requestId = await panel.getAttribute('data-approval-request-id');
  expect(requestId, 'pending skill approval must expose an immutable request id').toBeTruthy();

  const observation = await measureRestart(lifecycle, boundary, async () => {
    await page.reload();
    await expect(
      page.locator(`[data-testid="hitl-approval"][data-approval-request-id="${requestId}"]`),
      `${boundary} restart must restore the same pending approval request`,
    ).toBeVisible();
    await expect(page.locator('textarea').last()).toBeEnabled();
  });

  return { threadId: threadId!, requestId: requestId!, observation };
}

/** @deprecated Prefer runApprovalPendingRestart(..., 'Gateway'). */
export async function runApprovalPendingGatewayRestart(
  execution: ResilienceAcceptanceExecution,
  lifecycle: OwnedStackLifecycle,
  page: Page,
): Promise<Readonly<{ threadId: string; requestId: string; observation: RestartObservation }>> {
  return runApprovalPendingRestart(execution, lifecycle, page, 'Gateway');
}
