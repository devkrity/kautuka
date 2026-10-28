import { Button } from '@/components/ui/button';
import React from 'react';
import { Check, ChevronDown, X } from 'lucide-react';
import { useCurrentUser } from '@/features/auth';
import type { PendingApproval } from '../types/chatTypes';

const isChatResolutionSource = (value: string | undefined): boolean =>
  value === 'chat' || value === 'agui';

export const ApprovalCard: React.FC<{
  approval: PendingApproval;
  onDecide(approved: boolean): void;
  onStopRun?(): void;
  contextLabel?: string;
  actionsDisabled?: boolean;
}> = ({ approval, onDecide, onStopRun, contextLabel, actionsDisabled = false }) => {
  const [isExpanded, setIsExpanded] = React.useState(false);
  const formatApprovalTimestamp = (value: string | undefined): string | null => {
    if (!value) {
      return null;
    }

    const parsed = new Date(value);
    if (Number.isNaN(parsed.getTime())) {
      return null;
    }

    return new Intl.DateTimeFormat(undefined, {
      hour: 'numeric',
      minute: '2-digit',
      second: '2-digit',
    }).format(parsed);
  };

  const { data: currentUser } = useCurrentUser();
  const isPending = approval.status === 'pending';
  const availableActions = new Set((approval.actions ?? []).map((action) => action.value));
  const showApprove = availableActions.size === 0 || availableActions.has('approve');
  const showDeny = availableActions.size === 0 || availableActions.has('deny');
  const showStopRun =
    Boolean(onStopRun && approval.runId) &&
    (availableActions.size === 0 || availableActions.has('stop_run'));
  const statusClassName =
    approval.status === 'approved'
      ? 'text-done'
      : 'text-muted-foreground';
  const friendlyCurrentUserName = currentUser?.displayName?.trim() || currentUser?.username?.trim();
  const resolvedByLabel = (() => {
    const label = approval.resolvedByLabel?.trim();
    if (!label) {
      if (isChatResolutionSource(approval.resolvedVia) && friendlyCurrentUserName) {
        return friendlyCurrentUserName;
      }
      return undefined;
    }

    if (!friendlyCurrentUserName || !currentUser?.id) {
      return label;
    }

    if (label === currentUser.id || label === `bypass:${currentUser.id}`) {
      return friendlyCurrentUserName;
    }

    return label;
  })();
  const resolvedViaLabel = (() => {
    switch (approval.resolvedVia) {
      case 'chat':
      case 'agui': // historical persisted source from the retired browser protocol
        return 'Chat';
      case 'approval_api':
        return 'Approvals';
      case 'channel_callback':
        return 'Telegram';
      default:
        return undefined;
    }
  })();
  const statusVerb =
    approval.status === 'approved'
      ? 'Approved'
      : approval.status === 'expired'
        ? 'Expired'
        : approval.status === 'stopped'
          ? 'Stopped'
          : 'Denied';
  const statusPrefix = approval.status === 'approved' ? '✓' : approval.status === 'expired' ? '○' : '✗';
  const statusLabel = (() => {
    if (resolvedByLabel && resolvedViaLabel) {
      return `${statusPrefix} ${statusVerb} by ${resolvedByLabel} via ${resolvedViaLabel}`;
    }

    if (resolvedByLabel) {
      return `${statusPrefix} ${statusVerb} by ${resolvedByLabel}`;
    }

    if (resolvedViaLabel) {
      return `${statusPrefix} ${statusVerb} via ${resolvedViaLabel}`;
    }

    return `${statusPrefix} ${statusVerb}`;
  })();
  const requestedAtLabel = formatApprovalTimestamp(approval.requestedAt);
  const decidedAtLabel = formatApprovalTimestamp(approval.decidedAt);
  let argsText: string | undefined;
  if (approval.argumentsJson) {
    try {
      argsText = JSON.stringify(JSON.parse(approval.argumentsJson), null, 2);
    } catch {
      argsText = approval.argumentsJson;
    }
  }

  if (!isPending) {
    return (
      <div
        className="mt-2 overflow-hidden rounded-xl border border-attention/40 bg-attention/5"
        data-testid="hitl-approval"
        data-approval-request-id={approval.requestId}
        data-tool-name={approval.toolName}
        data-approval-status={approval.status}
      >
        <button
          type="button"
          aria-expanded={isExpanded}
          onClick={() => setIsExpanded((current) => !current)}
          className="flex w-full items-center justify-between gap-3 px-3 py-2.5 text-left hover:bg-attention/10 transition-colors"
        >
          <span className="min-w-0">
            <span className={`block text-xs font-medium ${statusClassName}`}>{statusLabel}</span>
            <span className="mt-0.5 block truncate text-[11px] text-muted-foreground">
              Tool: {approval.toolName}
            </span>
          </span>
          <ChevronDown
            className={`h-4 w-4 shrink-0 text-muted-foreground transition-transform ${
              isExpanded ? 'rotate-180' : ''
            }`}
          />
        </button>
        {isExpanded && (
          <div className="border-t border-attention/20 px-3 pb-3 pt-2.5">
            <div className="mb-1.5 flex items-center gap-1.5 text-xs font-semibold text-attention">
              <span>Tool approval:</span>
              <code className="rounded bg-attention/15 px-1 py-0.5 font-mono">
                {approval.toolName}
              </code>
            </div>
            {contextLabel ? (
              <div className="mb-2 text-xs text-attention/80">
                {contextLabel}
              </div>
            ) : null}
            {argsText && (
              <pre className="mb-2 max-h-28 overflow-auto rounded-lg bg-muted/50 px-2 py-1.5 text-[11px] leading-relaxed text-foreground/80 whitespace-pre-wrap break-all">
                {argsText}
              </pre>
            )}
            <div className="flex flex-wrap items-center gap-3 text-[11px] text-muted-foreground">
              {requestedAtLabel && <span>Requested {requestedAtLabel}</span>}
              {decidedAtLabel && <span>Resolved {decidedAtLabel}</span>}
            </div>
          </div>
        )}
      </div>
    );
  }

  return (
    <div
      className="text-sm"
      role="region"
      aria-label={`Approval required for ${approval.toolName}`}
      data-testid="hitl-approval"
      data-approval-request-id={approval.requestId}
      data-tool-name={approval.toolName}
      data-approval-status={approval.status}
    >
      <div className="mb-1.5 flex items-center gap-1.5 text-xs font-medium text-foreground/80">
        <span>
          Tool approval required:{' '}
          <code className="rounded-sm bg-muted px-1 py-0.5 font-mono">{approval.toolName}</code>
        </span>
      </div>
      {contextLabel ? <div className="mb-2 text-xs text-muted-foreground">{contextLabel}</div> : null}
      <div className="flex items-center gap-2 flex-wrap">
        {showApprove ? (
          <Button
            type="button"
            size="xs"
            data-approval-action="approve"
            disabled={actionsDisabled}
            onClick={() => onDecide(true)}
          >
            <Check className="h-3 w-3" />
            Approve
          </Button>
        ) : null}
        {showDeny ? (
          <Button
            type="button"
            size="xs"
            variant="secondary"
            data-approval-action="deny"
            disabled={actionsDisabled}
            onClick={() => onDecide(false)}
            className="bg-muted text-foreground hover:bg-muted/80"
          >
            <X className="h-3 w-3" />
            Deny
          </Button>
        ) : null}
        {showStopRun ? (
          <Button
            type="button"
            size="xs"
            variant="destructive"
            data-approval-action="stop_run"
            disabled={actionsDisabled}
            onClick={onStopRun}
          >
            <X className="h-3 w-3" />
            Stop Run
          </Button>
        ) : null}
        {(argsText || requestedAtLabel) && (
          <Button
            type="button"
            variant="ghost"
            size="xs"
            aria-expanded={isExpanded}
            onClick={() => setIsExpanded((current) => !current)}
            className="ml-auto text-muted-foreground"
          >
            Details
            <ChevronDown
              className={`h-3.5 w-3.5 transition-transform ${isExpanded ? 'rotate-180' : ''}`}
            />
          </Button>
        )}
      </div>
      {isExpanded && (
        <div className="mt-2 space-y-2 border-t border-border/50 pt-2">
          {argsText && (
            <pre className="max-h-28 overflow-auto rounded-md bg-muted/50 px-2 py-1.5 text-[11px] leading-relaxed text-foreground/80 whitespace-pre-wrap break-all">
              {argsText}
            </pre>
          )}
          {requestedAtLabel && (
            <div className="text-[11px] text-muted-foreground">Requested {requestedAtLabel}</div>
          )}
        </div>
      )}
    </div>
  );
};
