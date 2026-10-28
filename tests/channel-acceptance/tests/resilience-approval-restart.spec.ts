import { chromium, expect, test, type Browser, type Page } from '@playwright/test';
import { assertDiceMentionVisible } from '../src/dice-fixture.js';
import { configurationDigest } from '../src/evidence.js';
import { resolveBrowserApproval } from '../src/flows.js';
import { OwnedStackLifecycle } from '../src/lifecycle.js';
import { runApprovalPendingRestart, type ApprovalPendingRestartBoundary } from '../src/resilience-approval-restart.js';
import { cleanupResilienceRun } from '../src/resilience-cleanup.js';
import { createResilienceExecution } from '../src/resilience-executor.js';
import { removeOwnedState, writeRestartEvidence } from '../src/restart-evidence.js';

const cases: ReadonlyArray<readonly [ApprovalPendingRestartBoundary, string]> = [
  ['Gateway', 'CRH-050-APPROVAL-PENDING-RESTART'],
  ['ChannelHost', 'CRH-050-CHANNELHOST-APPROVAL-PENDING-RESTART'],
  ['ProviderSimulator', 'CRH-050-SIMULATOR-APPROVAL-PENDING-RESTART'],
];

for (const [boundary, scenarioId] of cases) {
  test(`${scenarioId} restores the same pending skill approval after ${boundary} restart`, async () => {
    const execution = createResilienceExecution();
    const lifecycle = new OwnedStackLifecycle(execution.stack);
    const startedAtUtc = new Date().toISOString();
    let browser: Browser | undefined;
    let page: Page | undefined;
    let status: 'passed' | 'failed' = 'failed';
    let failure: string | undefined;
    let primaryFailure: unknown;
    let cleanupFailures: readonly string[] = [];
    let threadId: string | undefined;
    let requestId: string | undefined;

    try {
      await lifecycle.start();
      browser = await chromium.launch();
      page = await browser.newPage();
      const result = await runApprovalPendingRestart(execution, lifecycle, page, boundary);
      threadId = result.threadId;
      requestId = result.requestId;
      await resolveBrowserApproval(page, 'approve');
      await assertDiceMentionVisible(page, 23);
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
          threadId, requestId, boundary, processes: lifecycle.processRecords(), failure, cleanupFailures: cleanup,
          replayCommand: `KAUTUKA_ACCEPTANCE_SEED=${execution.seed} pnpm --dir tests/channel-acceptance exec playwright test tests/resilience-approval-restart.spec.ts --grep ${scenarioId}`,
        }, scenarioId),
      });
    }
    if (primaryFailure) throw primaryFailure;
    if (cleanupFailures.length > 0) throw new Error(`Resilience cleanup failed:\n${cleanupFailures.join('\n')}`);
  });
}
