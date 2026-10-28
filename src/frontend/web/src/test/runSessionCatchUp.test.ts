import { describe, expect, it, vi } from 'vitest';
import { NARAD_PROFILE_IDS, createNaradReducer, type NaradProfileId } from '@devkrity/narad';
import { runSessionCatchUp } from '../features/chat/kautuka/runSessionCatchUp';

const ALL_PROFILES = Object.values(NARAD_PROFILE_IDS);

describe('runSessionCatchUp', () => {
  it('does not skip when watch revision pointer is ahead of applied authority', async () => {
    const reducer = createNaradReducer({ sessionId: 'session-1', activeProfiles: ALL_PROFILES });
    const store = {
      subscribe: () => () => undefined,
      getSnapshot: () => reducer.getSnapshot(),
      apply: (value: unknown) => reducer.apply(value),
      clearSessionGap: () => reducer.clearSessionGap(),
      clearResyncRequired: () => reducer.clearResyncRequired(),
      setActiveProfiles: (profiles: readonly NaradProfileId[]) => reducer.setActiveProfiles(profiles),
    };
    for (let sequence = 1; sequence <= 8; sequence += 1) {
      store.apply({
        type: 'run.started',
        protocolVersion: 'narad/v1',
        eventId: `evt-${sequence}`,
        timestamp: '2026-08-05T00:00:00Z',
        sessionId: 'session-1',
        sessionSequence: sequence,
        rootRunId: `run-${sequence}`,
        runId: `run-${sequence}`,
        sequence: 1,
      });
    }
    store.apply({
      type: 'session.revision',
      protocolVersion: 'narad/v1',
      eventId: 'watch-29',
      timestamp: '2026-08-05T00:00:10Z',
      sessionId: 'session-1',
      revision: 29,
    });

    const fetchPages = vi.fn(async (_cursor: number, apply: (record: unknown) => void) => {
      apply({
        type: 'run.started',
        protocolVersion: 'narad/v1',
        eventId: 'evt-9',
        timestamp: '2026-08-05T00:00:11Z',
        sessionId: 'session-1',
        sessionSequence: 9,
        rootRunId: 'run-9',
        runId: 'run-9',
        sequence: 1,
      });
    });

    const result = await runSessionCatchUp({
      store,
      targetRevision: 29,
      signal: new AbortController().signal,
      fetchPages,
    });

    expect(result.ok).toBe(true);
    expect(fetchPages).toHaveBeenCalledTimes(1);
    expect(store.getSnapshot().lastSessionSequence).toBe(9);
    expect(store.getSnapshot().sessionRevision).toBe(29);
  });
});
