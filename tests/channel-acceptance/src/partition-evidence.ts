import { mkdir, writeFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { assertNoSecretShapedEvidence, redact } from './evidence.js';
import type { ResilienceFailure } from './resilience.js';

export interface PartitionObservation {
  readonly boundary: ResilienceFailure['boundary'];
  readonly caseId: string;
  readonly durationMs: number;
  readonly assertion: string;
  readonly provider?: string;
  readonly fault?: string;
}

/** Evidence-only writer for CRH-051. Does not invent product control planes. */
export async function writePartitionEvidence(directory: string, value: unknown): Promise<void> {
  const path = join(directory, 'evidence', 'CRH-051-PARTITION-MATRIX.json');
  const serialized = JSON.stringify(redact(value), null, 2);
  assertNoSecretShapedEvidence(serialized);
  await mkdir(dirname(path), { recursive: true });
  await writeFile(path, serialized, 'utf8');
}

export function partitionObservationFromFailure(
  failure: Extract<ResilienceFailure, { action: 'partition' }>,
  caseId: string,
  durationMs: number,
  assertion: string,
): PartitionObservation {
  if (failure.boundary === 'provider-http' || failure.boundary === 'provider-websocket') {
    return {
      boundary: failure.boundary,
      caseId,
      durationMs,
      assertion,
      provider: failure.provider,
      fault: failure.fault,
    };
  }

  return {
    boundary: failure.boundary,
    caseId,
    durationMs,
    assertion,
  };
}
