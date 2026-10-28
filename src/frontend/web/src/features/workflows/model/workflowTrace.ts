import type { RunTraceSpan, RunTraceView } from '@/features/chat';
import type { WorkflowJournalEvent } from '../api/workflows';
import { workflowPauseEventText } from './workflowLabels';

type OpenSpan = {
  index: number;
};

function timeOf(timestamp: string | undefined): number | null {
  if (!timestamp) {
    return null;
  }
  const value = Date.parse(timestamp);
  return Number.isFinite(value) ? value : null;
}

function stepKey(nodeId: string, attempt: number | undefined): string {
  return `step:${nodeId}:${attempt ?? 1}`;
}

function closeSpan(
  spans: RunTraceSpan[],
  open: OpenSpan,
  endedAtMs: number | null,
  status: RunTraceSpan['status']
): void {
  const span = spans[open.index];
  if (!span) {
    return;
  }
  const startedAtMs = span.startedAtMs ?? null;
  spans[open.index] = {
    ...span,
    status,
    endedAtMs,
    latencyMs:
      startedAtMs != null && endedAtMs != null && endedAtMs >= startedAtMs
        ? endedAtMs - startedAtMs
        : (span.latencyMs ?? null),
  };
}

/**
 * Waterflow spans from the workflow journal already on the page.
 * A step is the interval from step start to step finish. A review is the
 * interval from the pause to the next continue, finish, or stop.
 */
export function projectWorkflowTrace(
  events: readonly WorkflowJournalEvent[],
  stepNames: ReadonlyMap<string, string>,
  instanceId: string,
  pausedWithoutReview = false
): RunTraceView {
  const spans: RunTraceSpan[] = [];
  const rootId = `workflow:${instanceId}`;
  // An object, not a let. A let is narrowed to never after this loop.
  const rootRef: { current: OpenSpan | null } = { current: null };
  const openSteps = new Map<string, OpenSpan>();
  let openWait: OpenSpan | null = null;
  let lastAt: number | null = null;

  const ensureRoot = (startedAtMs: number | null): void => {
    if (rootRef.current) {
      return;
    }
    spans.push({
      id: rootId,
      name: 'Workflow run',
      type: 'run',
      status: 'running',
      startedAtMs,
      payloadJson: JSON.stringify({ instanceId }),
    });
    rootRef.current = { index: spans.length - 1 };
  };

  const closeWait = (at: number | null, status: RunTraceSpan['status']): void => {
    if (!openWait) {
      return;
    }
    closeSpan(spans, openWait, at, status);
    openWait = null;
  };

  for (const [eventIndex, event] of events.entries()) {
    const at = timeOf(event.timestamp);
    if (at != null) {
      lastAt = at;
    }
    const type = event.type;

    if (type === 'workflow.started') {
      ensureRoot(at);
      continue;
    }

    if (type === 'workflow.step.started' && event.nodeId) {
      ensureRoot(at);
      const attempt = event.stepAttempt ?? 1;
      const id = stepKey(event.nodeId, attempt);
      const base = stepNames.get(event.nodeId) ?? event.nodeId;
      spans.push({
        id,
        parentSpanId: rootId,
        name: attempt > 1 ? `${base} · attempt ${attempt}` : base,
        type: 'run.nested',
        status: 'running',
        startedAtMs: at,
        payloadJson: JSON.stringify({
          nodeId: event.nodeId,
          stepAttempt: attempt,
          agentRunId: event.agentRunId ?? null,
        }),
      });
      openSteps.set(id, { index: spans.length - 1 });
      continue;
    }

    if ((type === 'workflow.step.finished' || type === 'workflow.step.failed') && event.nodeId) {
      const id = stepKey(event.nodeId, event.stepAttempt);
      const open = openSteps.get(id);
      if (open) {
        closeSpan(spans, open, at, type === 'workflow.step.failed' ? 'failed' : 'completed');
        openSteps.delete(id);
      }
      continue;
    }

    if (type === 'workflow.paused') {
      ensureRoot(at);
      closeWait(at, 'completed');
      spans.push({
        id: `wait:${event.eventId}`,
        parentSpanId: rootId,
        name: workflowPauseEventText(
          events.map((item) => item.type),
          eventIndex,
          pausedWithoutReview,
          !pausedWithoutReview && Boolean(event.interruptId),
          Boolean(event.interruptId)
        ),
        type: 'workflow.wait',
        status: 'running',
        startedAtMs: at,
        payloadJson: JSON.stringify({ nodeId: event.nodeId ?? null }),
      });
      openWait = { index: spans.length - 1 };
      continue;
    }

    if (type === 'workflow.resumed') {
      closeWait(at, 'completed');
      continue;
    }

    if (
      type === 'workflow.finished' ||
      type === 'workflow.cancelled' ||
      type === 'workflow.error'
    ) {
      ensureRoot(at);
      const stopped = type !== 'workflow.finished';
      closeWait(at, stopped ? 'skipped' : 'completed');
      for (const open of openSteps.values()) {
        closeSpan(spans, open, at, type === 'workflow.error' ? 'failed' : 'skipped');
      }
      openSteps.clear();
      if (rootRef.current) {
        closeSpan(
          spans,
          rootRef.current,
          at,
          type === 'workflow.error'
            ? 'failed'
            : type === 'workflow.cancelled'
              ? 'skipped'
              : 'completed'
        );
        rootRef.current = null;
      }
    }
  }

  if (rootRef.current && lastAt != null) {
    const span = spans[rootRef.current.index];
    if (span && span.endedAtMs == null) {
      closeSpan(spans, rootRef.current, lastAt, 'running');
    }
  }

  return {
    threadId: instanceId,
    runId: instanceId,
    summary: null,
    spans,
  };
}
