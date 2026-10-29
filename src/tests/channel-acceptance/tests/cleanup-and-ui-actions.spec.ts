import { expect, test } from '@playwright/test';
import { attemptCleanup } from '../src/cleanup.js';
import { runBoundedUiAction } from '../src/ui-actions.js';

test('cleanup errors are captured for evidence instead of becoming the primary scenario failure', async () => {
  await expect(attemptCleanup('browser context', async () => { throw new Error('Target page, context or browser has been closed'); }))
    .resolves.toContain('browser context cleanup failed: Target page');
  await expect(attemptCleanup('owned stack', async () => {})).resolves.toBeUndefined();
});

test('bounded UI actions identify the exact interaction without force-clicking', async () => {
  await expect(runBoundedUiAction('composer submit', async (timeout) => {
    expect(timeout).toBe(15_000);
    throw new Error('element was detached');
  })).rejects.toThrow("UI action 'composer submit' failed within 15000ms: element was detached");
});
