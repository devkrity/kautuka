import { Dialog, DialogContent, DialogHeader, DialogTitle } from '@/components/ui/dialog';
import { useQueries } from '@tanstack/react-query';
import React from 'react';
import { Link } from 'react-router-dom';
import { probeSessionExists } from '../api/approvals';
import { useHitlDeniedCalls, type HitlStatsQuery } from '../api/queries';
import { prettyJson, sessionHref } from '../hooks/useApprovalsPage';
import type { Approval } from '../schemas/approvalSchema';

interface HitlDeniedCallsDrawerProps {
  open: boolean;
  query: HitlStatsQuery;
  title: string;
  windowLabel?: string;
  onClose: () => void;
}

export function HitlDeniedCallsDrawer({
  open,
  query,
  title,
  windowLabel = 'selected window',
  onClose,
}: Readonly<HitlDeniedCallsDrawerProps>) {
  const deniedQuery = useHitlDeniedCalls(query, { enabled: open });
  const deniedItems = deniedQuery.data ?? [];
  const sessionIds = React.useMemo(
    () =>
      [
        ...new Set(
          deniedItems
            .map((item) => item.sessionId?.trim())
            .filter((sessionId): sessionId is string => Boolean(sessionId))
        ),
      ].sort(),
    [deniedItems]
  );
  const sessionChecks = useQueries({
    queries: sessionIds.map((sessionId) => ({
      queryKey: ['sessions', 'exists', sessionId] as const,
      queryFn: () => probeSessionExists(sessionId),
      enabled: open && sessionIds.length > 0,
      staleTime: 60_000,
      retry: false,
    })),
  });
  const existingSessions = React.useMemo(() => {
    const map = new Map<string, boolean>();
    sessionIds.forEach((sessionId, index) => {
      const result = sessionChecks[index];
      if (result?.isSuccess) {
        map.set(sessionId, result.data === true);
      }
    });
    return map;
  }, [sessionChecks, sessionIds]);

  return (
    <Dialog open={open} onOpenChange={(nextOpen) => !nextOpen && onClose()}>
      <DialogContent className="max-h-[80vh] overflow-y-auto">
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
        </DialogHeader>
        <p className="text-xs text-muted-foreground">Denied runs in the {windowLabel}.</p>
        {deniedQuery.isLoading ? (
          <p className="mt-2 text-sm text-muted-foreground">Loading denied calls…</p>
        ) : deniedItems.length === 0 ? (
          <p className="mt-2 text-sm text-muted-foreground">No denied calls in the {windowLabel}.</p>
        ) : (
          <div className="mt-3 space-y-3">
            {deniedItems.map((item) => (
              <DeniedCallCard
                key={item.id}
                item={item}
                sessionExists={
                  item.sessionId ? existingSessions.get(item.sessionId.trim()) : undefined
                }
                onClose={onClose}
              />
            ))}
          </div>
        )}
      </DialogContent>
    </Dialog>
  );
}

function DeniedCallCard({
  item,
  sessionExists,
  onClose,
}: Readonly<{
  item: Approval;
  sessionExists: boolean | undefined;
  onClose: () => void;
}>) {
  const href = sessionHref(item);
  let sessionAction: React.ReactNode;
  if (!href) {
    sessionAction = <span>Session unavailable</span>;
  } else if (sessionExists === true) {
    sessionAction = (
      <Link
        to={href}
        className="text-foreground underline-offset-2 hover:underline"
        onClick={onClose}
      >
        Open session
      </Link>
    );
  } else if (sessionExists === false) {
    sessionAction = <span>Session unavailable</span>;
  } else {
    sessionAction = <span>Checking session…</span>;
  }

  return (
    <div className="rounded-lg border border-border/70 bg-muted/30 p-3">
      <div className="mb-1 flex flex-wrap items-center justify-between gap-2 text-xs text-muted-foreground">
        <span>
          {item.decidedAt ? new Date(item.decidedAt).toLocaleString() : 'Denied'}
          {item.agentName ? ` · ${item.agentName}` : null}
          {item.reason ? ` · ${item.reason}` : null}
        </span>
        {sessionAction}
      </div>
      {item.runId ? (
        <div className="mb-1 font-mono text-[11px] text-muted-foreground">run {item.runId}</div>
      ) : null}
      {item.argumentsJson ? (
        <pre className="max-h-40 overflow-auto whitespace-pre-wrap break-all text-[11px] leading-relaxed text-foreground/80">
          {prettyJson(item.argumentsJson)}
        </pre>
      ) : (
        <p className="text-xs text-muted-foreground">No arguments stored.</p>
      )}
    </div>
  );
}
