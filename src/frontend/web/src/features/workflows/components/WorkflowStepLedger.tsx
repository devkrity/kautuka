import React from 'react';
import { Link } from 'react-router-dom';
import type { WorkflowJournalEvent } from '../api/workflows';
import { fieldDisplayName, pointerToField } from '../model/workflowDraft';
import {
  describeWorkflowEvent,
  describeWorkflowEventFacts,
  describeWorkflowFailure,
} from '../model/workflowLabels';
import { WorkflowStepActivity } from '../narad/WorkflowStepActivity';
import type { WorkflowNode, WorkflowStepAttempt } from '../schemas/workflowSchema';
import {
  WorkflowActivityJournal,
  WorkflowEventFacts,
  type WorkflowActivityItem,
} from './WorkflowActivityJournal';
import { WorkflowStatusBadge } from './WorkflowStatusBadge';

interface WorkflowStepLedgerProps {
  stepNumber: number;
  node: WorkflowNode;
  agentName: string;
  attempts: WorkflowStepAttempt[];
  events: WorkflowJournalEvent[];
  envelope: Record<string, unknown>;
  fieldLabels: Record<string, string>;
  status: string;
  pausedRunLabel?: string;
  sessionId: string | null;
  active: boolean;
}

function isProblemEvent(type: string): boolean {
  return (
    type.endsWith('.failed') ||
    type.endsWith('.error') ||
    type.endsWith('.cancelled') ||
    type.endsWith('stop.requested')
  );
}

function formatClock(value: string | null | undefined): string {
  if (!value) {
    return '';
  }
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return value;
  }
  return date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
}

function formatValue(value: unknown): string {
  if (value === null || value === undefined || value === '') {
    return '—';
  }
  if (typeof value === 'string' || typeof value === 'number' || typeof value === 'boolean') {
    return String(value);
  }
  return JSON.stringify(value);
}

export const WorkflowStepLedger: React.FC<WorkflowStepLedgerProps> = ({
  stepNumber,
  node,
  agentName,
  attempts,
  events,
  envelope,
  fieldLabels,
  status,
  pausedRunLabel,
  sessionId,
  active,
}) => {
  const [agentHitlWaiting, setAgentHitlWaiting] = React.useState(false);
  const latest = attempts[attempts.length - 1];
  const displayStatus = agentHitlWaiting && status === 'running' ? 'paused' : status;
  const started = formatClock(latest?.startedAt);
  const ended = formatClock(latest?.endedAt);
  const windowLabel = started && ended ? `${started} – ${ended}` : started || ended;
  const stepFailure = describeWorkflowFailure(latest?.failureCode);
  const handed = node.maps.map((map) => {
    const to = pointerToField(map.to);
    return {
      from: pointerToField(map.from),
      to,
      value: envelope[to],
    };
  });
  const sessionHref =
    sessionId && node.agentId
      ? `/workspace/${encodeURIComponent(node.agentId)}?sessionId=${encodeURIComponent(sessionId)}`
      : null;
  const stepLive =
    active && (status === 'running' || status === 'paused' || displayStatus === 'paused');
  const activity = events.map((event, index): WorkflowActivityItem => {
    const failure = describeWorkflowFailure(event.code, event.message);
    const problem = isProblemEvent(event.type);
    const facts = describeWorkflowEventFacts(event);
    const failureText = problem && failure ? failure : '';
    return {
      id: event.eventId,
      label: describeWorkflowEvent(event.type),
      meta: formatClock(event.timestamp),
      status: problem ? 'denied' : stepLive && index === events.length - 1 ? 'running' : 'done',
      detail:
        facts.length > 0 || failureText ? (
          <WorkflowEventFacts facts={facts} failure={failureText || undefined} />
        ) : undefined,
    };
  });

  return (
    <li
      className={`rounded-xl border px-4 py-3 ${active ? 'border-primary/50 bg-primary/5' : 'border-border'}`}
    >
      <div className="flex items-center justify-between gap-3 text-sm">
        <span className="font-medium">Step {stepNumber}</span>
        <WorkflowStatusBadge
          status={displayStatus}
          kind="step"
          label={
            status === 'paused' && active
              ? pausedRunLabel
              : agentHitlWaiting && status === 'running'
                ? 'Waiting for your review'
                : undefined
          }
        />
      </div>
      <p className="mt-1 text-sm">{agentName}</p>
      <p className="mt-1 text-xs text-muted-foreground">{latest ? windowLabel : 'Not started'}</p>
      {attempts.map((attempt) => (
        <p key={attempt.id} className="mt-2 text-sm text-muted-foreground">
          Attempt {attempt.attempt}
          {attempt.reviewNote ? ` · You asked: ${attempt.reviewNote}` : ''}
        </p>
      ))}
      {stepFailure && displayStatus === 'failed' ? (
        <p className="mt-2 text-xs text-destructive">{stepFailure}</p>
      ) : null}

      {handed.length > 0 && attempts.some((attempt) => attempt.status === 'finished') ? (
        <div className="mt-3 space-y-1">
          <p className="section-label">Results passed on</p>
          <dl className="space-y-1">
            {handed.map((item) => (
              <div
                key={`${item.from}->${item.to}`}
                className="flex items-baseline justify-between gap-3 text-xs"
              >
                <dt className="text-muted-foreground">
                  {fieldLabels[item.from] ?? fieldDisplayName({ name: item.from })}
                  {item.from !== item.to
                    ? ` → ${fieldLabels[item.to] ?? fieldDisplayName({ name: item.to })}`
                    : ''}
                </dt>
                <dd className="truncate font-medium">{formatValue(item.value)}</dd>
              </div>
            ))}
          </dl>
        </div>
      ) : null}

      {activity.length > 0 ? (
        <div className="mt-3">
          <WorkflowActivityJournal items={activity} isLive={stepLive} empty="" />
        </div>
      ) : null}

      {sessionId && node.agentId ? (
        <WorkflowStepActivity
          agentId={node.agentId}
          sessionId={sessionId}
          follow={stepLive}
          onWaitingChange={setAgentHitlWaiting}
        />
      ) : null}

      {sessionHref ? (
        <p className="mt-2 text-xs">
          <Link className="text-primary underline-offset-2 hover:underline" to={sessionHref}>
            Open agent activity
          </Link>
        </p>
      ) : null}

      {node.promptTemplate ? (
        <details className="mt-2">
          <summary className="cursor-pointer text-xs text-muted-foreground">Details</summary>
          <p className="mt-2 text-xs font-medium text-muted-foreground">Instructions</p>
          <p className="mt-1 whitespace-pre-wrap text-xs text-muted-foreground">
            {node.promptTemplate
              .split(/\{\{\s*([^{}]+?)\s*\}\}/g)
              .map((part, index) =>
                index % 2 === 1 ? (fieldLabels[part] ?? fieldDisplayName({ name: part })) : part
              )}
          </p>
        </details>
      ) : null}
    </li>
  );
};
