import { chromium, expect, test, type Browser, type Page } from '@playwright/test';
import { configurationDigest } from '../src/evidence.js';
import { connectProvider, openAgent } from '../src/flows.js';
import { OwnedStackLifecycle } from '../src/lifecycle.js';
import { createResilienceExecution } from '../src/resilience-executor.js';
import { cleanupResilienceRun } from '../src/resilience-cleanup.js';
import { authenticateResilienceUser, channelCard, ensureResilienceAgent, openProfile } from '../src/resilience-fixture.js';
import { measureRestart, removeOwnedState, type RestartObservation, writeRestartEvidence } from '../src/restart-evidence.js';

test('CRH-050-RESTART-MATRIX direct owned children converge from committed state', async () => {
  const execution = createResilienceExecution();
  const lifecycle = new OwnedStackLifecycle(execution.stack);
  const startedAtUtc = new Date().toISOString();
  const observations: RestartObservation[] = [];
  let browser: Browser | undefined;
  let page: Page | undefined;
  let status: 'passed' | 'failed' = 'failed';
  let failure: string | undefined;
  let primaryFailure: unknown;
  let cleanupFailures: readonly string[] = [];
  let threadId: string | undefined;

  try {
    await lifecycle.start();
    browser = await chromium.launch();
    page = await browser.newPage();
    const headers = await authenticateResilienceUser(page, execution.stack.baseUrl, execution.credentials);
    const agentId = await ensureResilienceAgent(page, execution.stack.baseUrl, headers);
    await openAgent(page, execution.stack.baseUrl, agentId);
    threadId = new URL(page.url()).searchParams.get('sessionId') ?? undefined;
    expect(threadId, 'Gateway restart requires a committed canonical thread boundary').toBeTruthy();

    observations.push(await measureRestart(lifecycle, 'Gateway', async () => {
      await page!.reload();
      await expect(page!.locator('textarea').last()).toBeEnabled();
      expect(new URL(page!.url()).searchParams.get('sessionId')).toBe(threadId);
    }));

    await page.goto(new URL('/settings', execution.stack.baseUrl).toString());
    await connectProvider(page, 'telegram', agentId);
    observations.push(await measureRestart(lifecycle, 'ChannelHost', async () => {
      await page!.reload();
      await openProfile(page!);
      await expect(channelCard(page!, 'Telegram').getByText('Status: active')).toBeVisible();
    }));

    await execution.simulator.reset();
    await execution.simulator.injectInbound(telegramInbound());
    const pendingBefore = (await execution.simulator.exportEvidence()).pendingTelegramUpdates ?? 0;
    expect(pendingBefore, 'Injected Telegram update must reach durable simulator state').toBeGreaterThan(0);
    observations.push(await measureRestart(lifecycle, 'ProviderSimulator', async () => {
      const pendingAfter = (await execution.simulator.exportEvidence()).pendingTelegramUpdates ?? 0;
      expect(pendingAfter, 'Pending Telegram update must survive simulator restart').toBeGreaterThanOrEqual(pendingBefore);
    }));

    const browserStarted = Date.now();
    await browser.close();
    browser = await chromium.launch();
    page = await browser.newPage();
    await authenticateResilienceUser(page, execution.stack.baseUrl, execution.credentials);
    await page.goto(new URL(`/workspace/${encodeURIComponent(agentId)}?sessionId=${encodeURIComponent(threadId!)}`, execution.stack.baseUrl).toString());
    await expect(page.locator('textarea').last()).toBeEnabled();
    expect(new URL(page.url()).searchParams.get('sessionId')).toBe(threadId);
    observations.push({ boundary: 'browser', durationMs: Date.now() - browserStarted, beforeGeneration: 1, afterGeneration: 2, assertion: 'fresh browser process reattached to committed thread' });
    status = 'passed';
  } catch (error) {
    primaryFailure = error;
    failure = error instanceof Error ? error.message : String(error);
  } finally {
    cleanupFailures = await cleanupResilienceRun({
      closeBrowser: async () => { await browser?.close(); },
      stopStack: () => lifecycle.stop(),
      removeState: () => removeOwnedState(execution.stateRoot),
      writeEvidence: (cleanup) => writeRestartEvidence(execution.evidenceDirectory, {
        schemaVersion: 1, scenarioId: 'CRH-050-RESTART-MATRIX', seed: execution.seed,
        status: primaryFailure || cleanup.length > 0 ? 'failed' : status, startedAtUtc,
        completedAtUtc: new Date().toISOString(), configurationDigest: configurationDigest(execution.configurationForDigest),
        threadId, observations, processes: lifecycle.processRecords(), failure, cleanupFailures: cleanup,
        replayCommand: `KAUTUKA_ACCEPTANCE_SEED=${execution.seed} pnpm --dir tests/channel-acceptance exec playwright test tests/resilience-owned.spec.ts --grep CRH-050-RESTART-MATRIX`,
      }),
    });
  }
  if (primaryFailure) throw primaryFailure;
  if (cleanupFailures.length > 0) throw new Error(`Resilience cleanup failed:\n${cleanupFailures.join('\n')}`);
});

function telegramInbound() {
  return { provider: 'telegram' as const, kind: 'message' as const, actorReference: 'resilience-user', conversationReference: '100001', payload: {
    message: { message_id: 5001, date: 0, chat: { id: '100001', type: 'private' }, from: { id: 'resilience-user', is_bot: false, first_name: 'Resilience' }, text: 'restart boundary' },
  } };
}
