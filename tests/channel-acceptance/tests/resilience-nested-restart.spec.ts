import { chromium, expect, test, type Browser, type Page } from '@playwright/test';
import { assertDiceMentionVisible } from '../src/dice-fixture.js';
import { configurationDigest } from '../src/evidence.js';
import { resolveBrowserApproval } from '../src/flows.js';
import { OwnedStackLifecycle } from '../src/lifecycle.js';
import { cleanupResilienceRun } from '../src/resilience-cleanup.js';
import { createResilienceExecution } from '../src/resilience-executor.js';
import { authenticateResilienceUser } from '../src/resilience-fixture.js';
import { runNestedChildPauseRestart } from '../src/resilience-nested-restart.js';
import { removeOwnedState, writeRestartEvidence } from '../src/restart-evidence.js';
import { bootstrapPublicFixtures } from '../src/public-fixture-bootstrap.js';
import { bootstrapSkillFixtures } from '../src/skill-fixture-bootstrap.js';

for (const boundary of ['Gateway', 'ChannelHost'] as const) {
  const scenarioId = boundary === 'Gateway'
    ? 'CRH-035-NESTED-GATEWAY-RESTART-AT-CHILD-PAUSE'
    : 'CRH-035-NESTED-CHANNELHOST-RESTART-AT-CHILD-PAUSE';

  test(`${scenarioId} restores the same child approval after ${boundary} restart`, async () => {
    const execution = createResilienceExecution();
    const lifecycle = new OwnedStackLifecycle(execution.stack);
    const startedAtUtc = new Date().toISOString();
    let browser: Browser | undefined;
    let page: Page | undefined;
    let status: 'passed' | 'failed' = 'failed';
    let failure: string | undefined;
    let primaryFailure: unknown;
    let cleanupFailures: readonly string[] = [];
    let requestId: string | undefined;

    try {
      await lifecycle.start();
      browser = await chromium.launch();
      page = await browser.newPage();
      const headers = await authenticateResilienceUser(page, execution.stack.baseUrl, execution.credentials);
      const fixtures = await bootstrapPublicFixtures(page, execution.stack.baseUrl, headers);
      const skills = await bootstrapSkillFixtures(page, execution.stack.baseUrl, fixtures, headers);
      const result = await runNestedChildPauseRestart(
        page, execution.stack.baseUrl, execution.simulator, skills, execution.seed, lifecycle, boundary);
      requestId = result.requestId;
      await resolveBrowserApproval(page, 'approve');
      await assertDiceMentionVisible(page, 27);
      await expect(page.locator(`[data-testid="hitl-approval"][data-approval-request-id="${requestId}"]`)).toHaveCount(0);
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
          schemaVersion: 1, scenarioId, seed: execution.seed,
          status: primaryFailure || cleanup.length > 0 ? 'failed' : status,
          startedAtUtc, completedAtUtc: new Date().toISOString(),
          configurationDigest: configurationDigest(execution.configurationForDigest),
          requestId, boundary, processes: lifecycle.processRecords(), failure, cleanupFailures: cleanup,
          replayCommand: `KAUTUKA_ACCEPTANCE_SEED=${execution.seed} pnpm --dir tests/channel-acceptance exec playwright test tests/resilience-nested-restart.spec.ts --grep ${scenarioId}`,
        }, scenarioId),
      });
    }
    if (primaryFailure) throw primaryFailure;
    if (cleanupFailures.length > 0) throw new Error(`Resilience cleanup failed:\n${cleanupFailures.join('\n')}`);
  });
}
