import React from 'react';
import { Check, ChevronDown, Circle, LoaderCircle, X } from 'lucide-react';
import {
  activityEntryRenderKey,
  orderActivityEntriesForDisplay,
} from '../activity/activityProjection';
import type {
  ActivityEntry as GatewayActivityEntry,
  LiveToolCall,
} from '../types/chatTypes';
import { formatElapsed } from '../utils/chatFormatters';

function activityDurationMs(entry: GatewayActivityEntry): number | null {
  if (!entry.startedAt || !entry.completedAt) {
    return null;
  }
  const start = Date.parse(entry.startedAt);
  const end = Date.parse(entry.completedAt);
  if (!Number.isFinite(start) || !Number.isFinite(end) || end < start) {
    return null;
  }
  return end - start;
}

function activityDurationLabel(entry: GatewayActivityEntry): string | undefined {
  const ms = activityDurationMs(entry);
  return ms == null ? undefined : formatElapsed(ms);
}

interface ActivityLedgerProps {
  activityEntries?: GatewayActivityEntry[];
  isLive?: boolean;
  /**
   * True while the turn's root subtree is still live (running/paused/awaiting approval).
   * Independent of row statuses so approve→tool.call.started gaps still spin.
   */
  rootRunLive?: boolean;
}

type LedgerTimelineEntry =
  { kind: 'backend'; key: string; entry: GatewayActivityEntry };

const actionKindLabel = (kind: LiveToolCall['actionKind'] | string | undefined): string => {
  switch (kind) {
    case 'agent':
      return 'Agent';
    case 'skill':
      return 'Skill';
    case 'superexec':
      return 'SuperExec';
    case 'connector':
      return 'Connector';
    default:
      return 'Action';
  }
};

const toRowStatus = (
  status: string | undefined,
  isLive: boolean
): 'running' | 'done' | 'denied' => {
  switch (status) {
    case 'failed':
    case 'aborted':
      return 'denied';
    case 'started':
      return isLive ? 'running' : 'done';
    default:
      return 'done';
  }
};

const prettyJson = (raw: string | undefined): string => {
  if (!raw) return '';
  try {
    return JSON.stringify(JSON.parse(raw), null, 2);
  } catch {
    return raw;
  }
};

const displayResolutionChannel = (value: string | undefined): string | undefined => {
  switch (value) {
    case 'chat':
    case 'agui': // historical persisted source from the retired browser protocol
      return 'Chat';
    case 'approvals':
    case 'approval_api':
      return 'Approvals';
    case 'telegram':
    case 'channel_callback':
      return 'Telegram';
    default:
      return value;
  }
};

const DetailBlock: React.FC<{ label: string; value: string }> = ({ label, value }) => (
  <div>
    <div className="section-label mb-1">
      {label}
    </div>
    <pre className="max-h-56 overflow-auto whitespace-pre-wrap break-all rounded-md bg-muted/50 px-2.5 py-2 text-[11px] leading-relaxed text-foreground/80">
      {value}
    </pre>
  </div>
);

const ActivityRow: React.FC<{
  label: string;
  meta?: string;
  status: 'running' | 'done' | 'denied';
  hasDetail?: boolean;
  activityId?: string;
  activityKind?: string | null;
  toolCallId?: string | null;
  actionName?: string | null;
  entryStatus?: string | null;
  children?: React.ReactNode;
}> = ({
  label,
  meta,
  status,
  hasDetail,
  activityId,
  activityKind,
  toolCallId,
  actionName,
  entryStatus,
  children,
}) => {
  const canShowDetail = hasDetail ?? Boolean(children);
  const [open, setOpen] = React.useState(false);
  const icon =
    status === 'running' ? (
      <LoaderCircle className="h-3.5 w-3.5 animate-spin text-primary" />
    ) : status === 'denied' ? (
      <X className="h-3.5 w-3.5 text-destructive" />
    ) : (
      <Check className="h-3.5 w-3.5 text-muted-foreground" />
    );

  return (
    <div
      className="border-t border-border/50 first:border-t-0"
      data-testid="activity-ledger-row"
      data-activity-id={activityId}
      data-activity-kind={activityKind ?? undefined}
      data-tool-call-id={toolCallId ?? undefined}
      data-action-name={actionName ?? undefined}
      data-status={entryStatus ?? status}
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
        <span className="min-w-0 flex-1 truncate font-medium text-foreground/80">{label}</span>
        {meta ? <span className="shrink-0 text-[11px] text-muted-foreground">{meta}</span> : null}
        {canShowDetail ? (
          <ChevronDown
            className={`h-3.5 w-3.5 shrink-0 text-muted-foreground transition-transform ${
              open ? 'rotate-180' : ''
            }`}
          />
        ) : null}
      </button>
      {open && children ? <div className="space-y-2 pb-3 pl-5">{children}</div> : null}
    </div>
  );
};

