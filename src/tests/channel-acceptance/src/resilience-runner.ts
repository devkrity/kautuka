import { resilienceCaseId, type ResilienceFailure, type ResilienceProfile } from './resilience.js';

/** Test-owned boundary driver. Production services never receive this control. */
export interface ResilienceBoundaryDriver {
  restart(boundary: Extract<ResilienceFailure, { action: 'restart' }>['boundary'], caseId: string): Promise<void>;
  partition(failure: Extract<ResilienceFailure, { action: 'partition' }>, caseId: string): Promise<void>;
  converge(caseId: string): Promise<ResilienceObservation>;
}

export interface ResilienceObservation {
  readonly caseId: string;
  readonly latencyMs: number;
  readonly queueDepth: number;
  readonly retryCount: number;
  readonly unresolvedWork: number;
  readonly storageBytes: number;
  readonly memoryBytes: number;
  readonly handleCount?: number;
}

/** Executes every selected boundary in a fixed order and rejects non-convergence. */
export async function runResilienceMatrix(profile: ResilienceProfile, driver: ResilienceBoundaryDriver): Promise<readonly ResilienceObservation[]> {
  const observations: ResilienceObservation[] = [];
  for (let iteration = 0; iteration < profile.iterations; iteration++) {
    for (let failureIndex = 0; failureIndex < profile.failureMix.length; failureIndex++) {
      const failure = profile.failureMix[failureIndex];
      const caseId = resilienceCaseId(profile, iteration, failureIndex);
      if (failure.action === 'restart') await driver.restart(failure.boundary, caseId);
      else await driver.partition(failure, caseId);
      const observation = await driver.converge(caseId);
      if (observation.caseId !== caseId) throw new Error(`Resilience observation identity mismatch for ${caseId}.`);
      if (observation.unresolvedWork !== 0) throw new Error(`Resilience case ${caseId} did not converge: unresolvedWork=${observation.unresolvedWork}.`);
      observations.push(observation);
    }
  }
  return observations;
}
