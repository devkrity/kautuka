import { expect, test } from '@playwright/test';
import { readdir, readFile } from 'node:fs/promises';
import { join } from 'node:path';

async function sourceFiles(directory: string): Promise<readonly string[]> {
  const entries = await readdir(directory, { withFileTypes: true });
  return (await Promise.all(entries.map(async (entry) => entry.isDirectory()
    ? sourceFiles(join(directory, entry.name))
    : entry.name.endsWith('.ts') ? [join(directory, entry.name)] : []))).flat();
}

test('acceptance source remains small, declarative, and free of raw sleeps', async () => {
  const files = await sourceFiles(join(process.cwd(), 'src'));
  const tests = await sourceFiles(join(process.cwd(), 'tests'));
  const all = await Promise.all([...files, ...tests].map(async (file) => ({ file, text: await readFile(file, 'utf8') })));
  const prohibitedSleep = new RegExp(['waitFor' + 'Timeout', '\\bsle' + 'ep\\s*\\(', 'set' + 'Timeout\\s*\\('].join('|'));
  for (const item of all) {
    expect(item.text.split('\n').length, item.file).toBeLessThanOrEqual(116);
    if (item.file.includes(`${join('src')}${process.platform === 'win32' ? '\\' : '/'}`)) expect(item.text, item.file).not.toMatch(prohibitedSleep);
  }
  expect((await readFile(join(process.cwd(), 'src', 'model.ts'), 'utf8'))).toContain('interface AcceptanceScenario');
  expect((await readFile(join(process.cwd(), 'src', 'evidence.ts'), 'utf8'))).toContain('assertNoSecretShapedEvidence');
});

test('acceptance dependencies, formatting, selectors, and assertion ownership remain auditable', async () => {
  const files = await sourceFiles(join(process.cwd(), 'src'));
  const source = await Promise.all(files.map(async (file) => ({ file, text: await readFile(file, 'utf8') })));
  const packageJson = JSON.parse(await readFile(join(process.cwd(), 'package.json'), 'utf8')) as { dependencies?: Record<string, string>; devDependencies?: Record<string, string> };

  expect(packageJson.dependencies ?? {}).toEqual({});
  expect(Object.keys(packageJson.devDependencies ?? {}).sort()).toEqual(['@playwright/test', '@types/node', 'fflate', 'typescript']);
  for (const item of source) {
    expect(item.text.endsWith('\n'), item.file).toBeTruthy();
    expect(item.text, item.file).not.toMatch(/[ \t]+$/m);
    expect(item.text, item.file).not.toMatch(/locator\(['\"](?:div|section|main|article)\./);
    expect(item.text, item.file).not.toMatch(/(?:\.\.\/){3,}|D:\\Sources\\Kautuka\\src/);
  }
  const scenarioSource = await readFile(join(process.cwd(), 'src', 'model.ts'), 'utf8');
  expect(scenarioSource).toContain('readonly checklistIds: readonly string[];');
  expect(scenarioSource).toContain('readonly execute: (context: ScenarioContext) => Promise<void>;');
  const providerEvents = await readFile(join(process.cwd(), 'src', 'provider-events.ts'), 'utf8');
  expect(providerEvents.match(/export function hasProviderOperation/g)).toHaveLength(1);
});
