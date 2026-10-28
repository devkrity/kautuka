import { expect, test } from '@playwright/test';
import { cleanupResilienceRun } from '../src/resilience-cleanup.js';

test('resilience cleanup always stops owned processes even when earlier cleanup fails', async () => {
  const calls: string[] = [];
  let recordedFailures: readonly string[] = [];
  const failures = await cleanupResilienceRun({
    closeBrowser: async () => { calls.push('browser'); throw new Error('browser unavailable'); },
    stopStack: async () => { calls.push('stack'); },
    removeState: async () => { calls.push('state'); },
    writeEvidence: async (cleanupFailures) => { calls.push('evidence'); recordedFailures = cleanupFailures; },
  });

  expect(calls).toEqual(['browser', 'stack', 'state', 'evidence']);
  expect(failures).toEqual(['browser cleanup failed: browser unavailable']);
  expect(recordedFailures).toEqual(failures);
});
