import { expect, test } from '@playwright/test';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { partitionObservationFromFailure, writePartitionEvidence } from '../src/partition-evidence.js';
import { resilienceCaseId, resilienceProfileFromEnvironment } from '../src/resilience.js';
import { runResilienceMatrix, type ResilienceBoundaryDriver } from '../src/resilience-runner.js';

function profile(profile: string, iterations = '2', concurrency = '3', duration = '5000') {
  return resilienceProfileFromEnvironment({
    KAUTUKA_RESILIENCE_PROFILE: profile,
    KAUTUKA_RESILIENCE_ITERATIONS: iterations,
    KAUTUKA_RESILIENCE_CONCURRENCY: concurrency,
    KAUTUKA_RESILIENCE_MAX_DURATION_MS: duration,
  });
}

test('resilience profiles cover every approved restart and partition boundary without owning load policy', () => {
  const pr = profile('pull-request');
  const nightly = profile('nightly');
  const release = profile('release-candidate');

  expect(pr.failureMix.map((failure) => failure.boundary)).toEqual(['gateway-process', 'provider-http', 'browser-sse']);
  expect(new Set(nightly.failureMix.map((failure) => failure.boundary))).toEqual(new Set([
    'gateway-process', 'channel-host-process', 'provider-simulator-process', 'provider-http', 'provider-websocket', 'gateway-channelhost-mtls', 'browser-sse', 'service-dependency',
  ]));
  expect(new Set(release.failureMix.map((failure) => failure.boundary))).toEqual(new Set(nightly.failureMix.map((failure) => failure.boundary)));
  expect(resilienceCaseId(nightly, 1, 2)).toBe('nightly:i1:f2');
});

test('resilience profiles require explicit owner-approved execution quantities', () => {
  expect(() => resilienceProfileFromEnvironment({ KAUTUKA_RESILIENCE_PROFILE: 'pull-request' })).toThrow(/ITERATIONS/);
  expect(() => profile('nightly', '0')).toThrow(/positive integer/);
  expect(() => profile('release-candidate', '1', 'x')).toThrow(/positive integer/);
  expect(() => resilienceCaseId(profile('pull-request'), 2, 0)).toThrow(/outside/);
});

test('matrix execution is deterministic, exercises selected boundaries, and rejects incomplete convergence', async () => {
  const calls: string[] = [];
  const driver: ResilienceBoundaryDriver = {
    async restart(boundary, caseId) { calls.push(`restart:${boundary}:${caseId}`); },
    async partition(failure, caseId) { calls.push(`partition:${failure.boundary}:${caseId}`); },
    async converge(caseId) { return { caseId, latencyMs: 1, queueDepth: 0, retryCount: 0, unresolvedWork: 0, storageBytes: 0, memoryBytes: 0, handleCount: 0 }; },
  };
  const observations = await runResilienceMatrix(profile('pull-request', '1'), driver);
  expect(calls).toEqual([
    'restart:gateway-process:pull-request:i0:f0',
    'partition:provider-http:pull-request:i0:f1',
    'partition:browser-sse:pull-request:i0:f2',
  ]);
  expect(observations).toHaveLength(3);
  await expect(runResilienceMatrix(profile('pull-request', '1'), { ...driver, async converge(caseId) { return { caseId, latencyMs: 1, queueDepth: 0, retryCount: 0, unresolvedWork: 1, storageBytes: 0, memoryBytes: 0 }; } })).rejects.toThrow(/did not converge/);
});

test('CRH-051 partition evidence writer records every release-candidate partition boundary without secrets', async () => {
  const release = profile('release-candidate', '1', '1', '1000');
  const partitions = release.failureMix.filter((failure) => failure.action === 'partition');
  expect(new Set(partitions.map((failure) => failure.boundary))).toEqual(new Set([
    'provider-http',
    'provider-websocket',
    'gateway-channelhost-mtls',
    'browser-sse',
    'service-dependency',
  ]));

  const directory = await mkdtemp(join(tmpdir(), 'kautuka-partition-evidence-'));
  try {
    const observations = release.failureMix
    .map((failure, index) => ({ failure, index }))
    .filter((entry): entry is { failure: Extract<typeof release.failureMix[number], { action: 'partition' }>; index: number } =>
      entry.failure.action === 'partition')
    .map((entry) => partitionObservationFromFailure(
      entry.failure,
      resilienceCaseId(release, 0, entry.index),
      1,
      'evidence-only partition cell',
    ));
    await writePartitionEvidence(directory, {
      schemaVersion: 1,
      scenarioId: 'CRH-051-PARTITION-MATRIX',
      status: 'passed',
      observations,
    });
    const written = await readFile(join(directory, 'evidence', 'CRH-051-PARTITION-MATRIX.json'), 'utf8');
    expect(written).toContain('provider-http');
    expect(written).toContain('browser-sse');
    expect(written).not.toMatch(/BEGIN [A-Z ]+ KEY|password|token/i);
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
});
