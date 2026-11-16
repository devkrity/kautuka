import React from 'react';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import { destinationScope, formatDestinationDisplay } from '../destinationScope';
import type {
  NetworkAccessRequestDto,
  RequestDecisionDto,
  UserPrivilegesSummaryDto,
} from '../networkAccess';

interface PendingApprovalCardProps {
  request: NetworkAccessRequestDto;
  currentUser?: { id?: string; displayName?: string | null; role?: string };
  userPrivileges?: UserPrivilegesSummaryDto;
  expectedRevision: number;
  onDecide: (decision: RequestDecisionDto) => Promise<unknown>;
  disabled?: boolean;
}

export function PendingApprovalCard({
  request,
  currentUser,
  userPrivileges,

  expectedRevision,
  onDecide,
  disabled = false,
}: PendingApprovalCardProps) {
  const isAdmin = currentUser?.role?.toLowerCase() === 'admin';
  const isOwner = Boolean(currentUser?.id && request.userId && currentUser.id === request.userId);

  const canApproveOwn = isAdmin || (isOwner && Boolean(request.ownerCanApproveOwn ?? userPrivileges?.canApproveOwn));
  const canAllowAllAgents = canApproveOwn;
  const defaultScope = request.userId === 'system' ? 'system' : request.agentId ? 'agent' : request.connectorId ? 'connector' : 'user';
  const [allowLan, setAllowLan] = React.useState(false);
  const [confirmedAllScope, setConfirmedAllScope] = React.useState(false);
  const [submitting, setSubmitting] = React.useState(false);

  const isProbe = request.purpose === 'probe';
  const isPrivate = destinationScope(request.destination) === 'private';
  const destFormatted = formatDestinationDisplay(request.destination, request.port);

  // Format first seen time
  const formattedTime = React.useMemo(() => {
    try {
      return new Date(request.firstSeenUtc).toLocaleTimeString([], {
        hour: '2-digit',
        minute: '2-digit',
      });
    } catch {
      return request.firstSeenUtc;
    }
  }, [request.firstSeenUtc]);

  // Actions
  const handleAllowAgent = async () => {
    setSubmitting(true);
    try {
      await onDecide({
        allow: true,
        scope: defaultScope,
        allowLan: isAdmin && allowLan ? true : undefined,
        expectedRevision,
      });
    } catch {
      // Handled by parent error state
    } finally {
      setSubmitting(false);
    }
  };

  const handleKeepBlocked = async () => {
    setSubmitting(true);
    try {
      await onDecide({
        allow: false,
        scope: defaultScope,
        expectedRevision,
      });
    } catch {
      // Handled by parent error state
    } finally {
      setSubmitting(false);
    }
  };

  const handleAllowAllAgents = async () => {
    setConfirmedAllScope(true);
    setSubmitting(true);
    try {
      await onDecide({
        allow: true,
        scope: 'user',
        allowLan: isAdmin && allowLan ? true : undefined,
        expectedRevision,
      });
    } catch {
      // Handled by parent error state
    } finally {
      setSubmitting(false);
    }
  };

  const handleAllowConnector = async () => {
    setSubmitting(true);
    try {
      await onDecide({ allow: true, scope: 'connector', allowLan: isAdmin && allowLan ? true : undefined, expectedRevision });
    } finally {
      setSubmitting(false);
    }
  };

  // Case 2: Ordinary Agent / User Connector Probe Request Card
  const userLabel = request.userDisplayName || request.userId || 'User';
  const agentLabel = request.agentDisplayName || request.agentId || 'Agent';
  const connectorLabel = request.connectorDisplayName || 'this connector';
  const connectorInfo = request.connectorDisplayName ? ` (${request.connectorDisplayName})` : '';
  const agentTitle = !request.agentDisplayName && !request.agentId
    ? `${userLabel}'s ${connectorLabel} wants to reach ${destFormatted}`
    : `${userLabel}'s ${agentLabel}${connectorInfo} wants to reach ${destFormatted}`;

  // If waiting for admin or lacking canApproveOwn:
  // `waitingForAdmin` describes the request owner's permissions. Admins can
  // still decide the request, so that owner-specific flag must not hide their
  // controls.
  const showWaitingNotice = !isAdmin && ((!canApproveOwn && isOwner) || request.waitingForAdmin);

  return (
    <article
      data-testid={`request-card-${request.id}`}
      className="rounded-xl border border-border bg-card p-4 shadow-sm space-y-3"
    >
      <div className="flex flex-wrap items-center gap-2">
        <h3 className="font-semibold text-base">{agentTitle}</h3>
        {isProbe && <Badge variant="secondary">Probe (temporary)</Badge>}
        {request.compatibilityMode && (
          <Badge variant="attention">Compatibility mode (relaxed settings)</Badge>
        )}
      </div>

      <p className="text-sm text-muted-foreground">
        First tried {formattedTime}, tried {request.attempts} times.
      </p>
      <p className="text-sm text-muted-foreground">
        {isProbe
          ? 'This temporary connector probe tried to connect while discovering tools. Any approval applies only until the probe exits.'
          : "This agent or connector tried to connect to a website it hasn't been allowed to use."}
      </p>

      {showWaitingNotice ? (
        <p className="text-sm font-medium text-amber-600 dark:text-amber-400">
          Waiting for an administrator to approve
        </p>
      ) : canApproveOwn ? (
        <div className="space-y-3 pt-1">
          {isAdmin && isPrivate && (
            <div className="flex items-center space-x-2">
              <Checkbox
                id={`lan-${request.id}`}
                checked={allowLan}
                onChange={(e) => setAllowLan(e.target.checked)}
                disabled={disabled || submitting}
              />
              <label
                htmlFor={`lan-${request.id}`}
                className="text-sm leading-none cursor-pointer text-foreground"
              >
                Allow, including devices on my home or office network
              </label>
            </div>
          )}

          <div className="flex flex-wrap items-center gap-2">
            <Button
              type="button"
              variant="default"
              disabled={disabled || submitting}
              onClick={() => void handleAllowAgent()}
            >
              {isProbe ? 'Allow for this probe' : request.agentId ? `Allow for ${agentLabel}` : 'Allow'}
            </Button>
            <Button
              type="button"
              variant="outline"
              disabled={disabled || submitting}
              onClick={() => void handleKeepBlocked()}
            >
              Keep blocked
            </Button>
          </div>

          {!isProbe && request.agentId && request.connectorId && (
            <button type="button" className="text-sm font-medium text-primary hover:underline"
              disabled={disabled || submitting} onClick={() => void handleAllowConnector()}>
              Allow {connectorLabel} for all {isOwner ? 'my' : `${userLabel}'s`} activity
            </button>
          )}

          {/* Temporary probe approvals do not create persistent rules. */}
          {!isProbe && canAllowAllAgents && (
            <div className="pt-1">
              <button
                type="button"
                className="text-sm font-medium text-primary hover:underline focus:outline-none"
                disabled={disabled || submitting}
                onClick={() => void handleAllowAllAgents()}
              >
                {isOwner
                  ? 'Allow for all my activity'
                  : `Allow for all of ${userLabel}'s activity`}
              </button>
              {confirmedAllScope && (
                <p className="text-xs text-muted-foreground mt-1">
                  {isOwner ? 'All your activity' : `All of ${userLabel}'s activity`} can now reach{' '}
                  {destFormatted}.
                </p>
              )}
            </div>
          )}
        </div>
      ) : (
        <p className="text-sm text-muted-foreground italic">
          No permission to decide this request.
        </p>
      )}
    </article>
  );
}
