import { expect, test } from '@playwright/test';
import { assertNoDuplicateEventIds } from '../src/narad-assertions.js';
import {
  assertJournalProgressReplay,
  assertProgressReplayStable,
  assertPublicProgressPrivacy,
  assertReplayLiveConvergence,
  assertSessionSequenceMonotonic,
  assertSessionWatchRevision,
} from '../src/narad-replay-assertions.js';
import { loadConformanceTrace } from '../src/narad-conformance.js';
import { projectNaradRecordsFromResponse } from '../src/narad-stream-capture.js';

test('synthetic capture helpers enforce reconnect/progress/watch assertions', async () => {
  const reconnect = await loadConformanceTrace('reconnect-revision.jsonl');
  assertSessionSequenceMonotonic(reconnect);
  assertNoDuplicateEventIds(reconnect);
  assertReplayLiveConvergence(reconnect.slice(0, 3), reconnect.slice(3));

  const progress = await loadConformanceTrace('progress-replay.jsonl');
  assertJournalProgressReplay(progress);
  assertProgressReplayStable(progress, 2);

  const watch = await loadConformanceTrace('workspace-watch.jsonl');
  assertSessionWatchRevision(watch.map((record) => ({ ...record, endpointKind: 'workspace-watch' as const })));
});

test('projectNaradRecordsFromResponse retains endpoint kind without sensitive payload', () => {
  const sse = [
    'data: {"type":"progress.recorded","protocolVersion":"narad/v1","eventId":"evt-1","sessionId":"s1","sessionSequence":1,"progressId":"phase","privacy":"public","detail":"secret-detail","actorId":"operator-1"}',
    '',
  ].join('\n');
  const records = projectNaradRecordsFromResponse('/api/narad/v1/sessions/s1/stream', sse);
  expect(records).toHaveLength(1);
  expect(records[0]?.endpointKind).toBe('stream');
  expect(records[0]?.progressId).toBe('phase');
  expect(records[0]).not.toHaveProperty('detail');
  expect(records[0]).not.toHaveProperty('actorId');
  assertPublicProgressPrivacy(records);
});
