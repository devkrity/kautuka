import { expect } from '@playwright/test';
import {
  assertNoDuplicateEventIds,
  assertProgressReplayStable,
  assertPublicProgressPrivacy,
  assertReplayLiveConvergence,
  assertSessionSequenceMonotonic,
  assertSessionWatchRevision,
  assertWorkspaceWatchBehavior,
} from './narad-replay-assertions.js';
import type { NaradCaptureRecord } from './narad-stream-capture-types.js';

export function assertCaptureRunTerminalOrdering(records: readonly NaradCaptureRecord[], boundaryIndex: number): void {
  const slice = records.slice(boundaryIndex);
  const terminals = slice.flatMap((record, index) => (['run.finished', 'run.error', 'run.cancelled'].includes(record.type) ? [index] : []));
  expect(terminals.length, `completing Narad run must emit exactly one terminal (${slice.map((record) => record.type).join(' -> ')})`).toBe(1);
  const terminalIndex = terminals[0]!;
  const graphIndexes = slice.flatMap((record, index) => (record.type === 'graph.snapshot' || record.type === 'graph.delta' ? [index] : []));
  if (graphIndexes.length > 0) expect(Math.max(...graphIndexes)).toBeLessThan(terminalIndex);
  expect(slice.slice(terminalIndex + 1).map((record) => record.type)).toEqual([]);
}

export function assertCaptureReconnectSequenceIntegrity(
  records: readonly NaradCaptureRecord[],
  boundaryIndex: number,
): void {
  const beforeReload = records.slice(0, boundaryIndex);
  const afterReload = records.slice(boundaryIndex);
  assertSessionSequenceMonotonic(records);
  assertNoDuplicateEventIds(records);
  assertReplayLiveConvergence(beforeReload, afterReload);
}

export function assertCaptureProgressReplayPrivacy(
  records: readonly NaradCaptureRecord[],
  boundaryIndex: number,
): void {
  assertPublicProgressPrivacy(records);
  assertProgressReplayStable(records, boundaryIndex);
}

export function assertCaptureWatchRevisionMonotonic(records: readonly NaradCaptureRecord[]): void {
  assertSessionWatchRevision(records);
  assertWorkspaceWatchBehavior(records);
}
