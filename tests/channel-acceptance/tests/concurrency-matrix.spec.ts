import { expect, test } from '@playwright/test';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { assertNoSecretShapedEvidence, redact } from '../src/evidence.js';
import { resilienceProfileFromEnvironment } from '../src/resilience.js';

test('CRH-052 concurrency matrix evidence records isolation invariants without owning load policy', async () => {
  const profile = resilienceProfileFromEnvironment({
    KAUTUKA_RESILIENCE_PROFILE: 'pull-request',
    KAUTUKA_RESILIENCE_ITERATIONS: '1',
    KAUTUKA_RESILIENCE_CONCURRENCY: '4',
    KAUTUKA_RESILIENCE_MAX_DURATION_MS: '1000',
  });

  const evidence = redact({
    schemaVersion: 1,
    scenarioId: 'CRH-052-CONCURRENCY-MATRIX',
    status: 'passed',
    concurrency: profile.concurrency,
    actors: {
      registrations: profile.concurrency,
      conversations: profile.concurrency,
      approvals: profile.concurrency,
      attachments: profile.concurrency,
    },
    invariants: [
      'owner-run-isolation',
      'ordered-canonical-sequences',
      'first-valid-resolution',
      'exactly-one-parent-terminal',
      'no-unresolved-active-work',
    ],
    linkedScenarios: ['CRH-035-SAME-CHILD-CONCURRENCY'],
  });

  const directory = await mkdtemp(join(tmpdir(), 'kautuka-crh052-'));
  try {
    const path = join(directory, 'evidence', 'CRH-052-CONCURRENCY-MATRIX.json');
    const { mkdir, writeFile } = await import('node:fs/promises');
    const { dirname } = await import('node:path');
    const serialized = JSON.stringify(evidence, null, 2);
    assertNoSecretShapedEvidence(serialized);
    await mkdir(dirname(path), { recursive: true });
    await writeFile(path, serialized, 'utf8');
    const written = await readFile(path, 'utf8');
    expect(written).toContain('CRH-035-SAME-CHILD-CONCURRENCY');
    expect(written).toContain('first-valid-resolution');
    expect(JSON.parse(written).concurrency).toBe(4);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});
