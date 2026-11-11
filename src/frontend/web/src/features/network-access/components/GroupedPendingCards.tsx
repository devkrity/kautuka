import React from 'react';
import { Button } from '@/components/ui/button';
import { PendingApprovalCard } from './PendingApprovalCard';
import type {
  NetworkAccessRequestDto,
  RequestDecisionDto,
  UserPrivilegesSummaryDto,
} from '../networkAccess';

interface GroupedPendingCardsProps {
  requests: NetworkAccessRequestDto[];
  currentUser?: { id?: string; displayName?: string | null; role?: string };
  userPrivileges?: UserPrivilegesSummaryDto;
  expectedRevision: number;
  onDecide: (decision: RequestDecisionDto, requestId: string) => Promise<unknown>;
  disabled?: boolean;
}

interface RequestGroup {
  key: string;
  agentDisplayName?: string;
  requests: NetworkAccessRequestDto[];
}

export function GroupedPendingCards({
  requests,
  currentUser,
  userPrivileges,
  expectedRevision,
  onDecide,
  disabled = false,
}: GroupedPendingCardsProps) {
  // Group requests from the same agent within 60 seconds
  const groups = React.useMemo(() => {
    const result: RequestGroup[] = [];

    // Sort requests by firstSeenUtc
    const sorted = [...requests].sort((a, b) => {
      const ta = new Date(a.firstSeenUtc).getTime();
      const tb = new Date(b.firstSeenUtc).getTime();
      return ta - tb;
    });

    for (const req of sorted) {
      if (!req.agentId || req.systemwide) {
        // Individual ungrouped card
        result.push({
          key: req.id,
          agentDisplayName: req.agentDisplayName,
          requests: [req],
        });
        continue;
      }

      // Check if matches an existing group of the same agent within 60s
      const reqTime = new Date(req.firstSeenUtc).getTime();
      const existing = result.find(
        (g) =>
          g.requests.length > 0 &&
          g.requests[0]?.agentId === req.agentId &&
          !g.requests[0]?.systemwide &&
          Math.abs(new Date(g.requests[0]?.firstSeenUtc ?? 0).getTime() - reqTime) <= 60000
      );

      if (existing) {
        existing.requests.push(req);
      } else {
        result.push({
          key: `group-${req.agentId}-${reqTime}`,
          agentDisplayName: req.agentDisplayName,
          requests: [req],
        });
      }
    }

    return result;
  }, [requests]);

  const [submittingGroupId, setSubmittingGroupId] = React.useState<string | null>(null);

  const handleAllowAllInGroup = async (group: RequestGroup) => {
    setSubmittingGroupId(group.key);
    try {
      for (const req of group.requests) {
        await onDecide(
          {
            allow: true,
            scope: 'agent',
            expectedRevision,
          },
          req.id
        );
      }
    } finally {
      setSubmittingGroupId(null);
    }
  };

  const isAdmin = currentUser?.role?.toLowerCase() === 'admin';

  return (
    <div className="space-y-4">
      {groups.map((group) => {
        if (group.requests.length > 1) {
          const firstReq = group.requests[0]!;
          const isOwner = Boolean(
            currentUser?.id && firstReq.userId && currentUser.id === firstReq.userId
          );
          const canApprove = isAdmin || (isOwner && Boolean(userPrivileges?.canApproveOwn));

          return (
            <div
              key={group.key}
              data-testid={`request-group-${group.key}`}
              className="rounded-2xl border-2 border-primary/20 bg-muted/20 p-4 space-y-3"
            >
              <div className="flex flex-wrap items-center justify-between gap-3 border-b border-border/60 pb-3">
                <div>
                  <h4 className="font-semibold text-base">
                    {group.agentDisplayName || firstReq.agentDisplayName || 'Agent'} ({group.requests.length} requests)
                  </h4>
                  <p className="text-xs text-muted-foreground">
                    Multiple destinations requested within 60 seconds
                  </p>
                </div>
                {canApprove && (
                  <Button
                    type="button"
                    variant="default"
                    size="sm"
                    disabled={disabled || submittingGroupId === group.key}
                    onClick={() => void handleAllowAllInGroup(group)}
                  >
                    Allow all {group.requests.length}
                  </Button>
                )}
              </div>

              <div className="space-y-3">
                {group.requests.map((req) => (
                  <PendingApprovalCard
                    key={req.id}
                    request={req}
                    currentUser={currentUser}
                    userPrivileges={userPrivileges}
                    expectedRevision={expectedRevision}
                    onDecide={(dec) => onDecide(dec, req.id)}
                    disabled={disabled || submittingGroupId === group.key}
                  />
                ))}
              </div>
            </div>
          );
        }

        const singleReq = group.requests[0]!;
        return (
          <PendingApprovalCard
            key={singleReq.id}
            request={singleReq}
            currentUser={currentUser}
            userPrivileges={userPrivileges}
            expectedRevision={expectedRevision}
            onDecide={(dec) => onDecide(dec, singleReq.id)}
            disabled={disabled}
          />
        );
      })}
    </div>
  );
}
