import { expect, type Page } from '@playwright/test';
import { assertDiceMentionVisible } from './dice-fixture.js';
import {
  injectProviderApproval,
  openExistingThread,
  waitForProviderAction,
} from './flows.js';
import type { Provider, ProviderSimulatorControl } from './model.js';
import { waitForResolvedPublicApproval, providerSource } from './hitl-parity.js';
import { createPausedProviderApproval } from './attach-timing-shared.js';

export async function executeAttachAfterTerminal(
  page: Page,
  baseUrl: URL,
  simulator: ProviderSimulatorControl,
  fixtures: import('./public-fixture-bootstrap.js').AcceptanceFixtures,
  provider: Provider,
  scenarioSeed: string,
): Promise<void> {
  const scenarioId = `attach-terminal-${scenarioSeed}-${provider}`;
  const { threadId, requestId, destination, prompt, headers } = await createPausedProviderApproval(
    page, baseUrl, simulator, fixtures, provider, scenarioId);
  const providerAction = await waitForProviderAction(simulator, provider, 'approve');
  await injectProviderApproval(simulator, provider, providerAction, destination);
  await waitForResolvedPublicApproval(page, baseUrl, threadId, 'approve', providerSource(provider), headers);

  const streamConnection = page.waitForRequest((request) =>
    new URL(request.url()).pathname === `/api/narad/v1/sessions/${threadId}/stream`);
  await openExistingThread(page, baseUrl, fixtures.rollerId, threadId);
  await streamConnection;
  await expect(page.getByText(prompt, { exact: false }).last()).toBeVisible();
  await assertDiceMentionVisible(page, 23);
  await expect(page.locator(`[data-testid="hitl-approval"][data-approval-request-id="${requestId}"]`)).toHaveCount(0);
  await expect(page.locator('textarea').last()).toBeEnabled();
}
