/**
 * @vitest-environment node
 */
import { describe, expect, it } from 'vitest';
import { projectAuthorityTrace } from '@/features/chat/api/runTrace';

describe('projectAuthorityTrace', () => {
  it('projects tool durations and terminal token usage from public authority events', () => {
    const { spans, summary } = projectAuthorityTrace(
      [
        {
          type: 'run.started',
          eventId: 'e1',
          timestamp: '2026-08-05T00:00:00.000Z',
          rootRunId: 'run-1',
          runId: 'run-1',
        },
        {
          type: 'tool.call.started',
          eventId: 'e2',
          timestamp: '2026-08-05T00:00:01.000Z',
          rootRunId: 'run-1',
          runId: 'run-1',
          toolCallId: 'tool-1',
          toolName: 'search',
        },
        {
          type: 'tool.call.finished',
          eventId: 'e3',
          timestamp: '2026-08-05T00:00:01.250Z',
          rootRunId: 'run-1',
          runId: 'run-1',
          toolCallId: 'tool-1',
          toolName: 'search',
          outcome: 'success',
        },
        {
          type: 'run.finished',
          eventId: 'e4',
          timestamp: '2026-08-05T00:00:02.000Z',
          rootRunId: 'run-1',
          runId: 'run-1',
          usage: { promptTokens: 40, completionTokens: 12, totalTokens: 52, elapsedMs: 2000 },
        },
        {
          type: 'run.finished',
          eventId: 'e-other',
          timestamp: '2026-08-05T00:00:03.000Z',
          rootRunId: 'run-other',
          runId: 'run-other',
          usage: { promptTokens: 999, completionTokens: 999 },
        },
      ],
      'run-1',
    );

    expect(summary).toEqual({
      promptTokens: 40,
      completionTokens: 12,
      totalTokens: 52,
      elapsedMs: 2000,
      toolCount: 1,
    });
    const tool = spans.find((span) => span.id === 'tool-1');
    expect(tool?.latencyMs).toBe(250);
    expect(tool?.status).toBe('completed');
    expect(spans.some((span) => span.type === 'run.terminal')).toBe(true);
    expect(spans.every((span) => !String(span.name).includes('999'))).toBe(true);
  });

  it('places nested child usage on a wall-clock interval for timeline layout', () => {
    const { spans } = projectAuthorityTrace(
      [
        {
          type: 'run.started',
          eventId: 'e1',
          timestamp: '2026-08-05T00:00:00.000Z',
          rootRunId: 'run-1',
          runId: 'run-1',
        },
        {
          type: 'run.finished',
          eventId: 'e-child',
          timestamp: '2026-08-05T00:00:05.000Z',
          rootRunId: 'run-1',
          runId: 'child-abc-def',
          usage: { promptTokens: 10, completionTokens: 4, elapsedMs: 1500 },
        },
        {
          type: 'run.finished',
          eventId: 'e-root',
          timestamp: '2026-08-05T00:00:06.000Z',
          rootRunId: 'run-1',
          runId: 'run-1',
          usage: { promptTokens: 20, completionTokens: 8, elapsedMs: 6000 },
        },
      ],
      'run-1',
    );

    const nested = spans.find((span) => span.type === 'run.nested');
    expect(nested?.latencyMs).toBe(1500);
    expect(nested?.endedAtMs).toBe(Date.parse('2026-08-05T00:00:05.000Z'));
    expect(nested?.startedAtMs).toBe(Date.parse('2026-08-05T00:00:03.500Z'));
  });

  it('flags early root run.finished when tools continue and prefers wall-clock bars', () => {
    const { spans, summary } = projectAuthorityTrace(
      [
        {
          type: 'run.started',
          eventId: 'e1',
          timestamp: '2026-08-05T00:00:00.000Z',
          rootRunId: 'run-1',
          runId: 'run-1',
        },
        {
          type: 'run.finished',
          eventId: 'e-early',
          timestamp: '2026-08-05T00:01:00.000Z',
          rootRunId: 'run-1',
          runId: 'run-1',
          usage: { promptTokens: 5, completionTokens: 1, elapsedMs: 60_000 },
        },
        {
          type: 'tool.call.started',
          eventId: 'e-tool',
          timestamp: '2026-08-05T00:01:10.000Z',
          rootRunId: 'run-1',
          runId: 'run-1',
          toolCallId: 'tool-1',
          toolName: 'invoke_agent_roller',
        },
        {
          type: 'tool.call.finished',
          eventId: 'e-tool-done',
          timestamp: '2026-08-05T00:01:50.000Z',
          rootRunId: 'run-1',
          runId: 'run-1',
          toolCallId: 'tool-1',
          toolName: 'invoke_agent_roller',
          outcome: 'success',
        },
        {
          type: 'run.finished',
          eventId: 'e-final',
          timestamp: '2026-08-05T00:02:00.000Z',
          rootRunId: 'run-1',
          runId: 'run-1',
          usage: { promptTokens: 40, completionTokens: 12, elapsedMs: 120_000 },
        },
      ],
      'run-1',
    );

    const terminals = spans.filter((span) => span.type === 'run.terminal');
    expect(terminals).toHaveLength(2);
    expect(terminals[0]?.name).toContain('Early run.finished');
    expect(terminals[0]?.status).toBe('failed');
    expect(terminals[1]?.name).toBe('Run finished');
    expect(terminals[1]?.latencyMs).toBe(120_000);
    expect(summary?.elapsedMs).toBe(120_000);
    // Last root usage only (not 60k+120k).
    expect(summary?.promptTokens).toBe(40);
    expect(summary?.completionTokens).toBe(12);
  });

  it('dedupes journal events by eventId', () => {
    const { spans } = projectAuthorityTrace(
      [
        {
          type: 'run.started',
          eventId: 'e1',
          timestamp: '2026-08-05T00:00:00.000Z',
          rootRunId: 'run-1',
          runId: 'run-1',
        },
        {
          type: 'run.finished',
          eventId: 'same',
          timestamp: '2026-08-05T00:00:02.000Z',
          rootRunId: 'run-1',
          runId: 'run-1',
          usage: { promptTokens: 1, completionTokens: 1, elapsedMs: 2000 },
        },
        {
          type: 'run.finished',
          eventId: 'same',
          timestamp: '2026-08-05T00:00:02.000Z',
          rootRunId: 'run-1',
          runId: 'run-1',
          usage: { promptTokens: 1, completionTokens: 1, elapsedMs: 2000 },
        },
      ],
      'run-1',
    );
    expect(spans.filter((span) => span.type === 'run.terminal')).toHaveLength(1);
  });

  it('sums agent.turn progress usage for the header and labels each turn', () => {
    const { spans, summary } = projectAuthorityTrace(
      [
        {
          type: 'run.started',
          eventId: 'e1',
          timestamp: '2026-09-12T00:02:20.000Z',
          rootRunId: 'run-1',
          runId: 'run-1',
        },
        {
          type: 'progress.recorded',
          eventId: 'e-turn1-start',
          timestamp: '2026-09-12T00:02:21.000Z',
          rootRunId: 'run-1',
          runId: 'run-1',
          progressId: 'agent-turn:run-1:1',
          category: 'agent.turn',
          label: 'Agent turn 1',
          status: 'active',
          privacy: 'public',
        },
        {
          type: 'progress.recorded',
          eventId: 'e-turn1-done',
          timestamp: '2026-09-12T00:02:21.500Z',
          rootRunId: 'run-1',
          runId: 'run-1',
          progressId: 'agent-turn:run-1:1',
          category: 'agent.turn',
          label: 'Agent turn 1',
          status: 'completed',
          privacy: 'public',
          usage: { promptTokens: 1230, completionTokens: 70, totalTokens: 1300 },
        },
        {
          type: 'progress.recorded',
          eventId: 'e-nested',
          timestamp: '2026-09-12T00:02:32.000Z',
          rootRunId: 'run-1',
          runId: 'child-1',
          progressId: 'agent-turn:child-1:1',
          category: 'agent.turn',
          label: 'Nested agent turn 1',
          status: 'completed',
          privacy: 'public',
          usage: { promptTokens: 746, completionTokens: 19, totalTokens: 765 },
        },
        {
          type: 'run.finished',
          eventId: 'e-finish',
          timestamp: '2026-09-12T00:02:47.000Z',
          rootRunId: 'run-1',
          runId: 'run-1',
          usage: { promptTokens: 1386, completionTokens: 62, totalTokens: 1448, elapsedMs: 27000 },
        },
      ],
      'run-1',
    );

    expect(summary?.promptTokens).toBe(1976);
    expect(summary?.completionTokens).toBe(89);
    expect(summary?.totalTokens).toBe(2065);
    const parentTurn = spans.find((span) => span.id === 'progress:agent-turn:run-1:1');
    expect(parentTurn?.name).toContain('in 1230 / out 70');
    expect(parentTurn?.status).toBe('completed');
    const nestedTurn = spans.find((span) => span.id === 'progress:agent-turn:child-1:1');
    expect(nestedTurn?.name).toContain('in 746 / out 19');
  });

  it('keeps HITL continuation turns as separate spans when progress ids stay unique', () => {
    const { spans, summary } = projectAuthorityTrace(
      [
        {
          type: 'run.started',
          eventId: 'e1',
          timestamp: '2026-09-12T00:50:26.000Z',
          rootRunId: 'run-1',
          runId: 'run-1',
        },
        {
          type: 'progress.recorded',
          eventId: 'e-turn1',
          timestamp: '2026-09-12T00:50:26.300Z',
          rootRunId: 'run-1',
          runId: 'run-1',
          progressId: 'agent-turn:run-1:1',
          category: 'agent.turn',
          label: 'Agent turn 1',
          status: 'completed',
          privacy: 'public',
          usage: { promptTokens: 1229, completionTokens: 65, totalTokens: 1294 },
        },
        {
          type: 'progress.recorded',
          eventId: 'e-turn2',
          timestamp: '2026-09-12T00:50:52.000Z',
          rootRunId: 'run-1',
          runId: 'run-1',
          progressId: 'agent-turn:run-1:2',
          category: 'agent.turn',
          label: 'Agent turn 2',
          status: 'completed',
          privacy: 'public',
          usage: { promptTokens: 1375, completionTokens: 30, totalTokens: 1405 },
        },
      ],
      'run-1',
    );

    expect(summary?.promptTokens).toBe(2604);
    expect(summary?.completionTokens).toBe(95);
    expect(spans.filter((span) => span.id.startsWith('progress:agent-turn:run-1:'))).toHaveLength(2);
  });
});

