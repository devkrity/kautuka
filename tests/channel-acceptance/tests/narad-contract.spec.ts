import { expect, test } from '@playwright/test';
import {
  assertControlSeparateFromJournal,
  assertGraphAnchoredBySourceRunCursor,
  assertHydrateWithoutAuthorityFields,
  assertInterruptPauseResume,
  assertNoDuplicateEventIds,
  assertProtocolVersion,
  assertSessionSequenceContiguous,
  assertStopBeforeParentTerminal,
  assertTerminalOncePerRun,
} from '../src/narad-assertions.js';
import {
  assertJournalProgressReplay,
  assertProgressReplayStable,
  assertReplayLiveConvergence,
  assertSessionSequenceMonotonic,
} from '../src/narad-replay-assertions.js';
import { loadConformanceTrace, requiredConformanceTraces } from '../src/narad-conformance.js';
import { parseSseRecords } from '../src/narad-sse.js';
import { recordClass } from '../src/narad-types.js';

test('loads every required Narad conformance trace', async () => {
  for (const name of requiredConformanceTraces) {
    const records = await loadConformanceTrace(name);
    expect(records.length, name).toBeGreaterThan(0);
    assertProtocolVersion(records);
  }
});

test('validates conversation stream/replay semantics from snapshot-replay trace', async () => {
  const records = await loadConformanceTrace('snapshot-replay.jsonl');
  expect(records.map((record) => record.type)).toEqual(expect.arrayContaining(['messages.snapshot', 'message.text.started', 'message.text.delta', 'message.text.completed']));
  assertHydrateWithoutAuthorityFields(records);
});

test('validates interrupt pause/resume semantics from interrupt-pause trace', async () => {
  const records = await loadConformanceTrace('interrupt-pause.jsonl');
  assertInterruptPauseResume(records);
  assertTerminalOncePerRun(records);
  assertSessionSequenceContiguous(records);
});

test('validates graph hydrate snapshot/delta anchored by sourceRunCursor', async () => {
  const records = await loadConformanceTrace('run-graph.jsonl');
  assertGraphAnchoredBySourceRunCursor(records);
  assertTerminalOncePerRun(records);
});

test('validates reconnect recovery without duplicate event identities', async () => {
  const records = await loadConformanceTrace('reconnect-revision.jsonl');
  assertNoDuplicateEventIds(records);
  assertSessionSequenceMonotonic(records);
  assertReplayLiveConvergence(records.slice(0, 3), records.slice(3));
});

test('validates durable progress replay and privacy behavior', async () => {
  const records = await loadConformanceTrace('progress-replay.jsonl');
  assertJournalProgressReplay(records);
  assertProgressReplayStable(records, 2);
});

test('validates workspace/session watch revision records', async () => {
  const records = await loadConformanceTrace('workspace-watch.jsonl');
  expect(records.map((record) => record.type)).toEqual([
    'workspace.revision', 'session.created', 'session.updated', 'session.created', 'session.archived',
  ]);
});

test('validates stop cascade before parent terminal', async () => {
  const records = await loadConformanceTrace('client-tool-stop.jsonl');
  assertStopBeforeParentTerminal(records);
});

test('parses Narad SSE frames by record type rather than transport event name', () => {
  const sse = [
    'event: narad',
    'id: evt-1',
    'data: {"type":"run.started","protocolVersion":"narad/v1","eventId":"evt-1","timestamp":"2026-08-05T00:00:00Z","sessionId":"s1","sessionSequence":1,"rootRunId":"r1","runId":"r1","sequence":1}',
    '',
    'event: narad',
    'data: {"type":"server.heartbeat","protocolVersion":"narad/v1","serverTime":"2026-08-05T00:00:01Z"}',
    '',
  ].join('\n');
  const records = parseSseRecords(sse);
  expect(records.map((record) => recordClass(record))).toEqual(['authority', 'control']);
  assertControlSeparateFromJournal(records);
});

test('authority replay dedupes identical event identities', async () => {
  const records = await loadConformanceTrace('authority-replay.jsonl');
  assertNoDuplicateEventIds(records);
});
