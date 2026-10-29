import { chromium, expect, test, type Browser, type Page } from '@playwright/test';
import { configurationDigest } from '../src/evidence.js';
import { openAgent, sendPrompt } from '../src/flows.js';
import { OwnedStackLifecycle } from '../src/lifecycle.js';
import { cleanupResilienceRun } from '../src/resilience-cleanup.js';
import { createResilienceExecution } from '../src/resilience-executor.js';
import { authenticateResilienceUser } from '../src/resilience-fixture.js';
import { bootstrapPublicFixtures } from '../src/public-fixture-bootstrap.js';
import { removeOwnedState, writeRestartEvidence } from '../src/restart-evidence.js';

test('CRH-037-DEPENDENCY-OUTAGE-RECOVERY Manager outage blocks runs and recovers after resume', async () => {
  const execution = createResilienceExecution();
  const lifecycle = new OwnedStackLifecycle(execution.stack);
  const startedAtUtc = new Date().toISOString();
  let browser: Browser | undefined;
  let page: Page | undefined;
  let status: 'passed' | 'failed' = 'failed';
  let failure: string | undefined;
  let primaryFailure: unknown;
  let cleanupFailures: readonly string[] = [];

  try {
    await lifecycle.start();
    browser = await chromium.launch();
    page = await browser.newPage();
    const headers = await authenticateResilienceUser(page, execution.stack.baseUrl, execution.credentials);
    const fixtures = await bootstrapPublicFixtures(page, execution.stack.baseUrl, headers);
    await assertReadiness(page, execution.stack.baseUrl, headers, true);

    const paused = await lifecycle.pauseOwnedProcess('Manager');
    await assertReadiness(page, execution.stack.baseUrl, headers, false);

    await openAgent(page, execution.stack.baseUrl, fixtures.assistantId);
    await sendPrompt(page, `Reply READY after dependency recovery ${execution.seed}.`);
    await expect(page.getByText(/dependenc|unavailable|Manager|not ready/i).first()).toBeVisible({ timeout: 30_000 });

    await lifecycle.resumeOwnedProcess(paused.spec, paused.generation + 1);
    await assertReadiness(page, execution.stack.baseUrl, headers, true);
    await sendPrompt(page, `Reply with exactly RECOVERED for ${execution.seed}.`);
    await expect(page.getByText(/RECOVERED/i).last()).toBeVisible({ timeout: 90_000 });
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
        schemaVersion: 1,
        scenarioId: 'CRH-037-DEPENDENCY-OUTAGE-RECOVERY',
        seed: execution.seed,
        status: primaryFailure || cleanup.length > 0 ? 'failed' : status,
        startedAtUtc,
        completedAtUtc: new Date().toISOString(),
        configurationDigest: configurationDigest(execution.configurationForDigest),
        processes: lifecycle.processRecords(),
        failure,
        cleanupFailures: cleanup,
        replayCommand: `KAUTUKA_ACCEPTANCE_SEED=${execution.seed} pnpm --dir src/tests/channel-acceptance exec playwright test tests/resilience-dependency.spec.ts --grep CRH-037-DEPENDENCY-OUTAGE-RECOVERY`,
        }, 'CRH-037-DEPENDENCY-OUTAGE-RECOVERY'),
    });
  }
  if (primaryFailure) throw primaryFailure;
  if (cleanupFailures.length > 0) throw new Error(`Resilience cleanup failed:\n${cleanupFailures.join('\n')}`);
});

async function assertReadiness(
  page: Page,
  baseUrl: URL,
  headers: Record<string, string>,
  expectReady: boolean,
): Promise<void> {
  await expect.poll(async () => {
    const response = await page.context().request.get(new URL('/api/runtime/execution-readiness', baseUrl).toString(), { headers });
    if (!response.ok()) return false;
    const payload = await response.json() as { dependencies?: Array<{ dependency?: string; ready?: boolean }> };
    const manager = (payload.dependencies ?? []).find((item) => /manager/i.test(item.dependency ?? ''));
    return manager?.ready === true;
  }, `Manager readiness must become ${expectReady}`).toBe(expectReady);
}
