import { expect, type Page } from '@playwright/test';
import type { ApprovalAction, Provider, ProviderSimulatorControl } from './model.js';
import { nativeApprovalAction } from './approval-action-vocabulary.js';

export async function waitForProviderAction(simulator: ProviderSimulatorControl, provider: Provider, action: ApprovalAction): Promise<Readonly<{ token: string; transportMessageId: string }>> {
  const expectedAction = nativeApprovalAction(action);
  let found: Readonly<{ token: string; transportMessageId: string }> | undefined;
  await expect.poll(async () => {
    for (const entry of (await simulator.exportEvidence()).transcript) {
      if (entry.direction !== 'outbound' || entry.provider !== provider || !entry.transportMessageId) continue;
      const token = entry.actionTokens?.find((candidate) => candidate.endsWith(`:${expectedAction}`));
      if (token) found = { token, transportMessageId: entry.transportMessageId };
    }
    return found;
  }, {
    timeout: 180_000,
    message: `${provider} must publish the native ${action} action`,
  }).toBeTruthy();
  return found!;
}

export async function injectProviderApproval(simulator: ProviderSimulatorControl, provider: Provider, action: Readonly<{ token: string; transportMessageId: string }>, conversationReference: string): Promise<void> {
  const eventId = `acceptance-${Date.now()}`;
  const payload = provider === 'telegram' ? { callback_query: { id: eventId, from: { id: Number(conversationReference) }, message: { message_id: Number(action.transportMessageId), chat: { id: Number(conversationReference), type: 'private' } }, data: action.token } }
    : provider === 'slack' ? { _simulatorEnvelopeType: 'interactive', type: 'block_actions', user: { id: 'U1' }, channel: { id: conversationReference }, actions: [{ value: action.token }], container: { message_ts: action.transportMessageId } }
      : { _simulatorDispatchType: 'INTERACTION_CREATE', id: eventId, type: 3, channel_id: conversationReference, member: { user: { id: 'U1' } }, message: { id: action.transportMessageId }, data: { custom_id: action.token } };
  await simulator.injectInbound({ provider, kind: 'approval', actorReference: provider === 'telegram' ? conversationReference : 'U1', conversationReference, payload });
}

export async function resolveBrowserApproval(page: Page, action: string): Promise<void> {
  await page.locator(`[data-approval-action="${action}"]`).first().click();
  await expect(page.locator(`[data-approval-action="${action}"]`).first()).toBeHidden();
}
