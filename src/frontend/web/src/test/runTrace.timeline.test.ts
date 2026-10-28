/**
 * @vitest-environment node
 */
import { describe, expect, it } from 'vitest';
import {
  buildTraceTimeline,
  resolveSpanInterval,
} from '@/features/chat/api/runTraceTimeline';
import type { RunTraceSpan } from '@/features/chat/api/runTrace';

describe('run trace timeline layout', () => {
  it('resolves intervals from start+latency and end+latency', () => {
    expect(
      resolveSpanInterval({
        id: 'a',
        name: 'a',
        type: 'tool',
        status: 'completed',
        startedAtMs: 1000,
        latencyMs: 250,
      }),
    ).toEqual({ startMs: 1000, endMs: 1250 });

    expect(
      resolveSpanInterval({
        id: 'b',
        name: 'b',
        type: 'run.nested',
        status: 'completed',
        endedAtMs: 5000,
        latencyMs: 1500,
      }),
    ).toEqual({ startMs: 3500, endMs: 5000 });
  });

  it('builds waterflow lanes ordered by start with shared origin', () => {
    const spans: RunTraceSpan[] = [
      {
        id: 'root',
        name: 'Root run',
        type: 'run',
        status: 'completed',
        startedAtMs: 0,
        endedAtMs: 10_000,
        latencyMs: 10_000,
      },
      {
        id: 'tool-late',
        parentSpanId: 'root',
        name: 'late',
        type: 'tool',
        status: 'completed',
        startedAtMs: 5_000,
        endedAtMs: 7_000,
        latencyMs: 2_000,
      },
      {
        id: 'tool-early',
        parentSpanId: 'root',
        name: 'early',
        type: 'tool',
        status: 'completed',
        startedAtMs: 1_000,
        endedAtMs: 1_500,
        latencyMs: 500,
      },
      {
        id: 'no-time',
        parentSpanId: 'root',
        name: 'ghost',
        type: 'progress',
        status: 'completed',
      },
    ];

    const timeline = buildTraceTimeline(spans);
    expect(timeline.originMs).toBe(0);
    expect(timeline.totalMs).toBe(10_000);
    expect(timeline.lanes.map((lane) => lane.span.id)).toEqual(['root', 'tool-early', 'tool-late']);
    expect(timeline.lanes[1]?.offsetMs).toBe(1_000);
    expect(timeline.lanes[1]?.durationMs).toBe(500);
    expect(timeline.untimed.map((span) => span.id)).toEqual(['no-time']);
  });
});
