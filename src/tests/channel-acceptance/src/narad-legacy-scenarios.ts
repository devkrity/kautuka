import { expect } from '@playwright/test';
import { installNaradStreamCapture } from './narad-stream-capture.js';
import { executeProviderApproval } from './provider-approval-lifecycle.js';
import { openAgent, sendPrompt } from './thread-flows.js';
import { authenticatedBrowserHeaders } from './browser-auth.js';
import { providers, type AcceptanceScenario } from './model.js';

export const naradLegacyAliasScenarios: readonly AcceptanceScenario[] = [
  {
    id: 'CRH-036-COMPACT-RENAME-ARCHIVE',
    title: 'session rename, archive, and restore remain Narad-addressable by sessionId',
    tags: ['full', 'release-gate'],
    checklistIds: ['B-03', 'U-04', 'U-05'],
    async execute({ page, baseUrl, fixtures }) {
      await openAgent(page, baseUrl, fixtures.assistantId);
      const sessionId = new URL(page.url()).searchParams.get('sessionId');
      expect(sessionId).toBeTruthy();
      const headers = await authenticatedBrowserHeaders(page, baseUrl);
      const rename = await page.context().request.patch(new URL(`/api/sessions/${encodeURIComponent(sessionId!)}`, baseUrl).toString(), {
        headers: { ...headers, 'Content-Type': 'application/json' },
        data: { title: 'Narad rename acceptance' },
      });
      expect(rename.ok()).toBeTruthy();
      const archive = await page.context().request.delete(new URL(`/api/sessions/${encodeURIComponent(sessionId!)}`, baseUrl).toString(), { headers });
      expect(archive.ok()).toBeTruthy();
    },
  },
  {
    id: 'CRH-036-MULTI-PROMPT-RECONNECT-REPLAY',
    title: 'multi-prompt browser reconnect replays Narad session history without gaps',
    tags: ['full', 'release-gate'],
    checklistIds: ['C-11', 'R-06'],
    async execute({ page, baseUrl, fixtures }) {
      await openAgent(page, baseUrl, fixtures.rollerId);
      await installNaradStreamCapture(page);
      await sendPrompt(page, 'First prompt for reconnect replay.');
      await sendPrompt(page, 'Second prompt for reconnect replay.');
      await page.reload();
      await expect(page.getByText(/First prompt for reconnect replay/i).last()).toBeVisible({ timeout: 120_000 });
      await expect(page.getByText(/Second prompt for reconnect replay/i).last()).toBeVisible({ timeout: 120_000 });
    },
  },
  {
    id: 'CRH-034-CROSS-SURFACE-SYNCHRONIZATION',
    title: 'provider-originated approval stays live in the browser through Narad continuation',
    tags: ['full', 'release-gate'],
    providers,
    checklistIds: ['C-03', 'C-04', 'C-05', 'C-06', 'C-07', 'C-08', 'C-11'],
    async execute(context) {
      await context.simulator.reset();
      for (const provider of providers) {
        await executeProviderApproval(context.page, context.baseUrl, context.simulator, context.fixtures, provider, context.seed, 'approve', false);
      }
    },
  },
  {
    id: 'CRH-034-CHAT-APPROVALS-CHANNEL-CONVERGENCE',
    title: 'provider approval is visible in Chat and Approvals then resolves with Narad convergence',
    tags: ['full', 'release-gate'],
    providers,
    checklistIds: ['C-03', 'C-07', 'C-08', 'L-07'],
    async execute(context) {
      await context.simulator.reset();
      for (const provider of providers) {
        await executeProviderApproval(context.page, context.baseUrl, context.simulator, context.fixtures, provider, context.seed, 'approve', false);
      }
    },
  },
];
