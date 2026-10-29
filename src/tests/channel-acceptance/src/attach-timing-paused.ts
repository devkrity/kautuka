import { expect, type Page } from '@playwright/test';
import { assertDiceMentionVisible } from './dice-fixture.js';
import { openExistingThread } from './flows.js';
import type { Provider, ProviderSimulatorControl } from './model.js';
import { waitForResolvedPublicApproval } from './hitl-parity.js';
import { createPausedProviderApproval } from './attach-timing-shared.js';

export async function executeAttachWhilePaused(
  page: Page,
  baseUrl: URL,
  simulator: ProviderSimulatorControl,
  fixtures: import('./public-fixture-bootstrap.js').AcceptanceFixtures,
  provider: Provider,
  scenarioSeed: string,
): Promise<void> {
  const { threadId, requestId, headers } = await createPausedProviderApproval(
    page, baseUrl, simulator, fixtures, provider, `attach-paused-${scenarioSeed}-${provider}`);
  const streamConnection = page.waitForRequest((request) =>
    new URL(request.url()).pathname === `/api/narad/v1/sessions/${threadId}/stream`);
  await openExistingThread(page, baseUrl, fixtures.rollerId, threadId);
  await streamConnection;
  const panel = page.locator(`[data-testid="hitl-approval"][data-approval-request-id="${requestId}"]`);
  await expect(panel, 'attach while paused must reconstruct the pending approval').toBeVisible();
  await panel.locator('[data-approval-action="approve"]').click();
  await waitForResolvedPublicApproval(page, baseUrl, threadId, 'approve', 'chat', headers);
  await assertDiceMentionVisible(page, 23);
  await expect(page.locator('[data-approval-action]').first()).toBeHidden();
}
