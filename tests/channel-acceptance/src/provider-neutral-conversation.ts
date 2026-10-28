import { expect, type Page } from '@playwright/test';
import { authenticatedBrowserHeaders } from './browser-auth.js';
import { connectProvider, currentThreadIds, openExistingThread, waitForNewThread } from './flows.js';
import type { AcceptanceEvidence, Provider, ProviderSimulatorControl, SimulatorTranscriptEntry } from './model.js';
import { providerMessage } from './provider-events.js';
import { providerDestination } from './provider-fixtures.js';

export async function exerciseProviderNeutralConversation(page: Page, baseUrl: URL, simulator: ProviderSimulatorControl, agentId: string, evidence: AcceptanceEvidence): Promise<void> {
  await page.goto(new URL('/settings', baseUrl).toString());
  for (const provider of ['telegram', 'slack'] as const) {
    const destination = providerDestination(provider);
    await connectProvider(page, provider, agentId, destination);
    const headers = await authenticatedBrowserHeaders(page, baseUrl);
    const previous = await currentThreadIds(page, baseUrl, agentId, headers);
    const prompt = `CRH-032 ${provider} Unicode: 你好 👋`;
    await simulator.injectInbound(providerMessage(provider, destination, prompt, `CRH-032-${provider}`));
    const threadId = await waitForNewThread(page, baseUrl, agentId, headers, previous);
    await openExistingThread(page, baseUrl, agentId, threadId);
    await expect(page.getByText(prompt, { exact: false }).last()).toBeVisible();
    await assertProviderResponse(simulator, provider, destination);
  }
  evidence.addSkipped('CRH-032-thread-payload', 'Safe simulator evidence hashes provider bodies, so thread fields cannot be asserted without retaining content.');
  evidence.addSkipped('CRH-032-inbound-reaction-edit', 'Inbound reactions and message-edit ingress remain phase 2 ChannelHost widening.');
}

async function assertProviderResponse(simulator: ProviderSimulatorControl, provider: Provider, destination: string): Promise<void> {
  await expect.poll(async () => (await simulator.exportEvidence()).transcript.filter((entry) => isResponse(entry, provider, destination)), `${provider} must emit a provider-native response`).not.toEqual([]);
  const transcript = (await simulator.exportEvidence()).transcript;
  const response = transcript.find((entry) => isResponse(entry, provider, destination));
  expect(response?.transportMessageId, `${provider} response requires a provider reply identity`).toBeTruthy();
  if (provider === 'slack') return; // Slack reports typing_not_supported without a provider transport call.
  await expect.poll(async () => (await simulator.exportEvidence()).transcript.some((entry) => isTyping(entry, provider, destination)), `${provider} must publish typing progress`).toBeTruthy();
}

function isResponse(entry: SimulatorTranscriptEntry, provider: Provider, destination: string): boolean {
  return entry.provider === provider && entry.direction === 'outbound' && entry.transportMessageId !== null && entry.transportMessageId !== undefined
    && (provider === 'telegram' ? entry.operation === 'sendMessage' : provider === 'slack' ? entry.operation === 'chat.postMessage' : entry.operation === `channels/${destination}/messages`);
}

function isTyping(entry: SimulatorTranscriptEntry, provider: Exclude<Provider, 'slack'>, destination: string): boolean {
  return entry.provider === provider && entry.direction === 'outbound' && (provider === 'telegram' ? entry.operation === 'sendChatAction' : entry.operation === `channels/${destination}/typing`);
}
