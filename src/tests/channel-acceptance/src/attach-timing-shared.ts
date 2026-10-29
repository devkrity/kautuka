import { expect, type Page } from '@playwright/test';
import { authenticatedBrowserHeaders } from './browser-auth.js';
import {
  connectProvider,
  currentThreadIds,
  waitForNewThread,
} from './flows.js';
import type { Provider, ProviderSimulatorControl } from './model.js';
import { providerMessage } from './provider-events.js';
import { scenarioProviderDestination } from './provider-fixtures.js';
import { publicApprovalProjection } from './hitl-parity.js';

export async function createPausedProviderApproval(
  page: Page,
  baseUrl: URL,
  simulator: ProviderSimulatorControl,
  fixtures: import('./public-fixture-bootstrap.js').AcceptanceFixtures,
  provider: Provider,
  scenarioId: string,
): Promise<Readonly<{ threadId: string; requestId: string; destination: string; prompt: string; headers: Record<string, string> }>> {
  const destination = scenarioProviderDestination(provider, scenarioId);
  await connectProvider(page, provider, fixtures.rollerId, destination);
  const headers = await authenticatedBrowserHeaders(page, baseUrl);
  const previousThreads = await currentThreadIds(page, baseUrl, fixtures.rollerId, headers);
  const prompt = `Roll a d23 for ${scenarioId}. Do not simulate the roll.`;
  await simulator.injectInbound(providerMessage(provider, destination, prompt, scenarioId));
  const threadId = await waitForNewThread(page, baseUrl, fixtures.rollerId, headers, previousThreads);
  const requestId = await waitForPendingApprovalId(page, baseUrl, threadId, headers);
  return { threadId, requestId, destination, prompt, headers };
}

export async function waitForPendingApprovalId(
  page: Page,
  baseUrl: URL,
  threadId: string,
  headers: Record<string, string>,
): Promise<string> {
  let requestId: string | undefined;
  await expect.poll(async () => {
    const pending = (await publicApprovalProjection(page, baseUrl, threadId, headers)).find((item) => !item.decision);
    requestId = pending?.id;
    return requestId;
  }, {
    timeout: 180_000,
    message: 'provider ingress must persist a pending approval before browser attach',
  }).toBeTruthy();
  return requestId!;
}