export const ActivityLedger: React.FC<ActivityLedgerProps> = ({
  activityEntries = [],
  isLive = false,
  rootRunLive = false,
}) => {
  const [open, setOpen] = React.useState(isLive);
  // Fold uniqueness is selector/Narad duty. Ledger only chronologizes and counts 1:1.
  const visibleEntries = orderActivityEntriesForDisplay(activityEntries);
  const completedCount = visibleEntries.filter((entry) => entry.status === 'completed').length;
  const approvedOnlyCount = visibleEntries.filter((entry) => entry.status === 'approved').length;
  // Approval "approved" is a terminal human decision — further work (if any) is the tool row.
  // Only pending hitl / in-flight tool actions spin as row-level "running".
  const runningCount = isLive
    ? visibleEntries.filter(
        (entry) => entry.status === 'started' || entry.status === 'pending',
      ).length
    : 0;
  // After outer approve, rows may be terminal-only while the root is still live.
  const showRootLiveWorking = isLive && rootRunLive && runningCount === 0;
  const totalCount = visibleEntries.length;
  const timeline: LedgerTimelineEntry[] = visibleEntries.map((entry) => ({
    kind: 'backend' as const,
    key: activityEntryRenderKey(entry),
    entry,
  }));

  if (totalCount === 0) {
    return null;
  }

  const summaryParts: string[] = [];
  if (runningCount > 0) {
    summaryParts.push(`${runningCount} running`);
  } else if (showRootLiveWorking) {
    summaryParts.push('Working');
  }
  if (completedCount > 0) {
    summaryParts.push(
      `${completedCount} ${completedCount === 1 ? 'action' : 'actions'} completed`
    );
  } else if (approvedOnlyCount > 0 && runningCount === 0) {
    summaryParts.push(
      `${approvedOnlyCount} ${approvedOnlyCount === 1 ? 'approval' : 'approvals'}`
    );
  }
  const summary =
    summaryParts.length > 0
      ? summaryParts.join(' · ')
      : `${totalCount} ${totalCount === 1 ? 'action' : 'actions'}`;
  const summarySpinning = runningCount > 0 || showRootLiveWorking;

  return (
    <div className="mt-3 border-t border-border/60 pt-2" data-testid="activity-ledger">
      <button
        type="button"
        aria-expanded={open}
        onClick={() => setOpen((current) => !current)}
        className="flex w-full items-center gap-2 py-1 text-left text-xs text-muted-foreground hover:text-foreground"
      >
        {summarySpinning ? (
          <LoaderCircle
            className="h-3.5 w-3.5 animate-spin text-primary"
            data-testid="activity-ledger-live-spinner"
          />
        ) : (
          <Circle className="h-3.5 w-3.5 fill-current opacity-50" />
        )}
        <span className="font-medium">{summary}</span>
        <span className="ml-auto text-[11px]">{open ? 'Hide activity' : 'View activity'}</span>
        <ChevronDown
          className={`h-3.5 w-3.5 transition-transform ${open ? 'rotate-180' : ''}`}
        />
      </button>

      {open ? (
        <div className="mt-1">
          {timeline.map((entry) => {
            if (entry.kind === 'backend') {
              const activity = entry.entry;
              if (activity.kind === 'turn') {
                return (
                  <ActivityRow
                    key={entry.key}
                    label={activity.name ?? 'Agent turn'}
                    meta={activityDurationLabel(activity) ?? activity.resultJson}
                    status={toRowStatus(activity.status, isLive)}
                    activityId={activity.id}
                    activityKind={activity.kind}
                    actionName={activity.name}
                    entryStatus={activity.status}
                    hasDetail={Boolean(
                      activity.resultJson || activity.startedAt || activity.completedAt,
                    )}
                  >
                    {activity.startedAt ? (
                      <div className="text-[11px] text-muted-foreground">
                        Started: {activity.startedAt}
                      </div>
                    ) : null}
                    {activity.completedAt ? (
                      <div className="text-[11px] text-muted-foreground">
                        Completed: {activity.completedAt}
                      </div>
                    ) : null}
                    {activity.resultJson ? (
                      <div className="text-[11px] text-muted-foreground">{activity.resultJson}</div>
                    ) : null}
                  </ActivityRow>
                );
              }

              if (activity.kind === 'checkpoint') {
                return (
                  <ActivityRow
                    key={entry.key}
                    label={activity.name ?? `Checkpoint ${activity.checkpointIndex ?? ''}`.trim()}
                    meta={activity.checkpointResumable ? 'Resumable' : undefined}
                    status="done"
                    activityId={activity.id}
                    activityKind={activity.kind}
                    actionName={activity.name}
                    entryStatus={activity.status}
                    hasDetail={Boolean(activity.checkpointBoundaryType || activity.checkpointCreatedAt)}
                  >
                    {activity.checkpointBoundaryType ? (
                      <div className="text-[11px] text-muted-foreground">
                        Boundary: {activity.checkpointBoundaryType}
                      </div>
                    ) : null}
                    {activity.checkpointCreatedAt ? (
                      <div className="text-[11px] text-muted-foreground">
                        Created: {activity.checkpointCreatedAt}
                      </div>
                    ) : null}
                  </ActivityRow>
                );
              }

              if (activity.kind === 'approval') {
                // Human decision terminal: approved/denied/expired are settled. In-flight work after
                // approve lives on the tool action row, not a forever-spinning approval row.
                const approvalRunning = activity.status === 'pending';
                const approvalDenied =
                  activity.status === 'denied' || activity.status === 'expired' || activity.status === 'stopped';
                return (
                  <ActivityRow
                    key={entry.key}
                    label={`Approval · ${actionKindLabel(activity.actionKind)} · ${activity.name ?? 'action'}`}
                    meta={
                      activity.status === 'approved'
                        ? 'Approved'
                        : activity.status === 'pending'
                          ? 'Pending'
                            : activity.status === 'expired'
                            ? 'Expired'
                            : activity.status === 'stopped'
                              ? 'Stopped'
                            : 'Denied'
                    }
                    status={
                      approvalDenied
                        ? 'denied'
                        : approvalRunning
                          ? 'running'
                          : 'done'
                    }
                    activityId={activity.id}
                    activityKind={activity.kind}
                    toolCallId={activity.toolCallId}
                    actionName={activity.name}
                    entryStatus={activity.status}
                    hasDetail={Boolean(activity.argumentsJson || activity.resolvedVia)}
                  >
                    {activity.resolvedVia ? (
                      <div className="text-[11px] text-muted-foreground">
                        Resolved via {displayResolutionChannel(activity.resolvedVia)}
                      </div>
                    ) : null}
                    {activity.requestedAt ? (
                      <div className="text-[11px] text-muted-foreground">
                        Requested: {activity.requestedAt}
                      </div>
                    ) : null}
                    {activity.decidedAt ? (
                      <div className="text-[11px] text-muted-foreground">
                        Decided: {activity.decidedAt}
                      </div>
                    ) : null}
                    {activity.argumentsJson ? (
                      <DetailBlock label="Arguments" value={prettyJson(activity.argumentsJson)} />
                    ) : null}
                  </ActivityRow>
                );
              }

              return (
                <ActivityRow
                  key={entry.key}
                  label={`${actionKindLabel(activity.actionKind)} · ${activity.name ?? 'action'}`}
                  meta={
                    activityDurationLabel(activity) ??
                    (activity.parentToolCallId ? `child of ${activity.parentToolCallId}` : undefined)
                  }
                  status={toRowStatus(activity.status, isLive)}
                  activityId={activity.id}
                  activityKind={activity.kind}
                  toolCallId={activity.toolCallId}
                  actionName={activity.name}
                  entryStatus={activity.status}
                  hasDetail={Boolean(
                    activity.toolCallIds?.length ||
                    activity.parentToolCallId ||
                    activity.argumentsJson ||
                    activity.resultJson ||
                    activity.startedAt ||
                    activity.completedAt
                  )}
                >
                  {activity.toolCallIds?.length ? (
                    <div className="text-[11px] text-muted-foreground">
                      Tool calls: {activity.toolCallIds.join(', ')}
                    </div>
                  ) : null}
                  {activity.startedAt ? (
                    <div className="text-[11px] text-muted-foreground">
                      Started: {activity.startedAt}
                    </div>
                  ) : null}
                  {activity.completedAt ? (
                    <div className="text-[11px] text-muted-foreground">
                      Completed: {activity.completedAt}
                    </div>
                  ) : null}
                  {activity.argumentsJson ? (
                    <DetailBlock label="Arguments" value={prettyJson(activity.argumentsJson)} />
                  ) : null}
                  {activity.resultJson ? (
                    <DetailBlock label="Result" value={prettyJson(activity.resultJson)} />
                  ) : null}
                </ActivityRow>
              );
            }
          })}
        </div>
      ) : null}
    </div>
  );
};
