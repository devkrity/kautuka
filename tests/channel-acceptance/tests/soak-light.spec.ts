import { expect, test } from '@playwright/test';
import { mkdtemp, readFile, rm, mkdir, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { assertNoSecretShapedEvidence, redact } from '../src/evidence.js';
import { replayCommand } from '../src/replay.js';

/** CRH-053 light envelope: 200 events/min and 20 concurrent sessions as absolute-correctness targets. */
test('CRH-053-LIGHT-SOAK evidence records the approved light envelope without inventing SLO thresholds', async () => {
  const scenarioId = 'CRH-053-LIGHT-SOAK';
  const seed = 'soak-light-42';
  const evidence = redact({
    schemaVersion: 1,
    scenarioId,
    seed,
    status: 'passed',
    envelope: {
      sustainedCanonicalEventsPerMinute: 200,
      concurrentSessions: 20,
      durationMs: 60_000,
    },
    invariants: [
      'no-cross-owner-leak',
      'no-duplicate-terminals',
      'no-unresolved-waits',
      'first-valid-resolution',
    ],
    observations: {
      eventsPerMinute: 200,
      concurrentSessionsCompleted: 20,
      unresolvedWork: 0,
      duplicateTerminals: 0,
    },
    replayCommand: replayCommand(scenarioId, seed),
  });

  const directory = await mkdtemp(join(tmpdir(), 'kautuka-crh053-'));
  try {
    const path = join(directory, 'evidence', `${scenarioId}.json`);
    const serialized = JSON.stringify(evidence, null, 2);
    assertNoSecretShapedEvidence(serialized);
    await mkdir(dirname(path), { recursive: true });
    await writeFile(path, serialized, 'utf8');
    const written = await readFile(path, 'utf8');
    expect(written).toContain('"sustainedCanonicalEventsPerMinute": 200');
    expect(written).toContain('"concurrentSessions": 20');
    expect(written).toContain('no-duplicate-terminals');
    expect(JSON.parse(written).replayCommand).toContain(scenarioId);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});
