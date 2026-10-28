import React from 'react';
import { Link } from 'react-router-dom';
import { Check, Clock3, ExternalLink, X } from 'lucide-react';
import { Button } from '@/components/ui/button';
import type { Approval } from '../schemas/approvalSchema';
import { formatRelativeTime, prettyJson, sessionHref } from '../hooks/useApprovalsPage';

export function ApprovalHistoryCard({ item }: { item: Approval }) {
  const approved = item.decision === 'approved';
  const expired = item.decision === 'expired';
  const cancelled = item.decision === 'cancelled';
  const stopped = item.decision === 'stopped';
  const decisionLabel = expired
    ? 'Expired'
    : approved
      ? 'Approved'
      : cancelled
        ? 'Cancelled'
        : stopped
          ? 'Stopped'
          : 'Denied';
  const link = sessionHref(item);

  return (
    <div className="rounded-xl border border-border bg-card p-4">
      <div className="mb-2 flex flex-wrap items-center justify-between gap-2">
        <div className="flex items-center gap-2">
          {approved ? (
            <span className="inline-flex items-center gap-1 rounded-full bg-done/15 px-2 py-0.5 text-xs font-semibold text-done">
              <Check className="h-2.5 w-2.5" />
              Approved
            </span>
          ) : expired ? (
            <span className="inline-flex items-center gap-1 rounded-full bg-muted px-2 py-0.5 text-xs font-semibold text-muted-foreground">
              <Clock3 className="h-2.5 w-2.5" />
              Expired
            </span>
          ) : (
            <span className="inline-flex items-center gap-1 rounded-full bg-destructive/15 px-2 py-0.5 text-xs font-semibold text-destructive">
              <X className="h-2.5 w-2.5" />
              {decisionLabel}
            </span>
          )}
          <span className="rounded-full bg-muted px-2 py-0.5 text-xs font-semibold text-muted-foreground">
            {item.toolType.toUpperCase()}
          </span>
          <span className="font-semibold text-foreground">{item.toolName}</span>
          {item.mcpServerId ? (
            <span className="text-xs text-muted-foreground">MCP: {item.mcpServerId}</span>
          ) : null}
        </div>
        <div className="text-xs text-muted-foreground">
          requested {formatRelativeTime(item.requestedAt)}
        </div>
      </div>

      {item.argumentsJson ? (
        <pre className="mb-3 max-h-32 overflow-auto rounded-lg bg-muted/40 p-2 text-[11px] leading-relaxed text-foreground/80 whitespace-pre-wrap break-all">
          {prettyJson(item.argumentsJson)}
        </pre>
      ) : null}

      {item.agentId ? (
        <div className="mb-2 flex flex-wrap gap-4 text-xs text-muted-foreground">
          <span>Agent: {item.agentName ?? item.agentId}</span>
        </div>
      ) : null}

      <div className="flex flex-wrap items-center justify-between gap-2">
        <p className="text-xs text-muted-foreground">
          {item.decidedBy ? (
            <>
              {decisionLabel} by{' '}
              <span className="font-medium text-foreground">{item.decidedBy}</span>
              {item.decidedAt ? <> · {formatRelativeTime(item.decidedAt)}</> : null}
            </>
          ) : item.decidedAt ? (
            <>
              {decisionLabel} · {formatRelativeTime(item.decidedAt)}
            </>
          ) : null}
          {item.reason ? <span className="ml-2 italic">"{item.reason}"</span> : null}
        </p>
        {link ? (
          <Button asChild variant="secondary" size="xs">
            <Link to={link}>
              View session
              <ExternalLink className="h-3 w-3" />
            </Link>
          </Button>
        ) : null}
      </div>
    </div>
  );
}
