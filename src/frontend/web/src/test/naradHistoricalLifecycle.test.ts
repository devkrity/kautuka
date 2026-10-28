import { createNaradReducer, NARAD_PROFILE_IDS } from '@devkrity/narad';
import { describe, expect, it } from 'vitest';

const event = (seq: number, type: string, body: Record<string, unknown> = {}) => ({
  type, protocolVersion: 'narad/v1', eventId: `event-${seq}`, timestamp: '2026-10-16T00:00:00Z',
  sessionId: 'session-1', sessionSequence: seq, rootRunId: 'root-1', runId: 'root-1', sequence: seq, ...body,
});
const tool = { toolCallId: 'call-1', toolName: 'invoke_agent_roller', executor: 'backend', args: { message: 'roll 44' } };
const create = () => createNaradReducer({ sessionId: 'session-1', activeProfiles: Object.values(NARAD_PROFILE_IDS) });

describe('historical Gateway lifecycle compatibility', () => {
  it('advances authority through a repeated identical open-tool notification', () => {
    const reducer = create();
    reducer.apply(event(1, 'run.started'));
    reducer.apply(event(2, 'tool.call.started', tool));
    const startedAt = reducer.getSnapshot().tools.get('call-1')?.startedAt;
    expect(reducer.apply(event(3, 'tool.call.started', tool)).applied).toBe(true);
    expect(reducer.getSnapshot().tools.size).toBe(1);
    expect(reducer.getSnapshot().tools.get('call-1')?.startedAt).toBe(startedAt);
    expect(reducer.apply(event(4, 'tool.call.finished', { toolCallId: 'call-1', outcome: 'success', response: '44' })).applied).toBe(true);
    expect(reducer.apply(event(5, 'run.finished')).applied).toBe(true);
    expect(reducer.getSnapshot().lastSessionSequence).toBe(5);
    expect(reducer.getSnapshot().sessionGap).toBe(false);
  });
  it('still rejects reuse with different arguments', () => {
    const reducer = create();
    reducer.apply(event(1, 'run.started'));
    reducer.apply(event(2, 'tool.call.started', tool));
    expect(reducer.apply(event(3, 'tool.call.started', { ...tool, args: { message: 'roll 777' } })).error).toBeTruthy();
    expect(reducer.getSnapshot().lastSessionSequence).toBe(2);
  });
  it('still rejects restarting a completed tool', () => {
    const reducer = create();
    reducer.apply(event(1, 'run.started'));
    reducer.apply(event(2, 'tool.call.started', tool));
    expect(reducer.apply(event(3, 'tool.call.finished', { toolCallId: 'call-1', outcome: 'success', response: '44' })).applied).toBe(true);
    expect(reducer.apply(event(4, 'tool.call.started', tool)).error).toBeTruthy();
  });
  it('preserves a finished run when old recovery appended a redundant process-lost error', () => {
    const reducer = create();
    reducer.apply(event(1, 'run.started'));
    reducer.apply(event(2, 'run.finished'));
    expect(reducer.apply(event(3, 'run.error', { code: 'gateway_process_lost', message: 'Restarted' })).applied).toBe(true);
    expect(reducer.getSnapshot().runs.get('root-1')?.state).toBe('finished');
    expect(reducer.getSnapshot().lastSessionSequence).toBe(3);
    expect(reducer.apply(event(4, 'run.error', { code: 'other_error', message: 'Unexpected' })).error).toBeTruthy();
  });
});
