import { expect, test } from '@playwright/test';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { partitionObservationFromFailure, writePartitionEvidence } from '../src/partition-evidence.js';
import { resilienceCaseId, resilienceProfileFromEnvironment } from '../src/resilience.js';
import { runResilienceMatrix, type ResilienceBoundaryDriver } from '../src/resilience-runner.js';

test('CRH-051 partition matrix driver records every nightly partition cell with convergence', async () => {
  const profile = resilienceProfileFromEnvironment({
    KAUTUKA_RESILIENCE_PROFILE: 'nightly',
    KAUTUKA_RESILIENCE_ITERATIONS: '1',
    KAUTUKA_RESILIENCE_CONCURRENCY: '2',
    KAUTUKA_RESILIENCE_MAX_DURATION_MS: '1000',
  });

  const calls: string[] = [];
  const driver: ResilienceBoundaryDriver = {
    async restart(boundary, caseId) { calls.push(`restart:${boundary}:${caseId}`); },
    async partition(failure, caseId) { calls.push(`partition:${failure.boundary}:${caseId}`); },
    async converge(caseId) {
      return {
        caseId, latencyMs: 5, queueDepth: 0, retryCount: 0, unresolvedWork: 0,
        storageBytes: 1024, memoryBytes: 2048, handleCount: 1,
      };
    },
  };

  const observations = await runResilienceMatrix(profile, driver);
  expect(observations.every((item) => item.unresolvedWork === 0)).toBeTruthy();
  expect(calls.some((entry) => entry.includes('partition:provider-http'))).toBeTruthy();
  expect(calls.some((entry) => entry.includes('partition:browser-sse'))).toBeTruthy();
  expect(calls.some((entry) => entry.includes('partition:gateway-channelhost-mtls'))).toBeTruthy();

  const directory = await mkdtemp(join(tmpdir(), 'kautuka-crh051-'));
  try {
    const partitions = profile.failureMix
      .map((failure, index) => ({ failure, index }))
      .filter((entry): entry is { failure: Extract<typeof profile.failureMix[number], { action: 'partition' }>; index: number } =>
        entry.failure.action === 'partition')
      .map((entry) => partitionObservationFromFailure(
        entry.failure, resilienceCaseId(profile, 0, entry.index), 5, 'evidence-only partition convergence'));
    await writePartitionEvidence(directory, {
      schemaVersion: 1,
      scenarioId: 'CRH-051-PARTITION-MATRIX',
      status: 'passed',
      observations: partitions,
      matrixObservations: observations,
    });
    const written = await readFile(join(directory, 'evidence', 'CRH-051-PARTITION-MATRIX.json'), 'utf8');
    expect(written).toContain('provider-websocket');
    expect(written).toContain('service-dependency');
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});
