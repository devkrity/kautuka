import { apiClient } from '@/lib/apiClient';

export interface RunTraceSpan {
  id: string;
  parentSpanId?: string | null;
  name: string;
  type: string;
  status: 'running' | 'completed' | 'failed' | 'skipped' | string;
  startedAtMs?: number | null;
  endedAtMs?: number | null;
  latencyMs?: number | null;
  payloadJson?: string | null;
}

export interface RunTraceView {
  threadId: string;
  runId: string;
  traceId?: string | null;
  openObserveUrl?: string | null;
  summary?: {
    promptTokens: number;
    completionTokens: number;
    totalTokens: number;
    elapsedMs: number | null;
    toolCount: number;
  } | null;
  spans: RunTraceSpan[];
}

type NaradJournalEvent = Readonly<{
  type?: string;
  eventId?: string;
  runId?: string;
  rootRunId?: string;
  timestamp?: string;
  toolCallId?: string;
  toolName?: string;
  outcome?: string;
  usage?: {
    promptTokens?: number;
    completionTokens?: number;
    totalTokens?: number;
    elapsedMs?: number;
  };
  code?: string;
  message?: string;
  progressId?: string;
  category?: string;
  label?: string;
  status?: string;
  [key: string]: unknown;
}>;

function timestampMs(value: string | undefined): number | null {
  if (!value) return null;
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : null;
}

function latencyMs(start: number | null | undefined, end: number | null | undefined): number | null {
  if (start == null || end == null || end < start) {
    return null;
  }
  return end - start;
}

function formatTurnName(label: string, usage: ReturnType<typeof readUsageTokens> | null): string {
  if (!usage || (usage.prompt <= 0 && usage.completion <= 0)) {
    return label;
  }
  return `${label} · in ${usage.prompt} / out ${usage.completion}`;
}

function belongsToRoot(event: NaradJournalEvent, rootRunId: string): boolean {
  const eventRoot = event.rootRunId ?? event.runId;
  return eventRoot === rootRunId || event.runId === rootRunId;
}

function readUsageTokens(usage: NaradJournalEvent['usage']): {
  prompt: number;
  completion: number;
  total: number;
  elapsedMs: number | null;
} {
  if (!usage) {
    return { prompt: 0, completion: 0, total: 0, elapsedMs: null };
  }
  const prompt = usage.promptTokens ?? 0;
  const completion = usage.completionTokens ?? 0;
  const total = usage.totalTokens ?? prompt + completion;
  const elapsedMs = typeof usage.elapsedMs === 'number' ? usage.elapsedMs : null;
  return { prompt, completion, total, elapsedMs };
}

/**
 * Project meaningful product-level spans from public Narad authority history:
 * root lifecycle, tool calls (with duration), progress steps, and token usage.
 * Does not depend on non-public `trace.event` diagnostics frames.
 *
 * Trace honesty rules:
 * - Wall-clock start/end for bars (not stacked nested usage as root duration).
 * - Multiple root `run.finished` events are kept but earlier ones are labeled
 *   anomalous when later root activity exists (tools / final terminal).
 * - When `progress.recorded` agent.turn events carry usage, the header sums
 *   those turns (root + nested). Otherwise last root terminal + nested terminals.
 */
