import { expect, test } from '@playwright/test';
import { readdir, readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { assertSafeRetainedArtifacts } from '../src/artifact-safety.js';
import { projectConnectDiagnostic, recordConnectDiagnostic } from '../src/connect-diagnostics.js';
import { assertNoSecretShapedEvidence, configurationDigest, redact } from '../src/evidence.js';
import { replayCommand } from '../src/replay.js';

test('redacts secret fields and token-shaped values before acceptance evidence is persisted', () => {
  expect(redact({ token: 'xoxb-example-token', nested: { authorization: 'Bearer private' }, safe: 'scenario-42' })).toEqual({ token: '[REDACTED]', nested: { authorization: '[REDACTED]' }, safe: 'scenario-42' });
  expect(() => assertNoSecretShapedEvidence('Authorization: Bearer deliberately-not-safe')).toThrow(/secret-shaped/);
  expect(configurationDigest({ secret: 'not-written' })).toMatch(/^[a-f0-9]{64}$/);
});

test('acceptance code uses no raw sleep and writes only redacted evidence', async () => {
  const root = join(process.cwd(), 'src');
  const files = (await readdir(root)).filter((name) => name.endsWith('.ts'));
  const source = await Promise.all(files.map((name) => readFile(join(root, name), 'utf8')));

  expect(source.join('\n')).not.toMatch(/(?:waitForTimeout|\bsleep\s*\(|setTimeout\s*\()/);
  const writer = await readFile(join(process.cwd(), 'src', 'evidence.ts'), 'utf8');
  expect(writer).toContain('const evidence = redact(');
  expect(writer).toContain('assertNoSecretShapedEvidence(serialized)');
});

test('acceptance support stays bounded and centralizes evidence redaction', async () => {
  const sourceRoot = join(process.cwd(), 'src');
  const files = (await readdir(sourceRoot)).filter((name) => name.endsWith('.ts'));
  const source = await Promise.all(files.map(async (name) => ({ name, text: await readFile(join(sourceRoot, name), 'utf8') })));

  for (const file of source) expect(file.text.split('\n').length, file.name).toBeLessThanOrEqual(116);
  expect(source.map((file) => file.text.match(/export function redact\(/g)?.length ?? 0).reduce((total, count) => total + count, 0)).toBe(1);
  expect(source.map((file) => file.text.match(/export function configurationDigest\(/g)?.length ?? 0).reduce((total, count) => total + count, 0)).toBe(1);
});

test('failure evidence has a safe, minimal deterministic replay command', () => {
  expect(replayCommand('CRH-050-GATEWAY-RESTART', 'seed-42')).toBe('KAUTUKA_ACCEPTANCE_SEED=seed-42 pnpm --dir tests/channel-acceptance exec playwright test --grep CRH-050-GATEWAY-RESTART');
  expect(() => replayCommand('not a scenario', 'seed-42')).toThrow(/replay-safe/);
  expect(() => replayCommand('CRH-050-GATEWAY-RESTART', 'seed with spaces')).toThrow(/safe opaque/);
});

test('retained browser artifacts cannot contain raw network captures or secret-shaped text', async () => {
  const config = await readFile(join(process.cwd(), 'playwright.config.ts'), 'utf8');
  expect(config).toContain("trace: 'off'");
  expect(config).toContain("video: 'off'");
  await assertSafeRetainedArtifacts(join(process.cwd(), 'test-results', 'artifacts'));
});

test('connect diagnostics retain only the approved correlation fields', () => {
  const diagnostic = projectConnectDiagnostic('POST', 'http://127.0.0.1:20229/api/narad/v1/agents/a-1/sessions/session-1/messages', {
    sessionId: 'session-1', runId: 'run-1', resume: { interruptId: 'interrupt-1', status: 'paused', message: 'do not retain' },
    message: 'do not retain', authorization: 'Bearer do-not-retain', token: 'xoxb-do-not-retain', headers: { cookie: 'do-not-retain' },
  }, 123);
  expect(diagnostic).toEqual({ timestampMs: 123, threadId: 'session-1', runId: 'run-1', resume: { interruptId: 'interrupt-1', status: 'paused' }, endpoint: 'message' });
  expect(JSON.stringify(diagnostic)).not.toMatch(/prompt|authorization|token|cookie|do-not-retain|"message":/i);
});

test('connect diagnostics record only canonical correlation, run, and wait identifiers', () => {
  const identifiers: unknown[] = [];
  const sink = { addConnectDiagnostic: () => undefined, addIdentifier: (value: unknown) => identifiers.push(value) } as never;
  recordConnectDiagnostic(sink, { timestampMs: 1, threadId: 'thread-1', runId: 'run-1', resume: { interruptId: 'wait-1', status: 'paused' } });
  expect(identifiers).toEqual([{ kind: 'correlation', value: 'thread-1' }, { kind: 'run', value: 'run-1' }, { kind: 'wait', value: 'wait-1' }]);
});
