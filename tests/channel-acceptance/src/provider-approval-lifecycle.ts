import { expect, type Page } from '@playwright/test';
import { authenticatedBrowserHeaders } from './browser-auth.js';
import {
  connectProvider,
  currentThreadIds,
  injectProviderApproval,
  openExistingThread,
  waitForNewThread,
  waitForProviderAction,
} from './flows.js';
import type { ApprovalAction, Provider, ProviderSimulatorControl } from './model.js';
import { providerConnectionCount, providerMessage } from './provider-events.js';
import { scenarioProviderDestination } from './provider-fixtures.js';
import { assertDiceMentionVisible } from './dice-fixture.js';
import { assertTerminalBrowserState, expectedDecision, providerSource, waitForResolvedPublicApproval } from './hitl-parity.js';

export async function executeProviderApproval(
  page: Page,
  baseUrl: URL,
  simulator: ProviderSimulatorControl,
  fixtures: import('./public-fixture-bootstrap.js').AcceptanceFixtures,
  provider: Provider,
  scenarioSeed: string,
  action: ApprovalAction,
  reconnect: boolean,
): Promise<void> {
  const scenarioId = `lifecycle-${scenarioSeed}-${provider}-${action}-${reconnect ? 'reconnect' : 'live'}`;
  const destination = scenarioProviderDestination(provider, scenarioId);
  await connectProvider(page, provider, fixtures.rollerId, destination);
  const headers = await authenticatedBrowserHeaders(page, baseUrl);
  const previousThreads = await currentThreadIds(page, baseUrl, fixtures.rollerId, headers);
  const prompt = `Roll a d23 for ${scenarioId}. Do not simulate the roll.`;

  await simulator.injectInbound(providerMessage(provider, destination, prompt, scenarioId));
  const threadId = await waitForNewThread(page, baseUrl, fixtures.rollerId, headers, previousThreads);
  const streamConnection = page.waitForRequest((request) =>
    new URL(request.url()).pathname === `/api/narad/v1/sessions/${threadId}/stream`);
  await openExistingThread(page, baseUrl, fixtures.rollerId, threadId);
  await streamConnection;
  await expect(page.getByText(prompt, { exact: false }).last()).toBeVisible();
  await expect(page.locator('[data-approval-action="approve"]').first()).toBeVisible();
  const requestIdBefore = await page.locator('[data-testid="hitl-approval"]').first().getAttribute('data-approval-request-id');
  expect(requestIdBefore, `${provider} must expose an immutable approval request id before resolution`).toBeTruthy();

  if (reconnect) {
    await reconnectPausedApproval(page, baseUrl, fixtures.rollerId, simulator, provider, threadId, requestIdBefore!);
  }

  // Capture native action tokens after any reconnect so inject uses the live outbound card.
  const providerAction = await waitForProviderAction(simulator, provider, action);
  const conflictingAction = action === 'deny' ? 'approve' : 'deny';
  const conflictingProviderAction = await waitForProviderAction(simulator, provider, conflictingAction);

  await injectProviderApproval(simulator, provider, providerAction, destination);
  const resolved = await waitForResolvedPublicApproval(page, baseUrl, threadId, action, providerSource(provider), headers);
  // Resolution surface is asserted on the public projection; reopen the live thread so the
  // browser observes continuation without requiring a specific activity caption string.
  await openExistingThread(page, baseUrl, fixtures.rollerId, threadId);
  await assertTerminalBrowserState(page, action, 23);

  // A provider may redeliver callbacks. The canonical first decision wins; a
  // duplicate or conflicting late action must never reopen or change the run.
  await injectProviderApproval(simulator, provider, providerAction, destination);
  await injectProviderApproval(simulator, provider, conflictingProviderAction, destination);
  const afterLateActions = await waitForResolvedPublicApproval(page, baseUrl, threadId, action, providerSource(provider), headers);
  expect(afterLateActions.id).toBe(resolved.id);
  expect(afterLateActions.decision).toBe(expectedDecision(action));
  await assertTerminalBrowserState(page, action, 23);

  await page.reload();
  await openExistingThread(page, baseUrl, fixtures.rollerId, threadId);
  if (expectedDecision(action) === 'approved') {
    await assertDiceMentionVisible(page, 23);
  } else {
    await expect(page.getByText(prompt, { exact: false }).last()).toBeVisible();
  }
  await expect(page.locator('[data-approval-action]').first()).toBeHidden();
  await expect(page.locator('textarea').last()).toBeEnabled();
}

async function reconnectPausedApproval(
  page: Page,
  baseUrl: URL,
  agentId: string,
  simulator: ProviderSimulatorControl,
  provider: Provider,
  threadId: string,
  requestId: string,
): Promise<void> {
  const before = await providerConnectionCount(simulator, provider);
  await simulator.scheduleFault({ provider, kind: 'disconnect', at: 'after-ingress' });
  await expect.poll(
    () => providerConnectionCount(simulator, provider),
    {
      timeout: 120_000,
      message: `${provider} transport must reconnect after a controlled disconnect`,
    }).toBeGreaterThan(before);

  await openExistingThread(page, baseUrl, agentId, threadId);
  const restored = page.locator(`[data-testid="hitl-approval"][data-approval-request-id="${requestId}"]`);
  await expect(restored, `${provider} reconnect must restore the same approval request id`).toBeVisible({ timeout: 60_000 });
  await expect(restored.locator('[data-approval-action="approve"]').first()).toBeVisible();
}