export function projectAuthorityTrace(
  events: readonly NaradJournalEvent[],
  rootRunId: string,
): { spans: RunTraceSpan[]; summary: RunTraceView['summary'] } {
  const relevant = events.filter((event) => belongsToRoot(event, rootRunId));
  const spans: RunTraceSpan[] = [];
  let runStartedAt: number | null = null;
  let runEndedAt: number | null = null;
  let runStatus: string = 'running';
  let rootPromptTokens = 0;
  let rootCompletionTokens = 0;
  let rootTotalTokens = 0;
  let rootUsageElapsedMs: number | null = null;
  let nestedPromptTokens = 0;
  let nestedCompletionTokens = 0;
  let nestedTotalTokens = 0;
  let nestedUsageElapsedMs: number | null = null;
  let hasUsage = false;
  let turnPromptTokens = 0;
  let turnCompletionTokens = 0;
  let turnTotalTokens = 0;
  let hasTurnUsage = false;
  let toolCount = 0;
  const progressSpanIndex = new Map<string, number>();
  const openTools = new Map<
    string,
    { name: string; startedAtMs: number | null; index: number }
  >();
  const seenEventIds = new Set<string>();

  for (let index = 0; index < relevant.length; index += 1) {
    const event = relevant[index]!;
    const type = event.type ?? '';
    const at = timestampMs(event.timestamp);
    const eventId = event.eventId ?? `${type}-${index}`;
    if (event.eventId) {
      if (seenEventIds.has(event.eventId)) {
        continue;
      }
      seenEventIds.add(event.eventId);
    }

    if (type === 'run.started' && (event.runId === rootRunId || event.rootRunId === rootRunId)) {
      if (event.runId === rootRunId) {
        // Prefer the first authentic root start.
        if (runStartedAt == null) {
          runStartedAt = at;
        }
        if (!spans.some((span) => span.type === 'run')) {
          spans.push({
            id: `${eventId}:run`,
            name: 'Root run',
            type: 'run',
            status: 'running',
            startedAtMs: at,
            payloadJson: JSON.stringify({ eventId, timestamp: event.timestamp }),
          });
        }
      }
      continue;
    }

    if (type === 'tool.call.started' && typeof event.toolCallId === 'string') {
      toolCount += 1;
      const name = event.toolName?.trim() || event.toolCallId;
      openTools.set(event.toolCallId, { name, startedAtMs: at, index: spans.length });
      spans.push({
        id: event.toolCallId,
        parentSpanId: `${rootRunId}:run-proxy`,
        name,
        type: 'tool',
        status: 'running',
        startedAtMs: at,
        payloadJson: JSON.stringify({
          toolCallId: event.toolCallId,
          toolName: event.toolName,
          runId: event.runId,
        }),
      });
      continue;
    }

    if (type === 'tool.call.finished' && typeof event.toolCallId === 'string') {
      const open = openTools.get(event.toolCallId);
      const startedAtMs = open?.startedAtMs ?? null;
      const status =
        event.outcome === 'error' ? 'failed' : event.outcome === 'cancelled' ? 'skipped' : 'completed';
      if (open) {
        const existing = spans[open.index];
        if (existing) {
          spans[open.index] = {
            ...existing,
            status,
            endedAtMs: at,
            latencyMs: latencyMs(startedAtMs, at),
            payloadJson: JSON.stringify({
              toolCallId: event.toolCallId,
              toolName: event.toolName ?? open.name,
              outcome: event.outcome,
              code: event.code,
              message: event.message,
              runId: event.runId,
            }),
          };
        }
        openTools.delete(event.toolCallId);
      } else {
        spans.push({
          id: event.toolCallId,
          parentSpanId: `${rootRunId}:run-proxy`,
          name: event.toolName?.trim() || event.toolCallId,
          type: 'tool',
          status,
          startedAtMs: null,
          endedAtMs: at,
          latencyMs: null,
          payloadJson: JSON.stringify(event),
        });
      }
      continue;
    }

    if (type === 'progress.recorded' && typeof event.progressId === 'string') {
      const status =
        event.status === 'failed'
          ? 'failed'
          : event.status === 'cancelled'
            ? 'skipped'
            : event.status === 'active'
              ? 'running'
              : 'completed';
      const turnUsage = event.usage ? readUsageTokens(event.usage) : null;
      const isAgentTurn = event.category === 'agent.turn';
      if (
        isAgentTurn &&
        status !== 'running' &&
        turnUsage &&
        (turnUsage.prompt > 0 || turnUsage.completion > 0)
      ) {
        hasTurnUsage = true;
        hasUsage = true;
        turnPromptTokens += turnUsage.prompt;
        turnCompletionTokens += turnUsage.completion;
        turnTotalTokens += turnUsage.total;
      }
      const existingIndex = progressSpanIndex.get(event.progressId);
      const label = event.label?.trim() || event.progressId;
      if (existingIndex != null) {
        const previous = spans[existingIndex]!;
        spans[existingIndex] = {
          ...previous,
          name: formatTurnName(label, turnUsage),
          status,
          endedAtMs: status === 'running' ? previous.endedAtMs ?? null : at,
          latencyMs:
            status === 'running' ? previous.latencyMs ?? null : latencyMs(previous.startedAtMs, at),
          payloadJson: JSON.stringify({
            progressId: event.progressId,
            category: event.category,
            status: event.status,
            usage: event.usage ?? null,
          }),
        };
      } else {
        progressSpanIndex.set(event.progressId, spans.length);
        spans.push({
          id: `progress:${event.progressId}`,
          parentSpanId: `${rootRunId}:run-proxy`,
          name: formatTurnName(label, turnUsage),
          type: event.category?.trim() || 'progress',
          status,
          startedAtMs: at,
          endedAtMs: status === 'running' ? null : at,
          latencyMs: null,
          payloadJson: JSON.stringify({
            progressId: event.progressId,
            category: event.category,
            status: event.status,
            usage: event.usage ?? null,
          }),
        });
      }
      continue;
    }

    if (
      (type === 'run.finished' || type === 'run.error' || type === 'run.cancelled') &&
      event.runId === rootRunId
    ) {
      runEndedAt = at;
      runStatus = type === 'run.finished' ? 'completed' : type === 'run.error' ? 'failed' : 'skipped';
      if (event.usage) {
        hasUsage = true;
        const usage = readUsageTokens(event.usage);
        // Last root terminal wins for root usage (do not stack successive root finishes).
        rootPromptTokens = usage.prompt;
        rootCompletionTokens = usage.completion;
        rootTotalTokens = usage.total;
        rootUsageElapsedMs = usage.elapsedMs;
      }
      const wallElapsed = latencyMs(runStartedAt, at);
      spans.push({
        id: `${eventId}:terminal`,
        parentSpanId: spans.find((span) => span.type === 'run')?.id ?? null,
        name:
          type === 'run.finished'
            ? 'Run finished'
            : type === 'run.error'
              ? `Run error${event.code ? ` · ${event.code}` : ''}`
              : 'Run cancelled',
        type: 'run.terminal',
        status: runStatus,
        startedAtMs: runStartedAt,
        endedAtMs: at,
        // Gantt/waterflow must follow wall clock, not reported usage, so later
        // activity after an early finish is visually honest.
        latencyMs: wallElapsed,
        payloadJson: JSON.stringify({
          type,
          code: event.code,
          message: event.message,
          usage: event.usage ?? null,
          wallElapsedMs: wallElapsed,
          usageElapsedMs: rootUsageElapsedMs,
        }),
      });
      continue;
    }

    // Child run terminal usage still counts toward root rollup.
    if (
      (type === 'run.finished' || type === 'run.error' || type === 'run.cancelled') &&
      event.rootRunId === rootRunId &&
      event.runId &&
      event.runId !== rootRunId
    ) {
      if (event.usage) {
        hasUsage = true;
        const usage = readUsageTokens(event.usage);
        nestedPromptTokens += usage.prompt;
        nestedCompletionTokens += usage.completion;
        nestedTotalTokens += usage.total;
        if (usage.elapsedMs != null) {
          nestedUsageElapsedMs = (nestedUsageElapsedMs ?? 0) + usage.elapsedMs;
        }
      }
      const nestedElapsed =
        typeof event.usage?.elapsedMs === 'number' && event.usage.elapsedMs >= 0
          ? event.usage.elapsedMs
          : null;
      const nestedEnded = at;
      const nestedStarted =
        nestedEnded != null && nestedElapsed != null ? nestedEnded - nestedElapsed : null;
      spans.push({
        id: `${eventId}:child-terminal`,
        parentSpanId: `${rootRunId}:run-proxy`,
        name: `Nested run ${event.runId.slice(0, 8)}`,
        type: 'run.nested',
        status: type === 'run.finished' ? 'completed' : type === 'run.error' ? 'failed' : 'skipped',
        startedAtMs: nestedStarted,
        endedAtMs: nestedEnded,
        latencyMs: nestedElapsed ?? latencyMs(nestedStarted, nestedEnded),
        payloadJson: JSON.stringify({ runId: event.runId, usage: event.usage ?? null }),
      });
    }
  }

  // Close synthetic parent for tools: attach children under root run span when present.
  const rootSpan = spans.find((span) => span.type === 'run');
  const rootSpanId = rootSpan?.id ?? `${rootRunId}:run-proxy`;
  for (let i = 0; i < spans.length; i += 1) {
    const span = spans[i]!;
    if (span.parentSpanId === `${rootRunId}:run-proxy`) {
      spans[i] = { ...span, parentSpanId: rootSpan ? rootSpan.id : null };
    }
  }

  // Mark anomalous early root terminals when journal continued after them.
  const rootTerminalIndices = spans
    .map((span, index) => ({ span, index }))
    .filter(({ span }) => span.type === 'run.terminal')
    .sort((a, b) => (a.span.endedAtMs ?? 0) - (b.span.endedAtMs ?? 0));
  if (rootTerminalIndices.length > 1) {
    for (let t = 0; t < rootTerminalIndices.length - 1; t += 1) {
      const { span, index } = rootTerminalIndices[t]!;
      const finishAt = span.endedAtMs ?? Number.NEGATIVE_INFINITY;
      const activityAfter = spans.some(
        (candidate) =>
          candidate.type !== 'run' &&
          candidate.type !== 'run.terminal' &&
          (candidate.startedAtMs ?? candidate.endedAtMs ?? 0) > finishAt,
      );
      spans[index] = {
        ...span,
        name: activityAfter
          ? 'Early run.finished (journal continued)'
          : 'Superseded run.finished',
        status: 'failed',
        payloadJson: JSON.stringify({
          ...(span.payloadJson ? JSON.parse(span.payloadJson) as object : {}),
          anomalous: true,
          reason: activityAfter
            ? 'Root terminal was followed by further authority activity for this root.'
            : 'A later root terminal replaced this finish.',
        }),
      };
    }
  }

  const promptTokens = hasTurnUsage
    ? turnPromptTokens
    : rootPromptTokens + nestedPromptTokens;
  const completionTokens = hasTurnUsage
    ? turnCompletionTokens
    : rootCompletionTokens + nestedCompletionTokens;
  const totalTokens = hasTurnUsage
    ? turnTotalTokens > 0
      ? turnTotalTokens
      : turnPromptTokens + turnCompletionTokens
    : rootTotalTokens + nestedTotalTokens > 0
      ? rootTotalTokens + nestedTotalTokens
      : promptTokens + completionTokens;

  const summaryUsageNote = hasTurnUsage
    ? 'Sum of agent.turn progress usage.'
    : rootTerminalIndices.length > 1
      ? 'Multiple root terminals in journal; usage from last terminal + nested.'
      : undefined;

  const wallRootElapsed = latencyMs(runStartedAt, runEndedAt);
  if (rootSpan) {
    const rootIndex = spans.findIndex((span) => span.id === rootSpan.id);
    if (rootIndex >= 0) {
      spans[rootIndex] = {
        ...rootSpan,
        status: runStatus,
        endedAtMs: runEndedAt,
        latencyMs: wallRootElapsed,
        payloadJson: JSON.stringify({
          runId: rootRunId,
          usage: hasUsage
            ? {
                promptTokens,
                completionTokens,
                totalTokens,
                elapsedMs: rootUsageElapsedMs ?? wallRootElapsed,
                usageNote: summaryUsageNote,
              }
            : null,
        }),
      };
    }
  } else if (spans.length > 0) {
    spans.unshift({
      id: rootSpanId,
      name: 'Root run',
      type: 'run',
      status: runStatus,
      startedAtMs: runStartedAt,
      endedAtMs: runEndedAt,
      latencyMs: wallRootElapsed,
    });
    for (let i = 1; i < spans.length; i += 1) {
      const span = spans[i]!;
      if (!span.parentSpanId) {
        spans[i] = { ...span, parentSpanId: rootSpanId };
      }
    }
  }

  return {
    spans,
    summary:
      hasUsage || toolCount > 0 || runStartedAt != null
        ? {
            promptTokens,
            completionTokens,
            totalTokens,
            elapsedMs: wallRootElapsed ?? rootUsageElapsedMs,
            toolCount,
          }
        : null,
  };
}

export async function fetchRunTrace(
  agentId: string,
  threadId: string,
  runId: string,
): Promise<RunTraceView> {
  const collected: NaradJournalEvent[] = [];
  let cursor = 0;

  while (true) {
    // Session-owner public authority feed (not admin-only /trace, which also
    // incorrectly filtered for non-emitted trace.event diagnostics).
    const response = await apiClient.get(
      `/narad/v1/sessions/${encodeURIComponent(threadId)}/events`,
      {
        params: {
          agentId,
          afterSessionSequence: cursor,
          limit: 200,
        },
      },
    );

    const payload = response.data as {
      events?: NaradJournalEvent[];
      hasMore?: boolean;
      nextSessionSequence?: number | null;
    };

    const events = Array.isArray(payload.events) ? payload.events : [];
    collected.push(...events);

    if (!payload.hasMore || payload.nextSessionSequence == null) {
      break;
    }

    cursor = payload.nextSessionSequence;
  }

  const { spans, summary } = projectAuthorityTrace(collected, runId);
  return {
    threadId,
    runId,
    summary,
    spans,
  };
}
