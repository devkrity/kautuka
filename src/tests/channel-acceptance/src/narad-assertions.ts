import { expect } from '@playwright/test';
import { hydrateTypes, recordClass, terminalTypes, type NaradRecord } from './narad-types.js';

export function assertProtocolVersion(records: readonly NaradRecord[]): void {
  for (const record of records) expect(record.protocolVersion, record.type).toBe('narad/v1');
}

export function assertSessionSequenceContiguous(records: readonly NaradRecord[]): void {
  const authority = records.filter((record) => typeof record.sessionSequence === 'number');
  const bySession = groupBy(authority, (record) => String(record.sessionId));
  for (const [, entries] of bySession) {
    const sequences = [...new Set(entries.map((entry) => entry.sessionSequence as number))].sort((a, b) => a - b);
    for (let index = 1; index < sequences.length; index += 1) {
      expect(sequences[index]! - sequences[index - 1]!, `sessionSequence gap in ${entries[0]?.sessionId}`).toBeLessThanOrEqual(1);
    }
  }
}

export function assertNoDuplicateEventIds(records: readonly NaradRecord[]): void {
  const seen = new Map<string, NaradRecord>();
  for (const record of records) {
    if (typeof record.eventId !== 'string') continue;
    const key = `${record.sessionId ?? ''}\u0000${record.eventId}`;
    const prior = seen.get(key);
    if (prior) expect(record).toEqual(prior);
    else seen.set(key, record);
  }
}

export function assertTerminalOncePerRun(records: readonly NaradRecord[]): void {
  const terminals = records.filter((record) => terminalTypes.has(record.type));
  const byRun = groupBy(terminals, (record) => String(record.rootRunId ?? record.runId));
  for (const [runId, entries] of byRun) expect(entries.length, `run ${runId}`).toBeLessThanOrEqual(1);
}

export function assertGraphAnchoredBySourceRunCursor(records: readonly NaradRecord[]): void {
  for (const record of records.filter((record) => record.type === 'graph.snapshot' || record.type === 'graph.delta')) {
    expect(record.sourceRunCursor, record.type).toBeTruthy();
    expect((record.sourceRunCursor as { sequence?: unknown }).sequence, record.type).toEqual(expect.any(Number));
  }
}

export function assertControlSeparateFromJournal(records: readonly NaradRecord[]): void {
  for (const record of records.filter((entry) => recordClass(entry) === 'control')) {
    expect(record.eventId, record.type).toBeUndefined();
    expect(record.sessionSequence, record.type).toBeUndefined();
  }
  for (const record of records.filter((entry) => recordClass(entry) === 'authority')) {
    expect(record.eventId, record.type).toBeTruthy();
    expect(record.sessionSequence, record.type).toEqual(expect.any(Number));
  }
}

export function assertInterruptPauseResume(records: readonly NaradRecord[]): void {
  const types = records.map((record) => record.type);
  expect(types).toContain('interrupt.requested');
  expect(types).toContain('run.paused');
  expect(types).toContain('interrupt.resolved');
  expect(types).toContain('run.resumed');
}

export function assertStopBeforeParentTerminal(records: readonly NaradRecord[]): void {
  const types = records.map((record) => record.type);
  const stopIndex = types.indexOf('run.stop.requested');
  const parentTerminal = types.findIndex((type) => terminalTypes.has(type));
  expect(stopIndex, 'stop must be requested').toBeGreaterThanOrEqual(0);
  expect(parentTerminal, 'parent must terminate').toBeGreaterThan(stopIndex);
  expect(types.slice(stopIndex + 1, parentTerminal)).toEqual(expect.arrayContaining(['tool.call.finished']));
}

export function assertNestedChildBeforeParentTerminal(records: readonly NaradRecord[]): void {
  const childStart = records.findIndex((record) => record.type === 'run.started' && record.parentRunId);
  const parentTerminal = records.findIndex((record) => terminalTypes.has(record.type) && !record.parentRunId);
  if (childStart < 0 || parentTerminal < 0) return;
  expect(childStart).toBeLessThan(parentTerminal);
}

export function assertHydrateWithoutAuthorityFields(records: readonly NaradRecord[]): void {
  for (const record of records.filter((entry) => hydrateTypes.has(entry.type))) {
    expect(record.eventId, record.type).toBeUndefined();
    expect(record.sessionSequence, record.type).toBeUndefined();
  }
}

function groupBy<T>(items: readonly T[], key: (item: T) => string): Map<string, T[]> {
  return items.reduce((groups, item) => {
    const bucket = key(item);
    groups.set(bucket, [...(groups.get(bucket) ?? []), item]);
    return groups;
  }, new Map<string, T[]>());
}
