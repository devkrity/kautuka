import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import type { SaveAutopilotScheduleRequest } from '@/features/autopilot';
import type { AutopilotInboxItem, AutopilotRun, AutopilotSchedule } from '@/features/autopilot';
import { getAutopilotCronLabel, getAutopilotModeLabel } from '@/features/autopilot';
import {
  AlertCircle,
  Bell,
  BellOff,
  Bot,
  CalendarClock,
  CheckCircle2,
  ChevronDown,
  ChevronUp,
  Clock,
  Loader2,
  Mail,
  MailOpen,
  Pause,
  Pencil,
  Play,
  Trash2,
  XCircle,
  Zap,
} from 'lucide-react';
import React from 'react';

type StatusTone = 'info' | 'success' | 'attention' | 'danger' | 'muted';

const RUN_STATUS_CONFIG: Record<
  string,
  { label: string; tone: StatusTone; Icon: React.FC<{ className?: string }> }
> = {
  completed: { label: 'Completed', tone: 'success', Icon: CheckCircle2 },
  running: { label: 'Running…', tone: 'info', Icon: Loader2 },
  scheduled: { label: 'Scheduled', tone: 'info', Icon: CalendarClock },
  awaiting_pre_run_approval: { label: 'Awaiting approval', tone: 'attention', Icon: AlertCircle },
  waiting_for_approval: { label: 'Waiting for you', tone: 'attention', Icon: AlertCircle },
  failed: { label: 'Failed', tone: 'danger', Icon: XCircle },
  rejected: { label: 'Rejected', tone: 'danger', Icon: XCircle },
  timed_out: { label: 'Timed out', tone: 'attention', Icon: XCircle },
  no_op: { label: 'Skipped (overlap)', tone: 'muted', Icon: BellOff },
  skipped: { label: 'Skipped', tone: 'muted', Icon: BellOff },
  stale: { label: 'Stale (old run)', tone: 'muted', Icon: BellOff },
};
const runStatusEntries = new Map(Object.entries(RUN_STATUS_CONFIG));

function friendlyTime(value: string | null | undefined): string {
  if (!value) return 'not yet';
  const date = new Date(value);
  const diffSeconds = Math.round((date.getTime() - Date.now()) / 1000);
  const absolute = Math.abs(diffSeconds);
  if (absolute < 60) return diffSeconds >= 0 ? 'in a moment' : 'just now';
  if (absolute < 3600)
    return diffSeconds >= 0
      ? `in ${Math.round(absolute / 60)}m`
      : `${Math.round(absolute / 60)}m ago`;
  if (absolute < 86400)
    return diffSeconds >= 0
      ? `in ${Math.round(absolute / 3600)}h`
      : `${Math.round(absolute / 3600)}h ago`;
  return date.toLocaleDateString(undefined, {
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  });
}

function RunStatusBadge({ status }: Readonly<{ status: string }>) {
  const config = runStatusEntries.get(status) ?? {
    label: status,
    tone: 'muted' as const,
    Icon: Clock,
  };
  const { label, tone, Icon } = config;
  return (
    <Badge variant={tone} className="gap-1 font-medium">
      <Icon className={`h-3 w-3 ${status === 'running' ? 'animate-spin' : ''}`} />
      {label}
    </Badge>
  );
}

interface AutopilotActivityPanelsProps {
  schedules: AutopilotSchedule[];
  runs: AutopilotRun[];
  inbox: AutopilotInboxItem[];
  agents: ReadonlyArray<{ id: string; name: string }>;
  workflows?: ReadonlyArray<{ id: string; name: string }>;
  setDraft: React.Dispatch<React.SetStateAction<SaveAutopilotScheduleRequest>>;
  setEditingScheduleId: (id: string | null) => void;
  setDraftFromSchedule: (id: string) => void;
  setShowScheduleForm: (show: boolean) => void;
  pauseSchedule: (id: string) => Promise<unknown>;
  resumeSchedule: (id: string) => Promise<unknown>;
  runNow: (id: string) => Promise<unknown>;
  deleteSchedule: (id: string) => Promise<unknown>;
  markInboxRead: (id: string) => Promise<unknown>;
  mutating: boolean;
}

