import { attemptCleanup } from './cleanup.js';

export interface ResilienceCleanupSteps {
  readonly closeBrowser: () => Promise<void>;
  readonly stopStack: () => Promise<void>;
  readonly removeState: () => Promise<void>;
  readonly writeEvidence: (cleanupFailures: readonly string[]) => Promise<void>;
}

/** Runs every cleanup boundary even when an earlier boundary fails. */
export async function cleanupResilienceRun(steps: ResilienceCleanupSteps): Promise<readonly string[]> {
  const operationalFailures = [
    await attemptCleanup('browser', steps.closeBrowser),
    await attemptCleanup('owned stack', steps.stopStack),
    await attemptCleanup('isolated state', steps.removeState),
  ].filter((failure): failure is string => failure !== undefined);
  const evidenceFailure = await attemptCleanup('evidence', () => steps.writeEvidence(operationalFailures));
  return evidenceFailure ? [...operationalFailures, evidenceFailure] : operationalFailures;
}
