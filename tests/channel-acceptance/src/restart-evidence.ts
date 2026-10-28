import { mkdir, rm, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { assertNoSecretShapedEvidence, redact } from './evidence.js';
import { type OwnedProcessRecord, OwnedStackLifecycle } from './lifecycle.js';

export interface RestartObservation {
  readonly boundary: string;
  readonly durationMs: number;
  readonly beforePid?: number;
  readonly afterPid?: number;
  readonly beforeGeneration: number;
  readonly afterGeneration: number;
  readonly assertion: string;
}

export async function measureRestart(
  lifecycle: OwnedStackLifecycle,
  name: string,
  assertConverged: () => Promise<void>,
): Promise<RestartObservation> {
  const before = latestRecord(lifecycle.processRecords(), name);
  const started = Date.now();
  const after = await lifecycle.restartOwnedProcess(name);
  await assertConverged();
  return {
    boundary: name,
    durationMs: Date.now() - started,
    beforePid: before.pid,
    afterPid: after.pid,
    beforeGeneration: before.generation,
    afterGeneration: after.generation,
    assertion: 'new owned PID reached readiness and committed state remained observable',
  };
}

export async function writeRestartEvidence(directory: string, value: unknown, scenarioId = 'CRH-050-RESTART-MATRIX'): Promise<void> {
  if (!/^CRH-0(?:35|37|50)-[A-Z0-9-]+$/.test(scenarioId)) {
    throw new Error(`Restart evidence scenario id must be replay-safe: ${scenarioId}`);
  }
  const path = join(directory, 'evidence', `${scenarioId}.json`);
  const serialized = JSON.stringify(redact(value), null, 2);
  assertNoSecretShapedEvidence(serialized);
  await mkdir(dirname(path), { recursive: true });
  await writeFile(path, serialized, 'utf8');
}

export async function removeOwnedState(stateRoot: string): Promise<void> {
  const allowedRoot = resolve(process.cwd(), '../..', '.artifacts/channel-acceptance/resilience-state');
  const exact = resolve(stateRoot);
  if (!exact.startsWith(`${allowedRoot}\\`) && !exact.startsWith(`${allowedRoot}/`)) {
    throw new Error(`Refusing to remove non-owned resilience state ${exact}.`);
  }
  await rm(exact, { recursive: true, force: true, maxRetries: 3, retryDelay: 200 });
}

function latestRecord(records: readonly OwnedProcessRecord[], name: string): OwnedProcessRecord {
  const result = records.filter((record) => record.name === name).at(-1);
  if (!result) throw new Error(`Owned process ${name} has no process record.`);
  return result;
}
