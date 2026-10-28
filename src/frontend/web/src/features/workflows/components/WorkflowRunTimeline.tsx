import { ScanSearch } from 'lucide-react';
import React from 'react';
import { RunTraceInspector } from '@/features/chat';
import type { WorkflowJournalEvent } from '../api/workflows';
import {
  describeWorkflowEvent,
  describeWorkflowEventFacts,
  describeWorkflowFailure,
  workflowPauseEventText,
} from '../model/workflowLabels';
import { projectWorkflowTrace } from '../model/workflowTrace';
import {
  WorkflowActivityJournal,
  WorkflowEventFacts,
  type WorkflowActivityItem,
  type WorkflowActivityStatus,
} from './WorkflowActivityJournal';

interface WorkflowRunTimelineProps {
  instanceId: string;
  events: WorkflowJournalEvent[];
  stepNames: Map<string, string>;
  isLive?: boolean;
  isLoading?: boolean;
  error?: string | null;
  pausedWithoutReview?: boolean;
  pausedWithReview?: boolean;
}

function isProblemEvent(type: string): boolean {
  return (
    type.endsWith('.failed') ||
    type.endsWith('.error') ||
    type.endsWith('.cancelled') ||
    type.endsWith('stop.requested')
  );
}

function formatClock(value: string): string {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return value;
  }
  return date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
}

function rowStatus(event: WorkflowJournalEvent, liveTail: boolean): WorkflowActivityStatus {
  if (isProblemEvent(event.type)) {
    return 'denied';
  }
  return liveTail ? 'running' : 'done';
}

export const WorkflowRunTimeline: React.FC<WorkflowRunTimelineProps> = ({
  instanceId,
  events,
  stepNames,
  isLive = false,
  isLoading,
  error,
  pausedWithoutReview = false,
  pausedWithReview = false,
}) => {
  const lastPauseIndex = events.reduce(
    (last, event, index) => (event.type === 'workflow.paused' ? index : last),
    -1
  );
  const items = events.map((event, index): WorkflowActivityItem => {
    const stepLabel = event.nodeId ? stepNames.get(event.nodeId) : undefined;
    const failure = describeWorkflowFailure(event.code, event.message);
    const problem = isProblemEvent(event.type);
    const liveTail = isLive && index === events.length - 1 && !problem;
    const facts = describeWorkflowEventFacts(event);
    const failureText = problem ? failure : '';
    const hasDetail = Boolean(stepLabel || facts.length > 0 || failureText);
    return {
      id: event.eventId,
      label:
        event.type === 'workflow.paused'
          ? workflowPauseEventText(
              events.map((item) => item.type),
              index,
              pausedWithoutReview,
              pausedWithReview && index === lastPauseIndex,
              Boolean(event.interruptId)
            )
          : describeWorkflowEvent(event.type),
      meta: formatClock(event.timestamp),
      status: rowStatus(event, liveTail),
      detail: hasDetail ? (
        <WorkflowEventFacts
          facts={facts}
          stepLabel={stepLabel}
          failure={failureText || undefined}
        />
      ) : undefined,
    };
  });

  const [traceOpen, setTraceOpen] = React.useState(false);
  const trace = traceOpen
    ? projectWorkflowTrace(events, stepNames, instanceId, pausedWithoutReview)
    : null;

  return (
    <section className="workspace-panel">
      {events.length > 0 ? (
        <div className="mb-1 flex justify-end">
          <button
            type="button"
            title="Open the run timeline"
            aria-label="Open the run timeline"
            data-testid="workflow-trace-open"
            onClick={() => setTraceOpen(true)}
            className="inline-flex items-center gap-1 rounded-full border border-border/70 px-2 py-1 text-[11px] font-medium text-muted-foreground hover:bg-muted hover:text-foreground"
          >
            <ScanSearch className="h-3 w-3" />
            Timeline
          </button>
        </div>
      ) : null}
      <WorkflowActivityJournal
        items={items}
        isLive={isLive}
        isLoading={isLoading}
        error={error}
        className=""
      />
      <RunTraceInspector
        open={traceOpen}
        loading={false}
        error={null}
        trace={trace}
        onClose={() => setTraceOpen(false)}
      />
    </section>
  );
};
