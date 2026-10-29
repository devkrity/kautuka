import { expect, test } from '@playwright/test';
import { assertNoSecretShapedEvidence, redact } from '../src/evidence.js';
import { replayCommand } from '../src/replay.js';

/** CRH-054: randomized/soak failures must emit seed + replay without secrets. */
test('CRH-054 failure triage evidence requires seed, replay, and a contiguous canonical range', async () => {
  const scenarioId = 'CRH-039-OPERATIONAL-DISCONNECT';
  const seed = 'seed-triage-42';
  const evidence = redact({
    schemaVersion: 1,
    scenarioId,
    seed,
    status: 'failed',
    failure: 'synthetic triage probe',
    canonicalEventRange: { fromSequence: 12, toSequence: 18 },
    smallestProviderTranscriptHash: 'sha256:deadbeef',
    replayCommand: replayCommand(scenarioId, seed),
  }) as {
    replayCommand: string;
    canonicalEventRange: { fromSequence: number; toSequence: number };
  };

  const serialized = JSON.stringify(evidence, null, 2);
  assertNoSecretShapedEvidence(serialized);
  expect(evidence.replayCommand).toContain(`KAUTUKA_ACCEPTANCE_SEED=${seed}`);
  expect(evidence.replayCommand).toContain(`--grep ${scenarioId}`);
  expect(evidence.canonicalEventRange.toSequence).toBeGreaterThanOrEqual(evidence.canonicalEventRange.fromSequence);
  expect(serialized).not.toMatch(/Bearer |sk-|BEGIN [A-Z ]+ KEY/);
});
