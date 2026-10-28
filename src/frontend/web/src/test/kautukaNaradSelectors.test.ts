import { createNaradReducer, NARAD_PROFILE_IDS } from '@devkrity/narad';
import { describe, expect, it } from 'vitest';
import {
  mergeOpenInterruptsWithPendingApprovals,
  selectActiveRootGraph,
  selectComposerLocked,
  selectOpenInterrupts,
  selectPartialAssistantText,
  selectRunSummary,
  selectTranscriptTurns,
} from '@/features/chat/kautuka/selectors';
import { activityEntryProjectionIdentity } from '@/features/chat/activity/activityProjection';

const ALL_PROFILES = Object.values(NARAD_PROFILE_IDS);

function replay(reducer: ReturnType<typeof createNaradReducer>, events: readonly unknown[]) {
  for (const event of events) {
    reducer.apply(event);
  }
  return reducer.getSnapshot();
}

describe('kautuka Narad selectors', () => {
  it('keeps overlapping Autopilot and channel responses attached to their own user turns', () => {
    const reducer = createNaradReducer({ sessionId: 'session-1', activeProfiles: ALL_PROFILES });
    const messages = [
      {
        messageId: 'u-auto',
        role: 'user',
        content: 'Autopilot request',
        rootRunId: 'auto',
        runId: 'auto',
        sessionSequence: 1,
        completed: true,
      },
      {
        messageId: 'u-telegram',
        role: 'user',
        content: 'roll d956',
        rootRunId: 'telegram',
        runId: 'telegram',
        sessionSequence: 3,
        completed: true,
      },
      {
        messageId: 'a-auto',
        role: 'assistant',
        content: 'Autopilot answer',
        rootRunId: 'auto',
        runId: 'auto',
        sessionSequence: 6,
        completed: true,
      },
      {
        messageId: 'a-telegram',
        role: 'assistant',
        content: '956',
        rootRunId: 'telegram',
        runId: 'telegram',
        sessionSequence: 8,
        completed: true,
      },
    ];
    const result = reducer.apply({
      type: 'messages.snapshot',
      protocolVersion: 'narad/v1',
      timestamp: '2026-10-16T02:22:54Z',
      snapshotId: 'overlap',
      sessionId: 'session-1',
      coversThroughSessionSequence: 8,
      messages,
    });
    expect(result.applied).toBe(true);
    const turns = selectTranscriptTurns(reducer.getSnapshot());
    expect(turns).toHaveLength(2);
    expect(turns[0]?.user?.content).toBe('Autopilot request');
    expect(turns[0]?.assistant?.content).toBe('Autopilot answer');
    expect(turns[0]?.rootRunId).toBe('auto');
    expect(turns[1]?.user?.content).toBe('roll d956');
    expect(turns[1]?.assistant?.content).toBe('956');
    expect(turns[1]?.rootRunId).toBe('telegram');
  });

  it('projects partial assistant streaming text from authority snapshot', () => {
    const reducer = createNaradReducer({ sessionId: 'session-1', activeProfiles: ALL_PROFILES });
    const snapshot = replay(reducer, [
      {
        type: 'message.text.started',
        protocolVersion: 'narad/v1',
        eventId: 'evt-1',
        timestamp: '2026-08-05T00:00:00Z',
        sessionId: 'session-1',
        sessionSequence: 1,
        messageId: 'msg-1',
        role: 'assistant',
        messageScope: 'session',
      },
      {
        type: 'message.text.delta',
        protocolVersion: 'narad/v1',
        eventId: 'evt-2',
        timestamp: '2026-08-05T00:00:01Z',
        sessionId: 'session-1',
        sessionSequence: 2,
        messageId: 'msg-1',
        delta: 'Hello',
        messageScope: 'session',
      },
    ]);

    expect(selectPartialAssistantText(snapshot)).toBe('Hello');
    expect(selectTranscriptTurns(snapshot)).toEqual([
      expect.objectContaining({
        assistant: expect.objectContaining({
          content: 'Hello',
          streaming: true,
          timestamp: '2026-08-05T00:00:00Z',
        }),
      }),
    ]);
  });

  it('maps open interrupts for approval dock rendering', () => {
    const reducer = createNaradReducer({ sessionId: 'session-1', activeProfiles: ALL_PROFILES });
    const snapshot = replay(reducer, [
      {
        type: 'run.started',
        protocolVersion: 'narad/v1',
        eventId: 'evt-1',
        timestamp: '2026-08-05T00:00:00Z',
        sessionId: 'session-1',
        sessionSequence: 1,
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 1,
      },
      {
        type: 'interrupt.requested',
        protocolVersion: 'narad/v1',
        eventId: 'evt-2',
        timestamp: '2026-08-05T00:00:01Z',
        sessionId: 'session-1',
        sessionSequence: 2,
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 2,
        interruptId: 'interrupt-1',
        waitId: 'wait-1',
        kind: 'approval',
        actions: ['approve', 'deny'],
        input: { toolName: 'read_file', toolCallId: 'call-1' },
      },
    ]);

    expect(selectOpenInterrupts(snapshot)).toEqual([
      expect.objectContaining({
        id: 'interrupt-1',
        toolName: 'read_file',
        decision: 'pending',
        requestedAt: '2026-08-05T00:00:01Z',
      }),
    ]);
    expect(selectRunSummary(snapshot).isRunning).toBe(true);
  });

  it('prefers REST pending rows over the single packed Narad interrupt', () => {
    const reducer = createNaradReducer({ sessionId: 'session-1', activeProfiles: ALL_PROFILES });
    const snapshot = replay(reducer, [
      {
        type: 'run.started',
        protocolVersion: 'narad/v1',
        eventId: 'evt-1',
        timestamp: '2026-08-05T00:00:00Z',
        sessionId: 'session-1',
        sessionSequence: 1,
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 1,
      },
      {
        type: 'interrupt.requested',
        protocolVersion: 'narad/v1',
        eventId: 'evt-2',
        timestamp: '2026-08-05T00:00:01Z',
        sessionId: 'session-1',
        sessionSequence: 2,
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 2,
        interruptId: 'ficc_1',
        waitId: 'wait:ficc_1',
        kind: 'approval',
        actions: ['approve', 'deny'],
        input: {
          toolName: 'invoke_agent_roller',
          toolCallId: 'call-1',
        },
      },
    ]);

    const merged = mergeOpenInterruptsWithPendingApprovals(snapshot, [
      {
        id: 'ficc_1',
        toolCallId: 'call-1',
        toolName: 'invoke_agent_roller',
        toolType: 'agent',
        requestedAt: '2026-08-05T00:00:01.000Z',
        actions: [{ label: 'Approve', value: 'approve', style: 'success', requiresRunId: false }],
        checkpointResumable: false,
      },
      {
        id: 'ficc_2',
        toolCallId: 'call-2',
        toolName: 'kautuka_web_fetch',
        toolType: 'connector',
        requestedAt: '2026-08-05T00:00:01.000Z',
        actions: [{ label: 'Approve', value: 'approve', style: 'success', requiresRunId: false }],
        checkpointResumable: false,
      },
      {
        id: 'ficc_3',
        toolCallId: 'call-3',
        toolName: 'kautuka_web_fetch',
        toolType: 'connector',
        requestedAt: '2026-08-05T00:00:01.000Z',
        actions: [{ label: 'Approve', value: 'approve', style: 'success', requiresRunId: false }],
        checkpointResumable: false,
      },
    ]);
    expect(merged.map((approval) => approval.id)).toEqual(['ficc_1', 'ficc_2', 'ficc_3']);
  });

  it('fans packed interrupt.requested members into dock cards without REST', () => {
    const reducer = createNaradReducer({ sessionId: 'session-1', activeProfiles: ALL_PROFILES });
    const snapshot = replay(reducer, [
      {
        type: 'run.started',
        protocolVersion: 'narad/v1',
        eventId: 'evt-1',
        timestamp: '2026-08-05T00:00:00Z',
        sessionId: 'session-1',
        sessionSequence: 1,
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 1,
      },
      {
        type: 'interrupt.requested',
        protocolVersion: 'narad/v1',
        eventId: 'evt-2',
        timestamp: '2026-08-05T00:00:01Z',
        sessionId: 'session-1',
        sessionSequence: 2,
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 2,
        interruptId: 'ficc_1',
        waitId: 'wait:ficc_1',
        kind: 'approval',
        actions: ['approve', 'deny'],
        input: {
          toolName: 'invoke_agent_roller',
          toolCallId: 'call-1',
          members: [
            {
              requestId: 'ficc_1',
              toolCallId: 'call-1',
              toolName: 'invoke_agent_roller',
              toolType: 'agent',
              args: { dice: 'd27' },
            },
            {
              requestId: 'ficc_2',
              toolCallId: 'call-2',
              toolName: 'kautuka_web_fetch',
              toolType: 'mcp',
              args: { url: 'https://example.test' },
            },
          ],
        },
      },
    ]);

    const merged = mergeOpenInterruptsWithPendingApprovals(snapshot, []);
    expect(merged.map((approval) => approval.id)).toEqual(['ficc_1', 'ficc_2']);
    expect(merged.map((approval) => approval.toolName)).toEqual([
      'invoke_agent_roller',
      'kautuka_web_fetch',
    ]);
  });

  it('drops REST-decided packed members from the journal dock before pack close', () => {
    const reducer = createNaradReducer({ sessionId: 'session-1', activeProfiles: ALL_PROFILES });
    const snapshot = replay(reducer, [
      {
        type: 'run.started',
        protocolVersion: 'narad/v1',
        eventId: 'evt-1',
        timestamp: '2026-08-05T00:00:00Z',
        sessionId: 'session-1',
        sessionSequence: 1,
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 1,
      },
      {
        type: 'interrupt.requested',
        protocolVersion: 'narad/v1',
        eventId: 'evt-2',
        timestamp: '2026-08-05T00:00:01Z',
        sessionId: 'session-1',
        sessionSequence: 2,
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 2,
        interruptId: 'ficc_1',
        waitId: 'wait:ficc_1',
        kind: 'approval',
        actions: ['approve', 'deny'],
        input: {
          toolName: 'invoke_agent_roller',
          toolCallId: 'call-1',
          members: [
            {
              requestId: 'ficc_1',
              toolCallId: 'call-1',
              toolName: 'invoke_agent_roller',
              toolType: 'agent',
            },
            {
              requestId: 'ficc_2',
              toolCallId: 'call-2',
              toolName: 'kautuka_web_fetch',
              toolType: 'mcp',
            },
          ],
        },
      },
    ]);

    const merged = mergeOpenInterruptsWithPendingApprovals(snapshot, [
      {
        id: 'ficc_1',
        toolCallId: 'call-1',
        toolName: 'invoke_agent_roller',
        toolType: 'agent',
        requestedAt: '2026-08-05T00:00:01.000Z',
        decision: 'approved',
        actions: [{ label: 'Approve', value: 'approve', style: 'success', requiresRunId: false }],
        checkpointResumable: false,
      },
      {
        id: 'ficc_2',
        toolCallId: 'call-2',
        toolName: 'kautuka_web_fetch',
        toolType: 'connector',
        requestedAt: '2026-08-05T00:00:01.000Z',
        actions: [{ label: 'Approve', value: 'approve', style: 'success', requiresRunId: false }],
        checkpointResumable: false,
      },
    ]);
    expect(merged.map((approval) => approval.id)).toEqual(['ficc_2']);
  });

  it('projects packed resolved.members into the ledger without REST overlay', () => {
    const reducer = createNaradReducer({ sessionId: 'session-1', activeProfiles: ALL_PROFILES });
    const snapshot = replay(reducer, [
      {
        type: 'run.started',
        protocolVersion: 'narad/v1',
        eventId: 'evt-1',
        timestamp: '2026-08-05T00:00:00Z',
        sessionId: 'session-1',
        sessionSequence: 1,
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 1,
      },
      {
        type: 'message.user.recorded',
        protocolVersion: 'narad/v1',
        eventId: 'evt-user',
        timestamp: '2026-08-05T00:00:00Z',
        sessionId: 'session-1',
        sessionSequence: 2,
        messageId: 'user-1',
        content: 'roll and fetch',
        messageScope: 'run',
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 2,
      },
      {
        type: 'interrupt.requested',
        protocolVersion: 'narad/v1',
        eventId: 'evt-2',
        timestamp: '2026-08-05T00:00:01Z',
        sessionId: 'session-1',
        sessionSequence: 3,
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 3,
        interruptId: 'ficc_1',
        waitId: 'wait:ficc_1',
        kind: 'approval',
        actions: ['approve', 'deny'],
        input: {
          toolName: 'invoke_agent_roller',
          toolCallId: 'call-1',
          members: [
            {
              requestId: 'ficc_1',
              toolCallId: 'call-1',
              toolName: 'invoke_agent_roller',
              toolType: 'agent',
            },
            {
              requestId: 'ficc_2',
              toolCallId: 'call-2',
              toolName: 'kautuka_web_fetch',
              toolType: 'mcp',
            },
          ],
        },
      },
      {
        type: 'run.paused',
        protocolVersion: 'narad/v1',
        eventId: 'evt-pause',
        timestamp: '2026-08-05T00:00:01Z',
        sessionId: 'session-1',
        sessionSequence: 4,
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 4,
        waitId: 'wait:ficc_1',
        reason: 'interrupt',
        resumeMode: 'command',
        interruptId: 'ficc_1',
      },
      {
        type: 'interrupt.resolved',
        protocolVersion: 'narad/v1',
        eventId: 'evt-3',
        timestamp: '2026-08-05T00:00:10Z',
        sessionId: 'session-1',
        sessionSequence: 5,
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 5,
        interruptId: 'ficc_1',
        waitId: 'wait:ficc_1',
        decision: 'deny',
        members: [
          {
            requestId: 'ficc_1',
            toolCallId: 'call-1',
            toolName: 'invoke_agent_roller',
            toolType: 'agent',
            decision: 'deny',
          },
          {
            requestId: 'ficc_2',
            toolCallId: 'call-2',
            toolName: 'kautuka_web_fetch',
            toolType: 'mcp',
            decision: 'approve',
          },
        ],
      },
    ]);

    const turns = selectTranscriptTurns(snapshot, []);
    const approvalRows = turns[0]?.activityEntries.filter((entry) => entry.kind === 'approval') ?? [];
    expect(approvalRows.map((entry) => `${entry.name}:${entry.status}`).sort()).toEqual([
      'invoke_agent_roller:denied',
      'kautuka_web_fetch:approved',
    ]);
    expect(snapshot.interrupts.get('ficc_1')?.decision).toBe('deny');
    expect(snapshot.interrupts.get('ficc_1')?.members?.map((member) => member.decision)).toEqual([
      'deny',
      'approve',
    ]);
  });

  it('projects gate-before-start interrupt deny into turn activity (no tool.call.*)', () => {
    const reducer = createNaradReducer({ sessionId: 'session-1', activeProfiles: ALL_PROFILES });
    const snapshot = replay(reducer, [
      {
        type: 'run.started',
        protocolVersion: 'narad/v1',
        eventId: 'evt-1',
        timestamp: '2026-08-05T00:00:00Z',
        sessionId: 'session-1',
        sessionSequence: 1,
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 1,
      },
      {
        type: 'message.user.recorded',
        protocolVersion: 'narad/v1',
        eventId: 'evt-user',
        timestamp: '2026-08-05T00:00:00Z',
        sessionId: 'session-1',
        sessionSequence: 2,
        messageId: 'user-1',
        content: 'roll a d65',
        messageScope: 'run',
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 2,
      },
      {
        type: 'interrupt.requested',
        protocolVersion: 'narad/v1',
        eventId: 'evt-2',
        timestamp: '2026-08-05T00:00:01Z',
        sessionId: 'session-1',
        sessionSequence: 3,
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 3,
        interruptId: 'apr-1',
        waitId: 'wait:apr-1',
        kind: 'approval',
        actions: ['approve', 'deny'],
        input: {
          toolName: 'invoke_agent_roller',
          toolCallId: 'call-1',
          toolType: 'agent',
        },
      },
      {
        type: 'run.paused',
        protocolVersion: 'narad/v1',
        eventId: 'evt-3',
        timestamp: '2026-08-05T00:00:01Z',
        sessionId: 'session-1',
        sessionSequence: 4,
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 4,
        waitId: 'wait:apr-1',
        reason: 'interrupt',
        resumeMode: 'command',
        interruptId: 'apr-1',
      },
      {
        type: 'interrupt.resolved',
        protocolVersion: 'narad/v1',
        eventId: 'evt-4',
        timestamp: '2026-08-05T00:00:05Z',
        sessionId: 'session-1',
        sessionSequence: 5,
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 5,
        interruptId: 'apr-1',
        waitId: 'wait:apr-1',
        decision: 'deny',
      },
      {
        type: 'run.resumed',
        protocolVersion: 'narad/v1',
        eventId: 'evt-5',
        timestamp: '2026-08-05T00:00:05Z',
        sessionId: 'session-1',
        sessionSequence: 6,
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 6,
        waitId: 'wait:apr-1',
      },
      {
        type: 'message.text.started',
        protocolVersion: 'narad/v1',
        eventId: 'evt-6',
        timestamp: '2026-08-05T00:00:06Z',
        sessionId: 'session-1',
        sessionSequence: 7,
        messageId: 'asst-1',
        role: 'assistant',
        messageScope: 'run',
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 7,
      },
      {
        type: 'message.text.delta',
        protocolVersion: 'narad/v1',
        eventId: 'evt-7',
        timestamp: '2026-08-05T00:00:06Z',
        sessionId: 'session-1',
        sessionSequence: 8,
        messageId: 'asst-1',
        delta: 'The tool call was denied.',
        messageScope: 'run',
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 8,
      },
      {
        type: 'message.text.completed',
        protocolVersion: 'narad/v1',
        eventId: 'evt-8',
        timestamp: '2026-08-05T00:00:07Z',
        sessionId: 'session-1',
        sessionSequence: 9,
        messageId: 'asst-1',
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 9,
      },
      {
        type: 'run.finished',
        protocolVersion: 'narad/v1',
        eventId: 'evt-9',
        timestamp: '2026-08-05T00:00:07Z',
        sessionId: 'session-1',
        sessionSequence: 10,
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 10,
      },
    ]);

    const turns = selectTranscriptTurns(snapshot);
    expect(turns).toHaveLength(1);
    expect(turns[0]?.activityEntries).toEqual([
      expect.objectContaining({
        kind: 'approval',
        status: 'denied',
        name: 'invoke_agent_roller',
        toolCallId: 'call-1',
        approvalRequestId: 'apr-1',
        decidedAt: '2026-08-05T00:00:05Z',
      }),
    ]);
    expect(selectOpenInterrupts(snapshot)).toHaveLength(0);
  });

  it('does not invent tool rows after gate-before-start approve without tool.call.started', () => {
    const reducer = createNaradReducer({ sessionId: 'session-1', activeProfiles: ALL_PROFILES });
    const snapshot = replay(reducer, [
      {
        type: 'run.started',
        protocolVersion: 'narad/v1',
        eventId: 'evt-1',
        timestamp: '2026-08-05T00:00:00Z',
        sessionId: 'session-1',
        sessionSequence: 1,
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 1,
      },
      {
        type: 'message.user.recorded',
        protocolVersion: 'narad/v1',
        eventId: 'evt-2',
        timestamp: '2026-08-05T00:00:00Z',
        sessionId: 'session-1',
        sessionSequence: 2,
        messageId: 'user-1',
        content: 'Roll a d88',
        messageScope: 'run',
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 2,
      },
      {
        type: 'interrupt.requested',
        protocolVersion: 'narad/v1',
        eventId: 'evt-3',
        timestamp: '2026-08-05T00:00:01Z',
        sessionId: 'session-1',
        sessionSequence: 3,
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 3,
        interruptId: 'apr-1',
        waitId: 'wait:apr-1',
        kind: 'approval',
        actions: ['approve', 'deny'],
        input: { toolName: 'invoke_agent_roller', toolCallId: 'call-1' },
      },
      {
        type: 'run.paused',
        protocolVersion: 'narad/v1',
        eventId: 'evt-3b',
        timestamp: '2026-08-05T00:00:01Z',
        sessionId: 'session-1',
        sessionSequence: 4,
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 4,
        waitId: 'wait:apr-1',
        reason: 'interrupt',
        resumeMode: 'command',
        interruptId: 'apr-1',
      },
      {
        type: 'interrupt.resolved',
        protocolVersion: 'narad/v1',
        eventId: 'evt-4',
        timestamp: '2026-08-05T00:00:02Z',
        sessionId: 'session-1',
        sessionSequence: 5,
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 5,
        interruptId: 'apr-1',
        waitId: 'wait:apr-1',
        decision: 'approve',
      },
      {
        type: 'run.resumed',
        protocolVersion: 'narad/v1',
        eventId: 'evt-5',
        timestamp: '2026-08-05T00:00:02Z',
        sessionId: 'session-1',
        sessionSequence: 6,
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 6,
        waitId: 'wait:apr-1',
      },
    ]);

    const turns = selectTranscriptTurns(snapshot);
    expect(turns[0]?.activityEntries.some((e) => e.kind === 'approval' && e.status === 'approved')).toBe(
      true,
    );
    expect(turns[0]?.activityEntries.some((e) => e.kind === 'action')).toBe(false);
  });

  it('projects tool activity from tool.call.started after gate-before-start approve', () => {
    const reducer = createNaradReducer({ sessionId: 'session-1', activeProfiles: ALL_PROFILES });
    const snapshot = replay(reducer, [
      {
        type: 'run.started',
        protocolVersion: 'narad/v1',
        eventId: 'evt-1',
        timestamp: '2026-08-05T00:00:00Z',
        sessionId: 'session-1',
        sessionSequence: 1,
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 1,
      },
      {
        type: 'message.user.recorded',
        protocolVersion: 'narad/v1',
        eventId: 'evt-2',
        timestamp: '2026-08-05T00:00:00Z',
        sessionId: 'session-1',
        sessionSequence: 2,
        messageId: 'user-1',
        content: 'Roll a d88',
        messageScope: 'run',
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 2,
      },
      {
        type: 'interrupt.requested',
        protocolVersion: 'narad/v1',
        eventId: 'evt-3',
        timestamp: '2026-08-05T00:00:01Z',
        sessionId: 'session-1',
        sessionSequence: 3,
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 3,
        interruptId: 'apr-1',
        waitId: 'wait:apr-1',
        kind: 'approval',
        actions: ['approve', 'deny'],
        input: { toolName: 'invoke_agent_roller', toolCallId: 'call-1' },
      },
      {
        type: 'run.paused',
        protocolVersion: 'narad/v1',
        eventId: 'evt-3b',
        timestamp: '2026-08-05T00:00:01Z',
        sessionId: 'session-1',
        sessionSequence: 4,
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 4,
        waitId: 'wait:apr-1',
        reason: 'interrupt',
        resumeMode: 'command',
        interruptId: 'apr-1',
      },
      {
        type: 'interrupt.resolved',
        protocolVersion: 'narad/v1',
        eventId: 'evt-4',
        timestamp: '2026-08-05T00:00:02Z',
        sessionId: 'session-1',
        sessionSequence: 5,
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 5,
        interruptId: 'apr-1',
        waitId: 'wait:apr-1',
        decision: 'approve',
      },
      {
        type: 'run.resumed',
        protocolVersion: 'narad/v1',
        eventId: 'evt-5',
        timestamp: '2026-08-05T00:00:02Z',
        sessionId: 'session-1',
        sessionSequence: 6,
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 6,
        waitId: 'wait:apr-1',
      },
      {
        type: 'tool.call.started',
        protocolVersion: 'narad/v1',
        eventId: 'evt-6',
        timestamp: '2026-08-05T00:00:03Z',
        sessionId: 'session-1',
        sessionSequence: 7,
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 7,
        toolCallId: 'call-1',
        toolName: 'invoke_agent_roller',
        executor: 'backend',
        args: {},
      },
    ]);

    const turns = selectTranscriptTurns(snapshot);
    expect(turns[0]?.activityEntries.some((e) => e.kind === 'approval' && e.status === 'approved')).toBe(
      true,
    );
    expect(
      turns[0]?.activityEntries.some(
        (e) => e.kind === 'action' && e.name === 'invoke_agent_roller' && e.status === 'started',
      ),
    ).toBe(true);
    const identities = turns[0]!.activityEntries.map(activityEntryProjectionIdentity);
    expect(new Set(identities).size).toBe(identities.length);
  });

  it('allows composing while execution is active and retains transport locks', () => {
    const reducer = createNaradReducer({ sessionId: 'session-1', activeProfiles: ALL_PROFILES });
    const snapshot = replay(reducer, [{
      type: 'run.started', protocolVersion: 'narad/v1', eventId: 'active-event',
      timestamp: '2026-10-16T00:00:00Z', sessionId: 'session-1', sessionSequence: 1,
      rootRunId: 'active-run', runId: 'active-run', sequence: 1,
    }]);
    expect(selectRunSummary(snapshot).isRunning).toBe(true);
    expect(selectComposerLocked(snapshot, false, 'live')).toBe(false);
    expect(selectComposerLocked(snapshot, true, 'live')).toBe(true);
    expect(selectComposerLocked(snapshot, false, 'attaching')).toBe(false);
    expect(selectComposerLocked({ ...snapshot, sessionGap: true, resyncRequired: true }, false, 'live')).toBe(false);
    expect(selectComposerLocked(snapshot, false, 'unauthorized')).toBe(true);
  });

  it('ignores an unfinished child belonging to a completed root when composing', () => {
    const reducer = createNaradReducer({ sessionId: 'session-1', activeProfiles: ALL_PROFILES });
    const snapshot = replay(reducer, [
      {
        type: 'run.started',
        protocolVersion: 'narad/v1',
        eventId: 'evt-1',
        timestamp: '2026-08-05T00:00:00Z',
        sessionId: 'session-1',
        sessionSequence: 1,
        rootRunId: 'run-old',
        runId: 'run-old',
        sequence: 1,
      },
      {
        type: 'run.started',
        protocolVersion: 'narad/v1',
        eventId: 'evt-2',
        timestamp: '2026-08-05T00:00:01Z',
        sessionId: 'session-1',
        sessionSequence: 2,
        rootRunId: 'run-old',
        runId: 'run-child',
        parentRunId: 'run-old',
        sequence: 2,
      },
      {
        type: 'run.finished',
        protocolVersion: 'narad/v1',
        eventId: 'evt-3',
        timestamp: '2026-08-05T00:00:02Z',
        sessionId: 'session-1',
        sessionSequence: 3,
        rootRunId: 'run-old',
        runId: 'run-old',
        sequence: 3,
      },
    ]);

    expect(snapshot.activeRootRunId).toBeNull();
    expect(selectRunSummary(snapshot).isRunning).toBe(false);
    expect(selectComposerLocked(snapshot, false, 'live')).toBe(false);
  });

  it('binds transcript turns to message root association instead of active root', () => {
    const reducer = createNaradReducer({ sessionId: 'session-1', activeProfiles: ALL_PROFILES });
    const snapshot = replay(reducer, [
      {
        type: 'run.started',
        protocolVersion: 'narad/v1',
        eventId: 'evt-1',
        timestamp: '2026-08-05T00:00:00Z',
        sessionId: 'session-1',
        sessionSequence: 1,
        rootRunId: 'run-a',
        runId: 'run-a',
        sequence: 1,
      },
      {
        type: 'message.user.recorded',
        protocolVersion: 'narad/v1',
        eventId: 'evt-2',
        timestamp: '2026-08-05T00:00:01Z',
        sessionId: 'session-1',
        sessionSequence: 2,
        messageId: 'user-1',
        content: 'First',
        messageScope: 'session',
        rootRunId: 'run-a',
      },
      {
        type: 'run.finished',
        protocolVersion: 'narad/v1',
        eventId: 'evt-3',
        timestamp: '2026-08-05T00:00:02Z',
        sessionId: 'session-1',
        sessionSequence: 3,
        rootRunId: 'run-a',
        runId: 'run-a',
        sequence: 2,
      },
      {
        type: 'run.started',
        protocolVersion: 'narad/v1',
        eventId: 'evt-4',
        timestamp: '2026-08-05T00:00:03Z',
        sessionId: 'session-1',
        sessionSequence: 4,
        rootRunId: 'run-b',
        runId: 'run-b',
        sequence: 1,
      },
      {
        type: 'message.user.recorded',
        protocolVersion: 'narad/v1',
        eventId: 'evt-5',
        timestamp: '2026-08-05T00:00:04Z',
        sessionId: 'session-1',
        sessionSequence: 5,
        messageId: 'user-2',
        content: 'Second',
        messageScope: 'session',
        rootRunId: 'run-b',
      },
    ]);

    const turns = selectTranscriptTurns(snapshot);
    expect(turns[0]?.rootRunId).toBe('run-a');
    expect(turns[1]?.rootRunId).toBe('run-b');
  });

  it('surfaces aggregated Narad terminal usage on transcript turns', () => {
    const reducer = createNaradReducer({ sessionId: 'session-1', activeProfiles: ALL_PROFILES });
    const snapshot = replay(reducer, [
      {
        type: 'run.started',
        protocolVersion: 'narad/v1',
        eventId: 'evt-run',
        timestamp: '2026-08-05T00:00:00Z',
        sessionId: 'session-1',
        sessionSequence: 1,
        rootRunId: 'run-a',
        runId: 'run-a',
        sequence: 1,
      },
      {
        type: 'message.user.recorded',
        protocolVersion: 'narad/v1',
        eventId: 'evt-user',
        timestamp: '2026-08-05T00:00:00Z',
        sessionId: 'session-1',
        sessionSequence: 2,
        messageId: 'user-1',
        content: 'Hi',
        messageScope: 'session',
        rootRunId: 'run-a',
        runId: 'run-a',
        sequence: 2,
      },
      {
        type: 'message.text.started',
        protocolVersion: 'narad/v1',
        eventId: 'evt-msg',
        timestamp: '2026-08-05T00:00:01Z',
        sessionId: 'session-1',
        sessionSequence: 3,
        messageId: 'asst-1',
        role: 'assistant',
        messageScope: 'run',
        rootRunId: 'run-a',
        runId: 'run-a',
        sequence: 3,
      },
      {
        type: 'message.text.completed',
        protocolVersion: 'narad/v1',
        eventId: 'evt-msg-done',
        timestamp: '2026-08-05T00:00:02Z',
        sessionId: 'session-1',
        sessionSequence: 4,
        messageId: 'asst-1',
        rootRunId: 'run-a',
        runId: 'run-a',
        sequence: 4,
      },
      {
        type: 'run.finished',
        protocolVersion: 'narad/v1',
        eventId: 'evt-done',
        timestamp: '2026-08-05T00:00:02Z',
        sessionId: 'session-1',
        sessionSequence: 5,
        rootRunId: 'run-a',
        runId: 'run-a',
        sequence: 5,
        usage: { promptTokens: 10, completionTokens: 20, totalTokens: 30, elapsedMs: 900 },
      },
    ]);

    expect(selectTranscriptTurns(snapshot)[0]?.usage).toEqual({
      promptTokens: 10,
      completionTokens: 20,
      totalTokens: 30,
      elapsedMs: 900,
    });
  });

  it('sums agent.turn tokens and uses root wall-clock elapsed, not last-invoke usage.elapsedMs', () => {
    const reducer = createNaradReducer({ sessionId: 'session-1', activeProfiles: ALL_PROFILES });
    const snapshot = replay(reducer, [
      {
        type: 'run.started',
        protocolVersion: 'narad/v1',
        eventId: 'evt-run',
        timestamp: '2026-09-12T01:45:28.480Z',
        sessionId: 'session-1',
        sessionSequence: 1,
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 1,
      },
      {
        type: 'message.user.recorded',
        protocolVersion: 'narad/v1',
        eventId: 'evt-user',
        timestamp: '2026-09-12T01:45:28.480Z',
        sessionId: 'session-1',
        sessionSequence: 2,
        messageId: 'user-1',
        content: 'roll a d55',
        messageScope: 'session',
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 2,
      },
      {
        type: 'progress.recorded',
        protocolVersion: 'narad/v1',
        eventId: 'evt-turn1',
        timestamp: '2026-09-12T01:45:36.274Z',
        sessionId: 'session-1',
        sessionSequence: 3,
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 3,
        progressId: 'agent-turn:run-1:1',
        category: 'agent.turn',
        label: 'Agent turn 1',
        status: 'completed',
        privacy: 'public',
        usage: { promptTokens: 1228, completionTokens: 76, totalTokens: 1304 },
      },
      {
        type: 'run.started',
        protocolVersion: 'narad/v1',
        eventId: 'evt-child',
        timestamp: '2026-09-12T01:45:41.405Z',
        sessionId: 'session-1',
        sessionSequence: 4,
        rootRunId: 'run-1',
        runId: 'run-child',
        parentRunId: 'run-1',
        sequence: 1,
      },
      {
        type: 'progress.recorded',
        protocolVersion: 'narad/v1',
        eventId: 'evt-nested',
        timestamp: '2026-09-12T01:45:43.464Z',
        sessionId: 'session-1',
        sessionSequence: 5,
        rootRunId: 'run-1',
        runId: 'run-child',
        sequence: 2,
        progressId: 'agent-turn:run-child:1',
        category: 'agent.turn',
        label: 'Nested agent turn 1',
        status: 'completed',
        privacy: 'public',
        usage: { promptTokens: 744, completionTokens: 19, totalTokens: 763 },
      },
      {
        type: 'progress.recorded',
        protocolVersion: 'narad/v1',
        eventId: 'evt-turn2',
        timestamp: '2026-09-12T01:45:56.891Z',
        sessionId: 'session-1',
        sessionSequence: 6,
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 4,
        progressId: 'agent-turn:run-1:2',
        category: 'agent.turn',
        label: 'Agent turn 2',
        status: 'completed',
        privacy: 'public',
        usage: { promptTokens: 1396, completionTokens: 44, totalTokens: 1440 },
      },
      {
        type: 'run.finished',
        protocolVersion: 'narad/v1',
        eventId: 'evt-done',
        timestamp: '2026-09-12T01:45:56.930Z',
        sessionId: 'session-1',
        sessionSequence: 7,
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 5,
        usage: { promptTokens: 1396, completionTokens: 44, totalTokens: 1440, elapsedMs: 4147 },
      },
    ]);

    expect(selectTranscriptTurns(snapshot)[0]?.usage).toEqual({
      promptTokens: 3368,
      completionTokens: 139,
      totalTokens: 3507,
      elapsedMs: 28450,
    });
  });

  it('hides finished execution graphs after reload (no active root chrome)', () => {
    const reducer = createNaradReducer({ sessionId: 'session-1', activeProfiles: ALL_PROFILES });
    const snapshot = replay(reducer, [
      {
        type: 'run.started',
        protocolVersion: 'narad/v1',
        eventId: 'evt-run',
        timestamp: '2026-08-05T00:00:00Z',
        sessionId: 'session-1',
        sessionSequence: 1,
        rootRunId: 'run-done',
        runId: 'run-done',
        sequence: 1,
      },
      {
        type: 'graph.snapshot',
        protocolVersion: 'narad/v1',
        timestamp: '2026-08-05T00:00:01Z',
        sessionId: 'session-1',
        scope: 'run',
        rootRunId: 'run-done',
        graphSchemaVersion: 'narad.graph/v1',
        revision: 2,
        sourceRunCursor: { rootRunId: 'run-done', sequence: 1 },
        rootNodeId: 'node-root',
        nodes: [
          { id: 'node-root', kind: 'run', status: 'completed', label: 'Root' },
          { id: 'node-t1', kind: 'tool', status: 'completed', label: 'search' },
        ],
        edges: [],
      },
      {
        type: 'run.finished',
        protocolVersion: 'narad/v1',
        eventId: 'evt-done',
        timestamp: '2026-08-05T00:00:02Z',
        sessionId: 'session-1',
        sessionSequence: 2,
        rootRunId: 'run-done',
        runId: 'run-done',
        sequence: 2,
      },
    ]);

    expect(snapshot.activeRootRunId).toBeNull();
    expect(selectActiveRootGraph(snapshot).graph).toBeNull();
  });

  it('keeps turn activity after messages.snapshot hydrate (reload parity)', () => {
    const reducer = createNaradReducer({ sessionId: 'session-1', activeProfiles: ALL_PROFILES });
    const snapshot = replay(reducer, [
      {
        type: 'run.started',
        protocolVersion: 'narad/v1',
        eventId: 'evt-run',
        timestamp: '2026-08-05T00:00:00Z',
        sessionId: 'session-1',
        sessionSequence: 1,
        rootRunId: 'run-a',
        runId: 'run-a',
        sequence: 1,
      },
      {
        type: 'message.user.recorded',
        protocolVersion: 'narad/v1',
        eventId: 'evt-user',
        timestamp: '2026-08-05T00:00:00Z',
        sessionId: 'session-1',
        sessionSequence: 2,
        messageId: 'user-1',
        content: 'roll a d45',
        messageScope: 'run',
        rootRunId: 'run-a',
        runId: 'run-a',
        sequence: 2,
      },
      {
        type: 'tool.call.started',
        protocolVersion: 'narad/v1',
        eventId: 'evt-tool-start',
        timestamp: '2026-08-05T00:00:01Z',
        sessionId: 'session-1',
        sessionSequence: 3,
        rootRunId: 'run-a',
        runId: 'run-a',
        sequence: 3,
        toolCallId: 'tool-1',
        toolName: 'invoke_agent_roller',
        executor: 'backend',
        args: { sides: 45 },
      },
      {
        type: 'tool.call.finished',
        protocolVersion: 'narad/v1',
        eventId: 'evt-tool-done',
        timestamp: '2026-08-05T00:00:02Z',
        sessionId: 'session-1',
        sessionSequence: 4,
        rootRunId: 'run-a',
        runId: 'run-a',
        sequence: 4,
        toolCallId: 'tool-1',
        toolName: 'invoke_agent_roller',
        outcome: 'success',
        response: { result: 42 },
      },
      {
        type: 'message.text.started',
        protocolVersion: 'narad/v1',
        eventId: 'evt-msg',
        timestamp: '2026-08-05T00:00:02Z',
        sessionId: 'session-1',
        sessionSequence: 5,
        messageId: 'asst-1',
        role: 'assistant',
        messageScope: 'run',
        rootRunId: 'run-a',
        runId: 'run-a',
        sequence: 5,
      },
      {
        type: 'message.text.delta',
        protocolVersion: 'narad/v1',
        eventId: 'evt-msg-delta',
        timestamp: '2026-08-05T00:00:02Z',
        sessionId: 'session-1',
        sessionSequence: 6,
        messageId: 'asst-1',
        delta: 'The result is 42.',
        messageScope: 'run',
        rootRunId: 'run-a',
        runId: 'run-a',
        sequence: 6,
      },
      {
        type: 'message.text.completed',
        protocolVersion: 'narad/v1',
        eventId: 'evt-msg-done',
        timestamp: '2026-08-05T00:00:03Z',
        sessionId: 'session-1',
        sessionSequence: 7,
        messageId: 'asst-1',
        rootRunId: 'run-a',
        runId: 'run-a',
        sequence: 7,
      },
      {
        type: 'run.finished',
        protocolVersion: 'narad/v1',
        eventId: 'evt-done',
        timestamp: '2026-08-05T00:00:03Z',
        sessionId: 'session-1',
        sessionSequence: 8,
        rootRunId: 'run-a',
        runId: 'run-a',
        sequence: 8,
      },
      {
        type: 'messages.snapshot',
        protocolVersion: 'narad/v1',
        timestamp: '2026-08-05T00:00:04Z',
        snapshotId: 'snap-1',
        sessionId: 'session-1',
        coversThroughSessionSequence: 8,
        messages: [
          {
            messageId: 'user-1',
            role: 'user',
            content: 'roll a d45',
            rootRunId: 'run-a',
            runId: 'run-a',
            timestamp: '2026-08-05T00:00:00Z',
            sessionSequence: 2,
            completed: true,
          },
          {
            messageId: 'asst-1',
            role: 'assistant',
            content: 'The result is 42.',
            rootRunId: 'run-a',
            runId: 'run-a',
            timestamp: '2026-08-05T00:00:03Z',
            sessionSequence: 7,
            completed: true,
          },
        ],
      },
    ]);

    const turns = selectTranscriptTurns(snapshot);
    expect(turns).toHaveLength(1);
    expect(turns[0]?.rootRunId).toBe('run-a');
    expect(turns[0]?.activityEntries.some((entry) => entry.name === 'invoke_agent_roller')).toBe(true);
    expect(selectActiveRootGraph(snapshot).graph).toBeNull();
  });

  it('does not treat duplicate authority events as transcript changes', () => {
    const reducer = createNaradReducer({ sessionId: 'session-1', activeProfiles: ALL_PROFILES });
    const started = {
      type: 'message.user.recorded',
      protocolVersion: 'narad/v1',
      eventId: 'evt-1',
      timestamp: '2026-08-05T00:00:00Z',
      sessionId: 'session-1',
      sessionSequence: 1,
      messageId: 'user-1',
      content: 'Hi',
    };
    replay(reducer, [started]);
    const first = selectTranscriptTurns(reducer.getSnapshot());
    reducer.apply(started);
    const second = selectTranscriptTurns(reducer.getSnapshot());
    expect(first).toEqual(second);
  });

  it('renders a second user prompt as its own turn', () => {
    const reducer = createNaradReducer({ sessionId: 'session-1', activeProfiles: ALL_PROFILES });
    const snapshot = replay(reducer, [
      {
        type: 'run.started',
        protocolVersion: 'narad/v1',
        eventId: 'evt-1',
        timestamp: '2026-08-05T00:00:00Z',
        sessionId: 'session-1',
        sessionSequence: 1,
        rootRunId: 'run-a',
        runId: 'run-a',
        sequence: 1,
      },
      {
        type: 'message.user.recorded',
        protocolVersion: 'narad/v1',
        eventId: 'evt-2',
        timestamp: '2026-08-05T00:00:00Z',
        sessionId: 'session-1',
        sessionSequence: 2,
        messageId: 'user-1',
        content: 'roll a 55-sided die',
        messageScope: 'run',
        rootRunId: 'run-a',
        runId: 'run-a',
        sequence: 2,
      },
      {
        type: 'run.finished',
        protocolVersion: 'narad/v1',
        eventId: 'evt-3',
        timestamp: '2026-08-05T00:00:05Z',
        sessionId: 'session-1',
        sessionSequence: 3,
        rootRunId: 'run-a',
        runId: 'run-a',
        sequence: 3,
      },
      {
        type: 'run.started',
        protocolVersion: 'narad/v1',
        eventId: 'evt-4',
        timestamp: '2026-08-05T00:00:10Z',
        sessionId: 'session-1',
        sessionSequence: 4,
        rootRunId: 'run-b',
        runId: 'run-b',
        sequence: 1,
      },
      {
        type: 'message.user.recorded',
        protocolVersion: 'narad/v1',
        eventId: 'evt-5',
        timestamp: '2026-08-05T00:00:10Z',
        sessionId: 'session-1',
        sessionSequence: 5,
        messageId: 'user-2',
        content: 'roll a d222',
        messageScope: 'run',
        rootRunId: 'run-b',
        runId: 'run-b',
        sequence: 2,
      },
    ]);

    const turns = selectTranscriptTurns(snapshot);
    expect(turns).toHaveLength(2);
    expect(turns[0]?.user?.content).toBe('roll a 55-sided die');
    expect(turns[1]?.user?.content).toBe('roll a d222');
  });

  it('projects attachment ids onto user turns', () => {
    const reducer = createNaradReducer({ sessionId: 'session-1', activeProfiles: ALL_PROFILES });
    const snapshot = replay(reducer, [
      {
        type: 'message.user.recorded',
        protocolVersion: 'narad/v1',
        eventId: 'evt-1',
        timestamp: '2026-08-05T00:00:00Z',
        sessionId: 'session-1',
        sessionSequence: 1,
        messageId: 'user-1',
        content: '',
        attachmentIds: ['asset-1'],
        messageScope: 'run',
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 1,
      },
    ]);

    expect(selectTranscriptTurns(snapshot)[0]?.user).toEqual({
      id: 'user-1',
      content: '',
      timestamp: '2026-08-05T00:00:00Z',
      attachmentIds: ['asset-1'],
    });
  });

  it('keeps pre-approval reasoning on the turn after a new delivery-attempt assistant message', () => {
    const reducer = createNaradReducer({ sessionId: 'session-1', activeProfiles: ALL_PROFILES });
    const snapshot = replay(reducer, [
      {
        type: 'message.user.recorded',
        protocolVersion: 'narad/v1',
        eventId: 'evt-1',
        timestamp: '2026-08-05T00:00:00Z',
        sessionId: 'session-1',
        sessionSequence: 1,
        messageId: 'user-1',
        content: 'roll a d567',
        messageScope: 'run',
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 1,
      },
      {
        type: 'reasoning.started',
        protocolVersion: 'narad/v1',
        eventId: 'evt-2',
        timestamp: '2026-08-05T00:00:01Z',
        sessionId: 'session-1',
        sessionSequence: 2,
        reasoningId: 'reasoning:assistant:run-1:attempt-1',
        messageId: 'assistant:run-1:attempt-1',
      },
      {
        type: 'reasoning.delta',
        protocolVersion: 'narad/v1',
        eventId: 'evt-3',
        timestamp: '2026-08-05T00:00:02Z',
        sessionId: 'session-1',
        sessionSequence: 3,
        reasoningId: 'reasoning:assistant:run-1:attempt-1',
        messageId: 'assistant:run-1:attempt-1',
        delta: 'I will invoke the roller tool.',
      },
      {
        type: 'reasoning.completed',
        protocolVersion: 'narad/v1',
        eventId: 'evt-4',
        timestamp: '2026-08-05T00:00:03Z',
        sessionId: 'session-1',
        sessionSequence: 4,
        reasoningId: 'reasoning:assistant:run-1:attempt-1',
        messageId: 'assistant:run-1:attempt-1',
      },
      {
        type: 'message.text.started',
        protocolVersion: 'narad/v1',
        eventId: 'evt-5',
        timestamp: '2026-08-05T00:00:04Z',
        sessionId: 'session-1',
        sessionSequence: 5,
        messageId: 'assistant:run-1:attempt-1',
        role: 'assistant',
        messageScope: 'run',
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 2,
      },
      {
        type: 'message.text.completed',
        protocolVersion: 'narad/v1',
        eventId: 'evt-6',
        timestamp: '2026-08-05T00:00:05Z',
        sessionId: 'session-1',
        sessionSequence: 6,
        messageId: 'assistant:run-1:attempt-1',
      },
      // Post-approval resume: new assistant messageId carries final text only.
      {
        type: 'message.text.started',
        protocolVersion: 'narad/v1',
        eventId: 'evt-7',
        timestamp: '2026-08-05T00:00:06Z',
        sessionId: 'session-1',
        sessionSequence: 7,
        messageId: 'assistant:run-1:attempt-2',
        role: 'assistant',
        messageScope: 'run',
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 3,
      },
      {
        type: 'message.text.delta',
        protocolVersion: 'narad/v1',
        eventId: 'evt-8',
        timestamp: '2026-08-05T00:00:07Z',
        sessionId: 'session-1',
        sessionSequence: 8,
        messageId: 'assistant:run-1:attempt-2',
        delta: 'The result of the d567 roll is 353.',
        messageScope: 'run',
      },
      {
        type: 'message.text.completed',
        protocolVersion: 'narad/v1',
        eventId: 'evt-9',
        timestamp: '2026-08-05T00:00:08Z',
        sessionId: 'session-1',
        sessionSequence: 9,
        messageId: 'assistant:run-1:attempt-2',
      },
    ]);

    const turn = selectTranscriptTurns(snapshot)[0];
    expect(turn?.assistant?.content).toBe('The result of the d567 roll is 353.');
    expect(turn?.assistant?.thinking).toBe('I will invoke the roller tool.');
  });

  it('projects agent.turn progress as ledger turn rows including nested runs', () => {
    const reducer = createNaradReducer({ sessionId: 'session-1', activeProfiles: ALL_PROFILES });
    const snapshot = replay(reducer, [
      {
        type: 'run.started',
        protocolVersion: 'narad/v1',
        eventId: 'evt-1',
        timestamp: '2026-08-05T00:00:00Z',
        sessionId: 'session-1',
        sessionSequence: 1,
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 1,
      },
      {
        type: 'message.user.recorded',
        protocolVersion: 'narad/v1',
        eventId: 'evt-2',
        timestamp: '2026-08-05T00:00:00Z',
        sessionId: 'session-1',
        sessionSequence: 2,
        messageId: 'user-1',
        content: 'do three things',
        messageScope: 'run',
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 2,
      },
      {
        type: 'progress.recorded',
        protocolVersion: 'narad/v1',
        eventId: 'evt-3',
        timestamp: '2026-08-05T00:00:01Z',
        sessionId: 'session-1',
        sessionSequence: 3,
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 3,
        progressId: 'agent-turn:run-1:1',
        category: 'agent.turn',
        label: 'Agent turn 1',
        status: 'active',
        privacy: 'public',
      },
      {
        type: 'progress.recorded',
        protocolVersion: 'narad/v1',
        eventId: 'evt-4',
        timestamp: '2026-08-05T00:00:02Z',
        sessionId: 'session-1',
        sessionSequence: 4,
        rootRunId: 'run-1',
        runId: 'run-1',
        sequence: 4,
        progressId: 'agent-turn:run-1:1',
        category: 'agent.turn',
        label: 'Agent turn 1',
        status: 'completed',
        privacy: 'public',
        detail: '1 tool call',
      },
      {
        type: 'run.started',
        protocolVersion: 'narad/v1',
        eventId: 'evt-5',
        timestamp: '2026-08-05T00:00:03Z',
        sessionId: 'session-1',
        sessionSequence: 5,
        rootRunId: 'run-1',
        runId: 'run-child',
        parentRunId: 'run-1',
        sequence: 1,
      },
      {
        type: 'progress.recorded',
        protocolVersion: 'narad/v1',
        eventId: 'evt-6',
        timestamp: '2026-08-05T00:00:04Z',
        sessionId: 'session-1',
        sessionSequence: 6,
        rootRunId: 'run-1',
        runId: 'run-child',
        sequence: 2,
        progressId: 'agent-turn:run-child:1',
        category: 'agent.turn',
        label: 'Nested agent turn 1',
        status: 'completed',
        privacy: 'public',
        detail: '1 tool call',
      },
    ]);

    const turns = selectTranscriptTurns(snapshot);
    expect(turns[0]?.activityEntries).toEqual([
      expect.objectContaining({
        kind: 'turn',
        id: 'agent-turn:run-1:1',
        name: 'Agent turn 1',
        status: 'completed',
        startedAt: '2026-08-05T00:00:01Z',
        completedAt: '2026-08-05T00:00:02Z',
        resultJson: '1 tool call',
      }),
      expect.objectContaining({
        kind: 'turn',
        id: 'agent-turn:run-child:1',
        name: 'Nested agent turn 1',
      }),
    ]);
  });
});
