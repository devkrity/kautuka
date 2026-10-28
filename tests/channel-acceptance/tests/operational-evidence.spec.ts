import { expect, test } from '@playwright/test';
import { projectNaradLifecycle, recordNaradLifecycle } from '../src/narad-operational-evidence.js';
import type { AcceptanceEvidence, CanonicalRange, EvidenceIdentifier, LifecycleAssertion, ServiceFinding } from '../src/model.js';

function evidence(): AcceptanceEvidence & { ranges: CanonicalRange[]; ids: EvidenceIdentifier[]; assertions: LifecycleAssertion[] } {
  const ranges: CanonicalRange[] = []; const ids: EvidenceIdentifier[] = []; const assertions: LifecycleAssertion[] = [];
  return { ranges, ids, assertions, addCanonicalRange: (range) => ranges.push(range), addIdentifier: (identifier) => ids.push(identifier), addLifecycleAssertion: (assertion) => assertions.push(assertion), addServiceFinding: (_finding: ServiceFinding) => undefined, addConnectDiagnostic: () => undefined, addSkipped: () => undefined };
}

const frame = (record: unknown) => `event: narad\ndata: ${JSON.stringify(record)}\n\n`;

test('projects Narad authority lifecycle fields from stream traffic', () => {
  const revisions = projectNaradLifecycle(frame({
    type: 'run.paused', protocolVersion: 'narad/v1', eventId: 'event-1', timestamp: '2026-08-05T00:00:00Z',
    sessionId: 'session-1', sessionSequence: 3, rootRunId: 'run-1', runId: 'run-1', sequence: 3, waitId: 'wait-1', reason: 'interrupt', resumeMode: 'command',
  }) + frame({
    type: 'run.finished', protocolVersion: 'narad/v1', eventId: 'event-2', timestamp: '2026-08-05T00:00:01Z',
    sessionId: 'session-1', sessionSequence: 4, rootRunId: 'run-1', runId: 'run-1', sequence: 4,
  }));
  expect(revisions).toEqual([
    { eventId: 'event-1', eventType: 'run.paused', rootRunId: 'run-1', sessionSequence: 3, awaitingAuthority: true },
    { eventId: 'event-2', eventType: 'run.finished', rootRunId: 'run-1', sessionSequence: 4, awaitingAuthority: false },
  ]);
});

test('records Narad sessionSequence ranges and lifecycle assertions', () => {
  const sink = evidence();
  recordNaradLifecycle(sink, projectNaradLifecycle(frame({
    type: 'run.paused', protocolVersion: 'narad/v1', eventId: 'event-1', timestamp: '2026-08-05T00:00:00Z',
    sessionId: 'session-1', sessionSequence: 3, rootRunId: 'run-1', runId: 'run-1', sequence: 3, waitId: 'wait-1', reason: 'interrupt', resumeMode: 'command',
  }) + frame({
    type: 'run.finished', protocolVersion: 'narad/v1', eventId: 'event-2', timestamp: '2026-08-05T00:00:01Z',
    sessionId: 'session-1', sessionSequence: 4, rootRunId: 'run-1', runId: 'run-1', sequence: 4,
  })));
  expect(sink.ranges).toEqual([{ firstEventId: 'event-1', lastEventId: 'event-2', firstSequence: 3, lastSequence: 4 }]);
  expect(sink.assertions).toEqual([{ runId: 'run-1', outcome: 'terminal-once', eventIds: ['event-1', 'event-2'] }]);
});

test('fails Narad operational evidence on duplicate terminal results or unresolved authority waits', () => {
  expect(() => recordNaradLifecycle(evidence(), projectNaradLifecycle(frame({
    type: 'run.finished', protocolVersion: 'narad/v1', eventId: 'event-1', timestamp: '2026-08-05T00:00:00Z',
    sessionId: 'session-1', sessionSequence: 1, rootRunId: 'run-1', runId: 'run-1', sequence: 1,
  }) + frame({
    type: 'run.error', protocolVersion: 'narad/v1', eventId: 'event-2', timestamp: '2026-08-05T00:00:01Z',
    sessionId: 'session-1', sessionSequence: 2, rootRunId: 'run-1', runId: 'run-1', sequence: 2,
  })))).toThrow(/duplicate terminal/);
  expect(() => recordNaradLifecycle(evidence(), projectNaradLifecycle(frame({
    type: 'run.finished', protocolVersion: 'narad/v1', eventId: 'event-3', timestamp: '2026-08-05T00:00:00Z',
    sessionId: 'session-1', sessionSequence: 1, rootRunId: 'run-2', runId: 'run-2', sequence: 1,
  }) + frame({
    type: 'run.paused', protocolVersion: 'narad/v1', eventId: 'event-4', timestamp: '2026-08-05T00:00:01Z',
    sessionId: 'session-1', sessionSequence: 2, rootRunId: 'run-2', runId: 'run-2', sequence: 2, waitId: 'wait-1', reason: 'interrupt', resumeMode: 'command',
  })))).toThrow(/unresolved authority wait/);
});
