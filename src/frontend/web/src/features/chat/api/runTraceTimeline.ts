import type { RunTraceSpan } from '../api/runTrace';

export type TraceTimelineLane = {
  span: RunTraceSpan;
  depth: number;
  startMs: number;
  endMs: number;
  offsetMs: number;
  durationMs: number;
};

function depthOf(span: RunTraceSpan, byId: Map<string, RunTraceSpan>): number {
  let depth = 0;
  let parentId = span.parentSpanId;
  const seen = new Set<string>();
  while (parentId && !seen.has(parentId)) {
    seen.add(parentId);
    depth += 1;
    parentId = byId.get(parentId)?.parentSpanId ?? null;
  }
  return depth;
}

/** Wall-clock interval used for waterflow placement. */
export function resolveSpanInterval(span: RunTraceSpan): { startMs: number; endMs: number } | null {
  if (span.startedAtMs != null && Number.isFinite(span.startedAtMs)) {
    const startMs = span.startedAtMs;
    if (span.endedAtMs != null && Number.isFinite(span.endedAtMs) && span.endedAtMs >= startMs) {
      return { startMs, endMs: span.endedAtMs };
    }
    if (span.latencyMs != null && Number.isFinite(span.latencyMs) && span.latencyMs >= 0) {
      return { startMs, endMs: startMs + span.latencyMs };
    }
    return { startMs, endMs: startMs };
  }
  if (
    span.endedAtMs != null &&
    Number.isFinite(span.endedAtMs) &&
    span.latencyMs != null &&
    Number.isFinite(span.latencyMs) &&
    span.latencyMs >= 0
  ) {
    return { startMs: span.endedAtMs - span.latencyMs, endMs: span.endedAtMs };
  }
  return null;
}

export function buildTraceTimeline(spans: readonly RunTraceSpan[]): {
  lanes: TraceTimelineLane[];
  originMs: number;
  totalMs: number;
  untimed: RunTraceSpan[];
} {
  const byId = new Map(spans.map((span) => [span.id, span]));
  const timed: Array<{ span: RunTraceSpan; startMs: number; endMs: number; depth: number }> = [];
  const untimed: RunTraceSpan[] = [];

  for (const span of spans) {
    const interval = resolveSpanInterval(span);
    if (!interval) {
      untimed.push(span);
      continue;
    }
    timed.push({ span, ...interval, depth: depthOf(span, byId) });
  }

  if (timed.length === 0) {
    return { lanes: [], originMs: 0, totalMs: 1, untimed };
  }

  const originMs = Math.min(...timed.map((item) => item.startMs));
  const horizonMs = Math.max(...timed.map((item) => item.endMs));
  const totalMs = Math.max(1, horizonMs - originMs);

  const lanes: TraceTimelineLane[] = timed
    .sort((a, b) => {
      if (a.startMs !== b.startMs) return a.startMs - b.startMs;
      if (a.depth !== b.depth) return a.depth - b.depth;
      return a.span.name.localeCompare(b.span.name);
    })
    .map((item) => {
      const durationMs = Math.max(0, item.endMs - item.startMs);
      return {
        span: item.span,
        depth: item.depth,
        startMs: item.startMs,
        endMs: item.endMs,
        offsetMs: item.startMs - originMs,
        durationMs,
      };
    });

  return { lanes, originMs, totalMs, untimed };
}

export function formatTraceDuration(ms: number | null | undefined): string {
  if (ms == null || !Number.isFinite(ms)) return '—';
  if (ms < 1000) return `${Math.round(ms)}ms`;
  if (ms < 10_000) return `${(ms / 1000).toFixed(2)}s`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)}s`;
  const minutes = Math.floor(ms / 60_000);
  const seconds = ((ms % 60_000) / 1000).toFixed(0);
  return `${minutes}m ${seconds}s`;
}

export function axisTicks(totalMs: number): number[] {
  if (totalMs <= 1) return [0, totalMs];
  const step =
    totalMs <= 500
      ? 100
      : totalMs <= 2000
        ? 500
        : totalMs <= 10_000
          ? 2000
          : totalMs <= 60_000
            ? 10_000
            : Math.ceil(totalMs / 4 / 1000) * 1000;
  const ticks = [0];
  for (let t = step; t < totalMs; t += step) {
    ticks.push(t);
  }
  if (ticks[ticks.length - 1] !== totalMs) {
    ticks.push(totalMs);
  }
  return ticks;
}
