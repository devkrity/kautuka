export const workflowStatusLabel: Record<string, string> = {
  running: 'Working',
  paused: 'Paused',
  finished: 'Finished',
  error: 'Failed',
  cancelled: 'Stopped',
};

export interface WorkflowRunStatusLike {
  id: string;
  status: string;
}

export interface PendingWorkflowReviewLike {
  instanceId?: string | null;
  kind?: string | null;
  toolType?: string | null;
}

export function workflowRunStatusText(
  run: WorkflowRunStatusLike,
  pendingReviews: PendingWorkflowReviewLike[]
): string {
  if (run.status !== 'paused') return workflowStatusLabel[run.status] ?? run.status;
  return pendingReviews.some(
    (approval) =>
      approval.instanceId === run.id &&
      (approval.kind === 'workflow_review' || approval.toolType === 'workflow_review')
  )
    ? 'Waiting for your review'
    : 'Paused';
}

export const workflowStepStatusLabel: Record<string, string> = {
  pending: 'Queued',
  running: 'Working',
  paused: 'Paused',
  finished: 'Done',
  failed: 'Failed',
  cancelled: 'Stopped',
};

export const workflowFailureLabel: Record<string, string> = {
  'gate.schema_failed': "The step's answer was missing some results.",
  'gate.rejected_exhausted': 'Review was declined too many times.',
  'gate.changes_exhausted': 'You asked for changes twice. Approve or stop this run.',
  'gate.expired': 'The review window expired.',
  'step.run_error': 'The agent run failed.',
  'step.run_cancelled': 'The agent run was stopped.',
  'step.tool_unresolved_failure': 'A tool this step needs could not finish.',
  'step.budget_exceeded': 'This step ran out of budget.',
  'workflow.stopped': 'This run was stopped.',
  'dependency.unavailable': 'A tool or skill this step needs is missing.',
  'snapshot.divergent': 'This run no longer matches the saved workflow.',
  'envelope.secret_detected': 'The working data looked like it contained a secret.',
  'envelope.size_exceeded': 'The working data grew too large.',
  structured_output_invalid: "The step's answer was missing some results.",
  review_rejected: 'You declined to continue.',
};

export const workflowJournalLabel: Record<string, string> = {
  'workflow.started': 'Run started',
  'workflow.resumed': 'Run continued',
  'workflow.paused': 'Waiting for your review',
  'workflow.finished': 'Run finished',
  'workflow.error': 'Run failed',
  'workflow.cancelled': 'Run stopped',
  'workflow.stop.requested': 'Stop requested',
  'workflow.step.started': 'Step started',
  'workflow.step.finished': 'Step finished',
  'workflow.step.failed': 'Step failed',
  'workflow.edge.taken': 'Path taken',
  'structured_output.extracted': 'Ignored extra results',
};

export function describeWorkflowFailure(
  code: string | null | undefined,
  fallback?: string | null
): string {
  if (code && workflowFailureLabel[code]) {
    return workflowFailureLabel[code];
  }
  if (code) {
    return 'This step failed.';
  }
  if (fallback?.trim()) {
    return fallback;
  }
  return '';
}

export function describeWorkflowEvent(type: string): string {
  return workflowJournalLabel[type] ?? type;
}

export function workflowPauseEventText(
  eventTypes: string[],
  index: number,
  pausedWithoutReview: boolean,
  pausedWithReview: boolean,
  reviewPause = false
): string {
  if (
    reviewPause &&
    eventTypes
      .slice(index + 1)
      .some((type) =>
        ['workflow.resumed', 'workflow.finished', 'workflow.cancelled', 'workflow.error'].includes(
          type
        )
      )
  )
    return 'Reviewed';
  if (pausedWithReview && !pausedWithoutReview) return 'Waiting for your review';
  return 'Paused';
}

export interface WorkflowEventFact {
  label: string;
  value: string;
}

function eventTimeLabel(type: string): string {
  if (type.endsWith('.started') || type === 'workflow.resumed') {
    return 'Started';
  }
  if (type.endsWith('.finished')) {
    return 'Completed';
  }
  return 'Time';
}

export function describeWorkflowEventFacts(event: {
  type: string;
  timestamp?: string;
  workflowSequence?: number;
  from?: string;
  to?: string;
  pointer?: string;
  op?: string;
  droppedKeys?: string[];
  envelopeHash?: string;
  envelopeRevision?: number;
  stepAttempt?: number;
  agentRunId?: string;
  definitionId?: string;
  caseIndex?: number;
}): WorkflowEventFact[] {
  const facts: WorkflowEventFact[] = [];
  if (event.timestamp) {
    facts.push({ label: eventTimeLabel(event.type), value: event.timestamp });
  }
  if (event.type === 'workflow.edge.taken' && event.from && event.to) {
    facts.push({ label: 'Path', value: `${event.from} → ${event.to}` });
    if (event.pointer && event.op) {
      facts.push({ label: 'Condition', value: `${event.op} ${event.pointer}` });
    }
  }
  if (event.droppedKeys && event.droppedKeys.length > 0) {
    facts.push({ label: 'Ignored', value: event.droppedKeys.join(', ') });
  }
  if (typeof event.stepAttempt === 'number') {
    facts.push({ label: 'Attempt', value: String(event.stepAttempt) });
  }
  return facts;
}
