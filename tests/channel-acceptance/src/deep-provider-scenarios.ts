import { expect } from '@playwright/test';
import { authenticatedBrowserHeaders } from './browser-auth.js';
import { slackSlash, telegramFileMessage } from './deep-provider-injects.js';
import { connectProvider, currentThreadIds, openExistingThread, waitForNewThread } from './flows.js';
import type { AcceptanceScenario } from './model.js';
import { providerMessage } from './provider-events.js';
import { providerDestination } from './provider-fixtures.js';

export const deepProviderScenarios: readonly AcceptanceScenario[] = [
  {
    id: 'CRH-032-COMMAND-INGRESS',
    title: 'Slack native commands reach Gateway command handling',
    tags: ['full', 'release-gate'],
    providers: ['slack'],
    checklistIds: ['P-10', 'L-06'],
    async execute(context) {
      const { page, baseUrl, simulator, fixtures } = context;
      await page.goto(new URL('/settings', baseUrl).toString());
      await connectProvider(page, 'slack', fixtures.assistantId);
      const headers = await authenticatedBrowserHeaders(page, baseUrl);
      const previous = await currentThreadIds(page, baseUrl, fixtures.assistantId, headers);
      await simulator.injectInbound(slackSlash(providerDestination('slack'), '/help', `cmd-slack-${context.seed}`));
      await expect.poll(async () => {
        const evidence = await simulator.exportEvidence();
        return evidence.transcript.some((entry) => entry.provider === 'slack' && entry.direction === 'outbound');
      }, 'Slack command must produce outbound evidence').toBeTruthy();
      const threadId = await waitForNewThread(page, baseUrl, fixtures.assistantId, headers, previous).catch(() => null);
      if (threadId) await openExistingThread(page, baseUrl, fixtures.assistantId, threadId);
    },
  },
  {
    id: 'CRH-032-FILE-ON-MESSAGE',
    title: 'Telegram file-on-message ingress is admitted on the message path',
    tags: ['full', 'release-gate'],
    providers: ['telegram'],
    checklistIds: ['L-08', 'P-10'],
    async execute(context) {
      const { page, baseUrl, simulator, fixtures, seed } = context;
      await page.goto(new URL('/settings', baseUrl).toString());
      const destination = providerDestination('telegram');
      await connectProvider(page, 'telegram', fixtures.assistantId, destination);
      const headers = await authenticatedBrowserHeaders(page, baseUrl);
      const previous = await currentThreadIds(page, baseUrl, fixtures.assistantId, headers);
      const caption = `CRH-032 file caption ${seed}`;
      await simulator.injectInbound(telegramFileMessage(destination, caption, seed));
      const threadId = await waitForNewThread(page, baseUrl, fixtures.assistantId, headers, previous);
      await openExistingThread(page, baseUrl, fixtures.assistantId, threadId);
      await expect(page.getByText(caption, { exact: false }).last()).toBeVisible({ timeout: 90_000 });
    },
  },
  {
    id: 'CRH-032-STREAM-EDIT-EVIDENCE',
    title: 'Long replies record outbound edit operations by name in hashed transcripts',
    tags: ['full', 'release-gate'],
    providers: ['telegram'],
    checklistIds: ['L-08', 'P-10'],
    async execute(context) {
      const { page, baseUrl, simulator, fixtures, seed } = context;
      await page.goto(new URL('/settings', baseUrl).toString());
      const destination = providerDestination('telegram');
      await connectProvider(page, 'telegram', fixtures.assistantId, destination);
      const headers = await authenticatedBrowserHeaders(page, baseUrl);
      const previous = await currentThreadIds(page, baseUrl, fixtures.assistantId, headers);
      const prompt = `CRH-032 stream-edit ${seed}: reply with a long multi-sentence answer about channel runtime hardening.`;
      await simulator.injectInbound(providerMessage('telegram', destination, prompt, `stream-${seed}`));
      await waitForNewThread(page, baseUrl, fixtures.assistantId, headers, previous);
      await expect.poll(async () => {
        const transcript = (await simulator.exportEvidence()).transcript;
        return transcript.some((entry) =>
          entry.provider === 'telegram'
          && entry.direction === 'outbound'
          && (entry.operation === 'editMessageText'
            || entry.operation === 'sendMessage'
            || entry.operation === 'sendChatAction'));
      }, { timeout: 90_000, message: 'telegram must record send, edit, or typing outbound operations' }).toBeTruthy();
    },
  },
];
