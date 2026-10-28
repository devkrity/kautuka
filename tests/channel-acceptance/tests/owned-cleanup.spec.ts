import { expect, test } from '@playwright/test';
import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';

test('owned lifecycle waits for its Windows process tree before deleting isolated WAL state', async () => {
  const source = await readFile(resolve(process.cwd(), 'src', 'owned-child-processes.ts'), 'utf8');
  expect(source).toContain('const exited = waitForChildExit(child);');
  expect(source).toContain('await exited;');
  expect(source).toContain('taskkill failed for owned process');
});
