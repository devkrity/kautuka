import { Check, ChevronDown, Circle, LoaderCircle, X } from 'lucide-react';
import React from 'react';
import type { WorkflowEventFact } from '../model/workflowLabels';

export type WorkflowActivityStatus = 'running' | 'done' | 'denied';

export interface WorkflowActivityItem {
  id: string;
  label: string;
  meta?: string;
  status: WorkflowActivityStatus;
  detail?: React.ReactNode;
}

interface WorkflowActivityJournalProps {
  items: WorkflowActivityItem[];
  isLive?: boolean;
  isLoading?: boolean;
  error?: string | null;
  empty?: string;
  className?: string;
}

export const WorkflowEventFacts: React.FC<{
  facts: readonly WorkflowEventFact[];
  stepLabel?: string;
  failure?: string;
}> = ({ facts, stepLabel, failure }) => {
  if (!stepLabel && facts.length === 0 && !failure) {
    return null;
  }
  return (
    <>
      {stepLabel ? <div className="text-[11px] text-muted-foreground">Step: {stepLabel}</div> : null}
      {facts.map((fact) => (
        <div key={`${fact.label}:${fact.value}`} className="text-[11px] text-muted-foreground">
          {fact.label}: {fact.value}
        </div>
      ))}
      {failure ? <div className="text-[11px] text-destructive">{failure}</div> : null}
    </>
  );
};

const ActivityRow: React.FC<{ item: WorkflowActivityItem }> = ({ item }) => {
  const canShowDetail = Boolean(item.detail);
  const [open, setOpen] = React.useState(false);
  const icon =
    item.status === 'running' ? (
      <LoaderCircle className="h-3.5 w-3.5 animate-spin text-primary" />
    ) : item.status === 'denied' ? (
      <X className="h-3.5 w-3.5 text-destructive" />
    ) : (
      <Check className="h-3.5 w-3.5 text-muted-foreground" />
    );

  return (
    <div
      className="border-t border-border/50 first:border-t-0"
      data-testid="workflow-activity-row"
      data-status={item.status}
    >
      <button
        type="button"
        aria-expanded={canShowDetail ? open : undefined}
        onClick={() => canShowDetail && setOpen((current) => !current)}
        className={`flex w-full items-center gap-2 py-2 text-left text-xs ${
          canShowDetail ? 'hover:text-foreground' : 'cursor-default'
        }`}
      >
        {icon}
        <span className="min-w-0 flex-1 truncate font-medium text-foreground/80">{item.label}</span>
        {item.meta ? <span className="shrink-0 text-[11px] text-muted-foreground">{item.meta}</span> : null}
        {canShowDetail ? (
          <ChevronDown
            className={`h-3.5 w-3.5 shrink-0 text-muted-foreground transition-transform ${
              open ? 'rotate-180' : ''
            }`}
          />
        ) : null}
      </button>
      {open && item.detail ? <div className="space-y-2 pb-3 pl-5">{item.detail}</div> : null}
    </div>
  );
};

export const WorkflowActivityJournal: React.FC<WorkflowActivityJournalProps> = ({
  items,
  isLive = false,
  isLoading,
  error,
  empty = 'No events.',
  className = 'border-t border-border/60 pt-2',
}) => {
  const [open, setOpen] = React.useState(isLive);
  const runningCount = items.filter((item) => item.status === 'running').length;
  const completedCount = items.filter((item) => item.status === 'done').length;
  const summaryParts: string[] = [];
  if (runningCount > 0) {
    summaryParts.push(`${runningCount} running`);
  }
  if (completedCount > 0) {
    summaryParts.push(`${completedCount} ${completedCount === 1 ? 'action' : 'actions'} completed`);
  }
  const summary =
    summaryParts.length > 0
      ? summaryParts.join(' · ')
      : `${items.length} ${items.length === 1 ? 'action' : 'actions'}`;

  if (error) {
    return <p className="text-sm text-destructive">{error}</p>;
  }
  if (isLoading && items.length === 0) {
    return <p className="text-sm text-muted-foreground">Loading the run journal…</p>;
  }
  if (items.length === 0) {
    return <p className="text-sm text-muted-foreground">{empty}</p>;
  }

  return (
    <div className={className} data-testid="workflow-activity-journal">
      <button
        type="button"
        aria-expanded={open}
        onClick={() => setOpen((current) => !current)}
        className="flex w-full items-center gap-2 py-1 text-left text-xs text-muted-foreground hover:text-foreground"
      >
        {runningCount > 0 ? (
          <LoaderCircle className="h-3.5 w-3.5 animate-spin text-primary" />
        ) : (
          <Circle className="h-3.5 w-3.5 fill-current opacity-50" />
        )}
        <span className="font-medium">{summary}</span>
        <span className="ml-auto text-[11px]">{open ? 'Hide activity' : 'View activity'}</span>
        <ChevronDown className={`h-3.5 w-3.5 transition-transform ${open ? 'rotate-180' : ''}`} />
      </button>
      {open ? (
        <div className="mt-1">
          {items.map((item) => (
            <ActivityRow key={item.id} item={item} />
          ))}
        </div>
      ) : null}
    </div>
  );
};
