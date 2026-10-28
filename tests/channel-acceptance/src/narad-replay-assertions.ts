import { expect } from '@playwright/test';
import type { NaradRecord } from './narad-types.js';
import { assertNoDuplicateEventIds, assertSessionSequenceContiguous } from './narad-assertions.js';

export function assertSessionSequenceMonotonic(records: readonly NaradRecord[]): void {
  const authority = records
    .filter((record) => typeof record.sessionSequence === 'number')
    .sort((left, right) => (left.sessionSequence as number) - (right.sessionSequence as number));
  for (let index = 1; index < authority.length; index += 1) {
    expect(
      authority[index]!.sessionSequence as number,
      `sessionSequence must not regress at ${authority[index]!.eventId ?? authority[index]!.type}`,
    ).toBeGreaterThanOrEqual(authority[index - 1]!.sessionSequence as number);
  }
  assertSessionSequenceContiguous(records);
}

export { assertNoDuplicateEventIds };

export function assertReplayLiveConvergence(beforeReload: readonly NaradRecord[], afterReload: readonly NaradRecord[]): void {
  const isAuthority = (record: NaradRecord) =>
    typeof record.eventId === 'string' && typeof record.sessionSequence === 'number';
  const authorityBefore = beforeReload.filter(isAuthority);
  const authorityAfter = afterReload.filter(isAuthority);
  expect(authorityAfter.length, 'reload must replay authority history').toBeGreaterThan(0);
  for (const prior of authorityBefore) {
    const replayed = authorityAfter.find((record) => record.eventId === prior.eventId);
    expect(replayed, `missing replay for ${prior.eventId}`).toBeTruthy();
    expect(replayed).toEqual(prior);
  }
  const maxBefore = Math.max(0, ...authorityBefore.map((record) => record.sessionSequence as number));
  const minAfter = Math.min(...authorityAfter.map((record) => record.sessionSequence as number));
  expect(minAfter, 'post-reload sequences must continue from prior authority cursor').toBeLessThanOrEqual(maxBefore + 1);
}

export function assertPublicProgressPrivacy(records: readonly NaradRecord[]): void {
  const progress = records.filter((record) => record.type === 'progress.recorded');
  const publicProgress = progress.filter((record) => record.privacy === 'public');
  expect(publicProgress.length, 'public stream must deliver public progress').toBeGreaterThan(0);
  expect(progress.some((record) => record.privacy === 'internal'), 'internal progress must not leak to public stream').toBe(false);
  expect(progress.some((record) => record.privacy === 'sensitive'), 'sensitive progress must not leak to public stream').toBe(false);
}

export function assertJournalProgressReplay(records: readonly NaradRecord[]): void {
  const progress = records.filter((record) => record.type === 'progress.recorded');
  expect(progress.some((record) => record.privacy === 'public'), 'journal retains public progress').toBe(true);
  expect(progress.some((record) => record.privacy === 'internal'), 'journal retains internal progress separately from public delivery').toBe(true);
  assertNoDuplicateEventIds(records);
}

export function assertProgressReplayStable(records: readonly NaradRecord[], boundaryIndex: number): void {
  const beforeReload = records.slice(0, boundaryIndex);
  const afterReload = records.slice(boundaryIndex);
  const publicBefore = beforeReload.filter((record) => record.type === 'progress.recorded' && record.privacy === 'public');
  expect(publicBefore.length, 'progress must be emitted before reload').toBeGreaterThan(0);
  for (const prior of publicBefore) {
    expect(typeof prior.progressId, prior.eventId ?? prior.type).toBe('string');
    const replayed = afterReload.find((record) => record.eventId === prior.eventId);
    expect(replayed, `progress ${prior.progressId} must replay after reload`).toBeTruthy();
    expect(replayed?.progressId).toBe(prior.progressId);
    expect(replayed?.privacy).toBe('public');
  }
}

export function assertSessionWatchRevision(records: readonly NaradRecord[]): void {
  const watchRecords = records.filter((record) => record.type === 'session.revision' || record.endpointKind === 'watch');
  const revisions = watchRecords
    .filter((record) => typeof record.revision === 'number')
    .map((record) => record.revision as number);
  if (revisions.length === 0) return;
  for (let index = 1; index < revisions.length; index += 1) {
    expect(revisions[index]!, 'session watch revision must be monotonic').toBeGreaterThanOrEqual(revisions[index - 1]!);
  }
  const authorityMax = Math.max(0, ...records
    .filter((record) => typeof record.sessionSequence === 'number')
    .map((record) => record.sessionSequence as number));
  expect(Math.max(...revisions), 'watch revision must catch up to authority cursor').toBeGreaterThanOrEqual(authorityMax);
}

export function assertWorkspaceWatchBehavior(records: readonly NaradRecord[]): void {
  const workspaceRecords = records.filter((record) =>
    record.endpointKind === 'workspace-watch'
    || ['workspace.revision', 'session.created', 'session.updated', 'session.archived'].includes(record.type));
  if (workspaceRecords.length === 0) return;
  const revisions = workspaceRecords
    .filter((record) => typeof record.revision === 'number')
    .map((record) => record.revision as number);
  for (let index = 1; index < revisions.length; index += 1) {
    expect(revisions[index]!, 'workspace watch revision must be monotonic').toBeGreaterThanOrEqual(revisions[index - 1]!);
  }
}
