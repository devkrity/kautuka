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

function describeInitiatingTask(request: NetworkAccessRequestDto): string | undefined {
  if (request.origin !== 'gateway') return undefined;

  switch (request.purpose) {
    case 'agent-run':
      return 'The helper was working on a task that needed its AI model.';
    case 'remote-mcp':
      return 'Kautuka was connecting to a remote connector to discover or use its tools.';
    case 'provider-test':
    case 'ai-probe':
      return 'Kautuka was testing an AI provider connection.';
    case 'ai-discovery':
      return 'Kautuka was looking up the models available from an AI provider.';
    case 'connector-oauth':
    case 'connector-oauth-refresh':
      return 'Kautuka was connecting an account or renewing access for a connector.';
    case 'oidc-login':
    case 'oidc-link':
      return 'Kautuka was communicating with a sign-in provider.';
    case 'catalog-install':
      return 'Kautuka was downloading an item from the catalog.';
    default:
      return undefined;
  }
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
  const [includeSubdomains, setIncludeSubdomains] = React.useState(false);
  const [confirmedAllScope, setConfirmedAllScope] = React.useState<string>();
  const [submitting, setSubmitting] = React.useState(false);

  const isProbe = request.purpose === 'probe';
  const baseDomain = request.destKind === 'host' && !isProbe ? request.baseDomain : null;
  const coverage = includeSubdomains && baseDomain ? { includeSubdomains: true } : {};
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
        ...coverage,
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
    setConfirmedAllScope(undefined);
    setSubmitting(true);
    try {
      await onDecide({
        allow: true,
        scope: 'user',
        ...coverage,
        allowLan: isAdmin && allowLan ? true : undefined,
        expectedRevision,
      });
      setConfirmedAllScope(includeSubdomains && baseDomain ? `${baseDomain} and its subdomains` : destFormatted);
    } catch {
      // Handled by parent error state
    } finally {
      setSubmitting(false);
    }
  };

  const handleAllowConnector = async () => {
    setSubmitting(true);
    try {
      await onDecide({ allow: true, scope: 'connector', ...coverage, allowLan: isAdmin && allowLan ? true : undefined, expectedRevision });
    } catch {
      // Handled by parent error state
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
        <h3 className="font-semibold text-base break-all">{agentTitle}</h3>
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
          : describeInitiatingTask(request) ?? "This agent or connector tried to connect to a website it hasn't been allowed to use."}
      </p>

      {showWaitingNotice ? (
        <p className="text-sm font-medium text-amber-600 dark:text-amber-400">
          {isOwner
            ? 'You need permission to manage your own network access. Ask an administrator to approve this request or grant that permission to your account.'
            : 'Waiting for an administrator to approve'}
        </p>
      ) : canApproveOwn ? (
        <div className="space-y-3 pt-1">
          {baseDomain && (
            <div className="flex items-center space-x-2">
              <Checkbox id={`domain-${request.id}`} checked={includeSubdomains} disabled={disabled || submitting}
                onChange={(event) => setIncludeSubdomains(event.target.checked)} />
              <label htmlFor={`domain-${request.id}`} className="text-sm cursor-pointer">
                Allow all of {baseDomain}, including its subdomains
              </label>
            </div>
          )}
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
              {isProbe ? 'Allow for this probe' : request.userId === 'system' ? 'Allow for everyone' : request.agentId ? `Allow ${agentLabel} to access this site` : 'Allow access'}
            </Button>
            <Button
              type="button"
              variant="outline"
              disabled={disabled || submitting}
              onClick={() => void handleKeepBlocked()}
            >
              {request.userId === 'system' && !isProbe ? 'Block for everyone' : 'Keep blocked'}
            </Button>
          </div>

          {request.userId === 'system' && !isProbe && <p className="text-xs text-muted-foreground">These choices apply to everyone. A block overrides other permissions.</p>}

          {!isProbe && request.userId !== 'system' && request.agentId && request.connectorId && (
            <button type="button" className="text-sm font-medium text-primary hover:underline"
              disabled={disabled || submitting} onClick={() => void handleAllowConnector()}>
              {isOwner
                ? `Allow this site whenever I use ${connectorLabel}`
                : `Allow this site whenever ${userLabel} uses ${connectorLabel}`}
            </button>
          )}

          {/* Temporary probe approvals do not create persistent rules. */}
          {!isProbe && request.userId !== 'system' && canAllowAllAgents && (
            <div className="pt-1">
              <button
                type="button"
                className="text-sm font-medium text-primary hover:underline focus:outline-none"
                disabled={disabled || submitting}
                onClick={() => void handleAllowAllAgents()}
              >
                {isOwner
                  ? 'Allow this site for everything I use'
                  : `Allow this site for everything ${userLabel} uses`}
              </button>
              {confirmedAllScope && (
                <p role="status" className="text-xs text-muted-foreground mt-1">
                  {isOwner ? 'All your activity' : `All of ${userLabel}'s activity`} can now reach{' '}
                  {confirmedAllScope}.
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
