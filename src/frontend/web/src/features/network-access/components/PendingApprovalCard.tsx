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

  // Privileges check:
  // For ordinary agent requests:
  // - canApproveOwn: If user holds canApproveOwn, or viewer is admin
  // - canAllowAllAgents: Based on request's owner privileges (v7-12)
  const canApproveOwn = isAdmin || (isOwner && Boolean(request.ownerCanApproveOwn ?? userPrivileges?.canApproveOwn));
  const canAllowAllAgents = Boolean(
    request.ownerCanAllowAllAgents !== undefined
      ? request.ownerCanAllowAllAgents
      : (userPrivileges?.canApproveOwn && userPrivileges?.canAllowAllAgents)
  );

  const [allowLan, setAllowLan] = React.useState(false);
  const [confirmedAllScope, setConfirmedAllScope] = React.useState(false);
  const [submitting, setSubmitting] = React.useState(false);

  const isSystemwide = Boolean(request.systemwide);
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
        scope: 'agent',
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
        scope: 'agent',
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

  // Systemwide connector actions
  const handleAllowEveryone = async () => {
    setSubmitting(true);
    try {
      await onDecide({
        allow: true,
        scope: 'connector-system',
        allowLan: isAdmin && allowLan ? true : undefined,
        expectedRevision,
      });
    } catch {
      // Handled by parent error state
    } finally {
      setSubmitting(false);
    }
  };

  const handleAllowUserOnly = async () => {
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

  // Case 1: Systemwide Connector Card
  if (isSystemwide) {
    if (!isAdmin) {
      return (
        <article
          data-testid={`request-card-${request.id}`}
          className="rounded-xl border border-border bg-card p-4 shadow-sm space-y-3"
        >
          <div className="flex flex-wrap items-center gap-2">
            <h3 className="font-semibold text-base">
              {request.connectorDisplayName || 'Connector'} wants to reach {destFormatted}
            </h3>
            {request.compatibilityMode && (
              <Badge variant="attention">Compatibility mode (relaxed settings)</Badge>
            )}
          </div>
          <p className="text-sm text-muted-foreground">
            First tried {formattedTime}, tried {request.attempts} times.
          </p>
          <p className="text-sm text-muted-foreground">
            This agent or connector tried to connect to a website it hasn't been allowed to use.
          </p>
          <p className="text-sm font-medium text-amber-600 dark:text-amber-400">
            Waiting for an administrator to approve
          </p>
        </article>
      );
    }

    // Systemwide Connector Card for Admin
    const isAttributedUser = request.attribution === 'user' && Boolean(request.userDisplayName || request.userId);
    const title = isAttributedUser
      ? `${request.connectorDisplayName || 'Connector'} (used by ${request.userDisplayName || request.userId}) wants to reach ${destFormatted}`
      : `${request.connectorDisplayName || 'Connector'} (used by everyone) wants to reach ${destFormatted}`;

    return (
      <article
        data-testid={`request-card-${request.id}`}
        className="rounded-xl border border-border bg-card p-4 shadow-sm space-y-3"
      >
        <div className="flex flex-wrap items-center gap-2">
          <h3 className="font-semibold text-base">{title}</h3>
          {isProbe && <Badge variant="secondary">Probe (temporary)</Badge>}
          {request.compatibilityMode && (
            <Badge variant="attention">Compatibility mode (relaxed settings)</Badge>
          )}
        </div>
        <p className="text-sm text-muted-foreground">
          First tried {formattedTime}, tried {request.attempts} times.
        </p>
        <p className="text-sm text-muted-foreground">
          This agent or connector tried to connect to a website it hasn't been allowed to use.
        </p>

        {request.attribution === 'none' && (
          <p className="text-sm text-muted-foreground rounded-md bg-muted/50 p-2.5">
            This connector is shared, so Kautuka can't tell which person's request this is.
          </p>
        )}

        {isPrivate && (
          <div className="flex items-center space-x-2 pt-1">
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

        <div className="flex flex-wrap items-center gap-2 pt-2">
          {isProbe ? (
            <Button
              type="button"
              variant="default"
              disabled={disabled || submitting}
              onClick={() => void handleAllowAgent()}
            >
              Allow for this probe
            </Button>
          ) : (
            <>
              <Button
                type="button"
                variant="default"
                disabled={disabled || submitting}
                onClick={() => void handleAllowEveryone()}
              >
                Allow for everyone
              </Button>

              {isAttributedUser && (
                <Button
                  type="button"
                  variant="outline"
                  disabled={disabled || submitting}
                  onClick={() => void handleAllowUserOnly()}
                >
                  Allow for {request.userDisplayName || request.userId} only
                </Button>
              )}
            </>
          )}

          <Button
            type="button"
            variant="outline"
            disabled={disabled || submitting}
            onClick={() => void handleKeepBlocked()}
          >
            Keep blocked
          </Button>
        </div>
      </article>
    );
  }

  // Case 2: Ordinary Agent / User Connector Probe Request Card
  const userLabel = request.userDisplayName || request.userId || 'User';
  const agentLabel = request.agentDisplayName || request.agentId || 'Agent';
  const connectorLabel = request.connectorDisplayName || request.connectorId || 'Connector';
  const connectorInfo = request.connectorDisplayName ? ` (${request.connectorDisplayName})` : '';
  const agentTitle = isProbe && !request.agentDisplayName && !request.agentId
    ? `${userLabel}'s ${connectorLabel} wants to reach ${destFormatted}`
    : `${userLabel}'s ${agentLabel}${connectorInfo} wants to reach ${destFormatted}`;

  // If waiting for admin or lacking canApproveOwn:
  const showWaitingNotice = (!canApproveOwn && isOwner) || request.waitingForAdmin;

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
              {isProbe ? 'Allow for this probe' : 'Allow'}
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

          {/* Secondary link: Shown ONLY when request's user holds both canApproveOwn and canAllowAllAgents
              and this is NOT an ephemeral probe request.
              Completely ABSENT from DOM when canAllowAllAgents is false! */}
          {!isProbe && canAllowAllAgents && (
            <div className="pt-1">
              <button
                type="button"
                className="text-sm font-medium text-primary hover:underline focus:outline-none"
                disabled={disabled || submitting}
                onClick={() => void handleAllowAllAgents()}
              >
                {isOwner
                  ? 'Allow for all my agents, including new ones'
                  : `Allow for all of ${userLabel}'s agents, including new ones`}
              </button>
              {confirmedAllScope && (
                <p className="text-xs text-muted-foreground mt-1">
                  All your agents, including ones you create later, can now reach{' '}
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
