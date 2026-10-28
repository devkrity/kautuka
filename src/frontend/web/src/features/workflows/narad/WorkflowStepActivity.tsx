import React from 'react';
import { ScanSearch } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { NARAD_PROFILE_IDS } from '@devkrity/narad';
import { NaradProvider, useNaradSnapshot } from '@devkrity/narad-react';
import { useApprovalsBySession } from '@/features/approvals';
import {
  ActivityLedger,
  RunTraceInspector,
  createKautukaNaradTransport,
  createNaradSessionBinding,
  fetchKautukaNaradHistoryPages,
  fetchRunTrace,
  selectOpenInterrupts,
  selectRunSummary,
  selectTranscriptTurns,
  type RunTraceView,
} from '@/features/chat';
import { authenticatedFetch } from '@/lib/authenticatedFetch';

const STEP_PROFILES = [
  NARAD_PROFILE_IDS.conversation,
  NARAD_PROFILE_IDS.graph,
  NARAD_PROFILE_IDS.progress,
] as const;

type WorkflowStepActivityProps = Readonly<{
  agentId: string;
  sessionId: string;
  /** While the step is still running, refresh the saved session instead of holding a live stream. */
  follow?: boolean;
  onWaitingChange?: (waiting: boolean) => void;
}>;

export const WorkflowStepActivity: React.FC<WorkflowStepActivityProps> = ({
  agentId,
  sessionId,
  follow = false,
  onWaitingChange,
}) => {
  const binding = React.useMemo(
    () =>
      createNaradSessionBinding({
        sessionId,
        activeProfiles: [...STEP_PROFILES],
      }),
    [sessionId]
  );
  const [connection, setConnection] = React.useState<'loading' | 'ready' | 'error'>('loading');

  React.useEffect(() => {
    const abort = new AbortController();
    const transport = createKautukaNaradTransport({
      agentId,
      fetch: authenticatedFetch,
      claimedProfiles: [...STEP_PROFILES],
    });

    const load = async () => {
      const cursor = binding.store.getSnapshot().lastSessionSequence;
      await fetchKautukaNaradHistoryPages(
        transport,
        sessionId,
        cursor,
        200,
        (record) => {
          binding.store.apply(record);
        },
        abort.signal
      );
    };

    void load()
      .then(() => {
        if (!abort.signal.aborted) {
          setConnection('ready');
        }
      })
      .catch((error: unknown) => {
        if (abort.signal.aborted) {
          return;
        }
        if (error instanceof DOMException && error.name === 'AbortError') {
          return;
        }
        setConnection('error');
      });

    const timer = follow
      ? window.setInterval(() => {
          void load().catch(() => undefined);
        }, 2000)
      : null;

    return () => {
      abort.abort();
      if (timer !== null) {
        window.clearInterval(timer);
      }
      transport.dispose();
    };
  }, [agentId, binding, follow, sessionId]);

  if (connection === 'error') {
    return <p className="mt-2 text-xs text-destructive">Agent activity unavailable.</p>;
  }

  return (
    <NaradProvider store={binding.store}>
      <WorkflowStepActivityBody
        agentId={agentId}
        sessionId={sessionId}
        onWaitingChange={onWaitingChange}
      />
    </NaradProvider>
  );
};

const WorkflowStepActivityBody: React.FC<
  Pick<WorkflowStepActivityProps, 'agentId' | 'sessionId' | 'onWaitingChange'>
> = ({ agentId, sessionId, onWaitingChange }) => {
  const snapshot = useNaradSnapshot();
  const { data: approvals = [] } = useApprovalsBySession(sessionId, { refetchInterval: false });
  const turns = selectTranscriptTurns(snapshot, approvals);
  const waiting = selectOpenInterrupts(snapshot).length > 0;
  const run = selectRunSummary(snapshot);
  const [traceOpen, setTraceOpen] = React.useState(false);
  const [traceLoading, setTraceLoading] = React.useState(false);
  const [traceError, setTraceError] = React.useState<string | null>(null);
  const [traceView, setTraceView] = React.useState<RunTraceView | null>(null);

  React.useEffect(() => {
    onWaitingChange?.(waiting);
  }, [onWaitingChange, waiting]);
  React.useEffect(() => () => onWaitingChange?.(false), [onWaitingChange]);

  const openTrace = React.useCallback(
    async (runId: string) => {
      setTraceOpen(true);
      setTraceLoading(true);
      setTraceError(null);
      setTraceView(null);
      try {
        setTraceView(await fetchRunTrace(agentId, sessionId, runId));
      } catch (error: unknown) {
        setTraceError(error instanceof Error ? error.message : String(error));
      } finally {
        setTraceLoading(false);
      }
    },
    [agentId, sessionId]
  );

  const withActivity = turns.filter((turn) => turn.activityEntries.length > 0);
  if (withActivity.length === 0) {
    return null;
  }

  return (
    <div className="mt-2 space-y-2">
      {withActivity.map((turn, index) => {
        const latest = index === withActivity.length - 1;
        const traceRunId = turn.rootRunId?.trim() || null;
        return (
          <div key={turn.id}>
            {traceRunId ? (
              <div className="mb-1 flex justify-end">
                <Button
                  type="button"
                  title="Open the run timeline"
                  aria-label="Open the run timeline"
                  data-testid="run-trace-open"
                  onClick={() => void openTrace(traceRunId)}
                  variant="outline"
                  size="xs"
                  className="rounded-full text-muted-foreground"
                >
                  <ScanSearch className="h-3 w-3" />
                  Timeline
                </Button>
              </div>
            ) : null}
            <ActivityLedger
              activityEntries={[...turn.activityEntries]}
              isLive={latest && run.isRunning}
              rootRunLive={latest && run.isRunning}
            />
          </div>
        );
      })}
      <RunTraceInspector
        open={traceOpen}
        loading={traceLoading}
        error={traceError}
        trace={traceView}
        onClose={() => setTraceOpen(false)}
      />
    </div>
  );
};