export function AutopilotActivityPanels(props: Readonly<AutopilotActivityPanelsProps>) {
  const {
    schedules,
    runs,
    inbox,
    agents,
    workflows = [],
    setDraft,
    setEditingScheduleId,
    setDraftFromSchedule,
    setShowScheduleForm,
    pauseSchedule,
    resumeSchedule,
    runNow,
    deleteSchedule,
    markInboxRead,
    mutating,
  } = props;
  const [showRunHistory, setShowRunHistory] = React.useState(false);
  const [showInbox, setShowInbox] = React.useState(false);
  const unreadCount = inbox.filter((item) => !item.isRead).length;
  const getTargetName = (type: string, id: string) => type === 'workflow'
    ? workflows.find((workflow) => workflow.id === id)?.name ?? id
    : agents.find((agent) => agent.id === id)?.name ?? id;

  return (
    <>
      {/* ── Schedules list ── */}
      <section className="workspace-panel order-1 space-y-3">
        <div className="flex items-center gap-2">
          <Zap className="h-4 w-4 text-primary" />
          <h2 className="text-base font-semibold">Your Schedules</h2>
          <Button
            type="button"
            size="xs"
            className="ml-auto"
            onClick={() => {
              setEditingScheduleId(null);
              setDraft((previous) => ({
                ...previous,
                targetType: 'agent',
                targetId: '',
                cronExpression: '*/5 * * * *',
                mode: 'on',
                schedulePromptContext: '',
                workspaceFilePaths: [],
                notifyUiInbox: true,
                notifyChannels: false,
                preRunApprovalExpiryPolicy: 'next_occurrence',
                preRunApprovalTimeoutSeconds: null,
                overlapPolicy: 'skip_and_notify',
              }));
              setShowScheduleForm(true);
            }}
          >
            + Create new schedule
          </Button>
          {schedules.length > 0 && (
            <span className="rounded-full bg-muted px-2 py-0.5 text-xs text-muted-foreground">
              {schedules.length}
            </span>
          )}
        </div>

        {schedules.length === 0 ? (
          <div className="rounded-xl border border-dashed border-border bg-muted/30 p-8 text-center">
            <CalendarClock className="mx-auto mb-2 h-8 w-8 text-muted-foreground/50" />
            <p className="text-sm font-medium text-muted-foreground">No schedules yet</p>
            <p className="mt-1 text-xs text-muted-foreground">
              Choose Create new schedule to set up your first repeating task.
            </p>
          </div>
        ) : (
          <div className="space-y-2">
            {schedules.map((schedule) => {
              const name = getTargetName(schedule.targetType, schedule.targetId);
              return (
                <div
                  key={schedule.id}
                  className={`rounded-lg border bg-card p-4 transition-colors ${schedule.isPaused ? 'border-border opacity-60' : 'border-border'}`}
                >
                  <div className="flex flex-wrap items-start justify-between gap-3">
                    <div className="min-w-0 flex-1 space-y-1">
                      <div className="flex items-center gap-2">
                        <Bot className="h-4 w-4 shrink-0 text-primary" />
                        <span className="truncate text-sm font-semibold">{name}</span>
                        {schedule.isPaused && <Badge variant="attention">Paused</Badge>}
                        {schedule.mode !== 'on' && !schedule.isPaused && (
                          <Badge variant="muted">{getAutopilotModeLabel(schedule.mode)}</Badge>
                        )}
                      </div>
                      <div className="flex flex-wrap items-center gap-x-3 gap-y-0.5 text-xs text-muted-foreground">
                        <span>
                          <Clock className="mr-1 inline h-3 w-3" />
                          {getAutopilotCronLabel(schedule.cronExpression)}
                        </span>
                        {schedule.nextDueAt && (
                          <span>Next: {friendlyTime(schedule.nextDueAt)}</span>
                        )}
                        <span className="capitalize">{schedule.targetType}</span>
                      </div>
                      {schedule.schedulePromptContext && (
                        <p className="truncate text-xs italic text-muted-foreground">
                          "{schedule.schedulePromptContext}"
                        </p>
                      )}
                    </div>

                    <div className="flex shrink-0 flex-wrap gap-2">
                      <Button
                        type="button"
                        variant="outline"
                        size="xs"
                        title="Edit"
                        onClick={() => {
                          setDraftFromSchedule(schedule.id);
                          setShowScheduleForm(true);
                        }}
                      >
                        <Pencil className="h-3 w-3" /> Edit
                      </Button>
                      <Button
                        type="button"
                        variant="outline"
                        size="xs"
                        onClick={() =>
                          void (schedule.isPaused
                            ? resumeSchedule(schedule.id)
                            : pauseSchedule(schedule.id))
                        }
                        disabled={mutating}
                      >
                        {schedule.isPaused ? (
                          <>
                            <Play className="h-3 w-3 text-done" /> Resume
                          </>
                        ) : (
                          <>
                            <Pause className="h-3 w-3 text-attention" /> Pause
                          </>
                        )}
                      </Button>
                      <Button
                        type="button"
                        variant="outline"
                        size="xs"
                        className="border-primary/30 bg-primary/5 text-primary hover:bg-primary/10"
                        onClick={() => void runNow(schedule.id)}
                        disabled={mutating}
                      >
                        <Zap className="h-3 w-3" /> Run now
                      </Button>
                      <Button
                        type="button"
                        variant="ghost"
                        size="xs"
                        className="border border-destructive/30 text-destructive hover:bg-destructive/5"
                        onClick={() => void deleteSchedule(schedule.id)}
                        disabled={mutating}
                      >
                        <Trash2 className="h-3 w-3" /> Delete
                      </Button>
                    </div>
                  </div>
                </div>
              );
            })}
          </div>
        )}
      </section>

      {/* ── Run history ── */}
      <section className="workspace-panel space-y-3">
        <button
          type="button"
          className="flex w-full items-center gap-2 text-left"
          onClick={() => setShowRunHistory((v) => !v)}
        >
          <Clock className="h-4 w-4 text-primary" />
          <h2 className="text-base font-semibold">Run History</h2>
          {runs.length > 0 && (
            <span className="rounded-full bg-muted px-2 py-0.5 text-xs text-muted-foreground">
              {runs.length}
            </span>
          )}
          <span className="ml-auto text-muted-foreground">
            {showRunHistory ? (
              <ChevronUp className="h-4 w-4" />
            ) : (
              <ChevronDown className="h-4 w-4" />
            )}
          </span>
        </button>

        {showRunHistory &&
          (runs.length === 0 ? (
            <div className="rounded-xl border border-dashed border-border bg-muted/30 p-6 text-center">
              <p className="text-sm text-muted-foreground">
                No runs yet. Once a schedule fires, each attempt shows up here with its result.
              </p>
            </div>
          ) : (
            <div className="space-y-2">
              {runs.map((run) => {
                const name = getTargetName(run.targetType, run.targetId);
                return (
                  <div key={run.id} className="rounded-lg border border-border bg-card px-4 py-3">
                    <div className="flex flex-wrap items-center justify-between gap-2">
                      <div className="flex items-center gap-2">
                        <Bot className="h-3.5 w-3.5 text-muted-foreground" />
                        <span className="text-sm font-medium">{name}</span>
                        <RunStatusBadge status={run.status} />
                      </div>
                      <div className="text-xs text-muted-foreground">
                        Scheduled {friendlyTime(run.scheduledFor)}
                        {run.endedAt && ` · ended ${friendlyTime(run.endedAt)}`}
                      </div>
                    </div>
                    {run.error && (
                      <p className="mt-1.5 rounded bg-destructive/10 px-2 py-1 text-xs text-destructive">
                        {run.error}
                      </p>
                    )}
                    {run.skipReason === 'blocked_by_non_terminal_run' && (
                      <p className="mt-1.5 rounded bg-muted/50 px-2 py-1 text-xs text-muted-foreground">
                        Blocked by {run.blockingRunStateCategory ?? 'active run'}{' '}
                        {run.blockingRunId ? `(${run.blockingRunId})` : ''}
                        {run.blockingRunStatusSnapshot
                          ? ` with status ${run.blockingRunStatusSnapshot}`
                          : ''}
                      </p>
                    )}
                  </div>
                );
              })}
            </div>
          ))}
      </section>

      {/* ── Inbox ── */}
      <section className="workspace-panel space-y-3">
        <button
          type="button"
          className="flex w-full items-center gap-2 text-left"
          onClick={() => setShowInbox((v) => !v)}
        >
          {unreadCount > 0 ? (
            <Bell className="h-4 w-4 text-primary" />
          ) : (
            <Bell className="h-4 w-4 text-muted-foreground" />
          )}
          <h2 className="text-base font-semibold">Inbox Notifications</h2>
          {unreadCount > 0 && (
            <span className="rounded-full bg-primary-fill px-2 py-0.5 text-xs font-medium text-primary-foreground">
              {unreadCount} new
            </span>
          )}
          <span className="ml-auto text-muted-foreground">
            {showInbox ? <ChevronUp className="h-4 w-4" /> : <ChevronDown className="h-4 w-4" />}
          </span>
        </button>

        {showInbox &&
          (inbox.length === 0 ? (
            <div className="rounded-xl border border-dashed border-border bg-muted/30 p-6 text-center">
              <p className="text-sm text-muted-foreground">
                No notifications yet. When a run finishes and you've enabled "Notify me here", a
                message will appear here.
              </p>
            </div>
          ) : (
            <div className="space-y-2">
              {inbox.map((item) => {
                let payloadSummary: string | null = null;
                if (item.payloadJson) {
                  try {
                    const parsed = JSON.parse(item.payloadJson) as Record<string, unknown>;
                    payloadSummary =
                      typeof parsed['summary'] === 'string'
                        ? parsed['summary']
                        : typeof parsed['message'] === 'string'
                          ? parsed['message']
                          : item.payloadJson;
                  } catch {
                    payloadSummary = item.payloadJson;
                  }
                }

                return (
                  <div
                    key={item.id}
                    className={`rounded-lg border px-4 py-3 ${item.isRead ? 'border-border bg-card opacity-60' : 'border-primary/30 bg-primary/5'}`}
                  >
                    <div className="flex items-start justify-between gap-3">
                      <div className="flex items-start gap-2">
                        {item.isRead ? (
                          <MailOpen className="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" />
                        ) : (
                          <Mail className="mt-0.5 h-4 w-4 shrink-0 text-primary" />
                        )}
                        <div>
                          <p className="text-sm font-medium">
                            {item.isRead ? 'Run notification' : '🔔 Run finished'}
                          </p>
                          {payloadSummary && (
                            <p className="mt-0.5 text-xs text-muted-foreground">{payloadSummary}</p>
                          )}
                        </div>
                      </div>
                      {!item.isRead ? (
                        <Button
                          type="button"
                          variant="outline"
                          size="xs"
                          className="shrink-0"
                          onClick={() => void markInboxRead(item.id)}
                        >
                          Mark read
                        </Button>
                      ) : (
                        <span className="shrink-0 text-xs text-muted-foreground">Read</span>
                      )}
                    </div>
                  </div>
                );
              })}
            </div>
          ))}
      </section>
    </>
  );
}
