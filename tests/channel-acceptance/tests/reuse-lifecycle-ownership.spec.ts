import { expect, test } from '@playwright/test';
import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';

test('reused acceptance stack has one owner and cannot remove the outer workspace', async () => {
  const source = await readFile(resolve(process.cwd(), 'src', 'harness.ts'), 'utf8');
  expect(source).toContain('reuseExistingStack ? undefined : new OwnedStackLifecycle');
  expect(source).toContain("lifecycle ? await attemptCleanup('owned stack'");
  expect(source).not.toContain('await lifecycle.start();');
});

test('normal scenarios create one fresh owned lifecycle and write evidence after cleanup', async () => {
  const normal = await readFile(resolve(process.cwd(), 'tests', 'cross-surface.spec.ts'), 'utf8');
  const harness = await readFile(resolve(process.cwd(), 'src', 'harness.ts'), 'utf8');
  expect(normal).not.toContain('OwnedStackLifecycle');
  expect(normal).not.toContain('reuseExistingStack: true');
  expect(normal).toContain("test.describe.configure({ mode: 'serial' });");
  expect(normal).toContain('await executeScenario(browser, scenario, execution);');
  expect(normal).toContain("expect(execution.stack.reuseExistingStack");
  expect(harness.indexOf("await attemptCleanup('owned stack'"))
    .toBeLessThan(harness.indexOf('evidence.write('));
});
