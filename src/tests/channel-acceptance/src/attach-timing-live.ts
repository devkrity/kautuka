import { expect, type Page } from '@playwright/test';
import { assertDiceMentionVisible } from './dice-fixture.js';
import {
  injectProviderApproval,
  openAgent,
  openExistingThread,
  sendPrompt,
  waitForProviderAction,
} from './flows.js';
import type { Provider, ProviderSimulatorControl } from './model.js';
import { waitForResolvedPublicApproval, providerSource } from './hitl-parity.js';
import { createPausedProviderApproval } from './attach-timing-shared.js';

/** Browser is live on a thread before the approval-requiring turn starts. */
export async function executeAttachBeforeRequest(
  page: Page,
  baseUrl: URL,
  fixtures: import('./public-fixture-bootstrap.js').AcceptanceFixtures,
  scenarioSeed: string,
): Promise<void> {
  await openAgent(page, baseUrl, fixtures.rollerId);
  const threadId = new URL(page.url()).searchParams.get('sessionId');
  expect(threadId, 'attach-before-request requires a live canonical thread').toBeTruthy();
  await expect(page.locator('textarea').last()).toBeEnabled();

  const prompt = `Roll a d23 for attach-before-${scenarioSeed}. Do not simulate the roll.`;
  await sendPrompt(page, prompt);
  const panel = page.locator('[data-testid="hitl-approval"]').first();
  await expect(panel, 'pre-attached browser must observe the approval without reload').toBeVisible();
  const requestId = await panel.getAttribute('data-approval-request-id');
  expect(requestId).toBeTruthy();
  await panel.locator('[data-approval-action="approve"]').click();
  await waitForResolvedPublicApproval(page, baseUrl, threadId!, 'approve', 'chat');
  await assertDiceMentionVisible(page, 23);
  await expect(page.locator(`[data-testid="hitl-approval"][data-approval-request-id="${requestId}"]`)).toHaveCount(0);
}

/** Browser stays attached while channel resolves; continuation converges without reload. */
export async function executeAttachDuringContinuation(
  page: Page,
  baseUrl: URL,
  simulator: ProviderSimulatorControl,
  fixtures: import('./public-fixture-bootstrap.js').AcceptanceFixtures,
  provider: Provider,
  scenarioSeed: string,
): Promise<void> {
  const scenarioId = `attach-continuation-${scenarioSeed}-${provider}`;
  const { threadId, requestId, destination, headers } = await createPausedProviderApproval(
    page, baseUrl, simulator, fixtures, provider, scenarioId);

  const streamConnection = page.waitForRequest((request) =>
    new URL(request.url()).pathname === `/api/narad/v1/sessions/${threadId}/stream`);
  await openExistingThread(page, baseUrl, fixtures.rollerId, threadId);
  await streamConnection;
  await expect(page.locator(`[data-testid="hitl-approval"][data-approval-request-id="${requestId}"]`)).toBeVisible();

  const providerAction = await waitForProviderAction(simulator, provider, 'approve');
  const attachWithinFiveSeconds = page.waitForRequest(
    (request) =>
      request.method() === 'GET'
      && new URL(request.url()).pathname === `/api/narad/v1/sessions/${threadId}/stream`,
    { timeout: 5_000 },
  );
  await injectProviderApproval(simulator, provider, providerAction, destination);
  await attachWithinFiveSeconds;
  await waitForResolvedPublicApproval(page, baseUrl, threadId, 'approve', providerSource(provider), headers);
  await assertDiceMentionVisible(page, 23);
  await expect(page.locator(`[data-testid="hitl-approval"][data-approval-request-id="${requestId}"]`)).toHaveCount(0);
  await expect(page.locator('textarea').last()).toBeEnabled();
}
