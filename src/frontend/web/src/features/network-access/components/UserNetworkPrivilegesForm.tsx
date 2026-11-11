import React from 'react';
import { Link } from 'react-router-dom';
import { Checkbox } from '@/components/ui/checkbox';
import { Alert } from '@/components/ui/alert';
import type { UpdateUserNetworkPrivilegesDto } from '../networkAccess';

interface UserNetworkPrivilegesFormProps {
  userId?: string;
  userDisplayName?: string;
  userPronoun?: string;
  canApproveOwn: boolean;
  canAllowAllAgents: boolean;
  isAdminViewer: boolean;
  onSave?: (privileges: UpdateUserNetworkPrivilegesDto) => Promise<unknown> | void;
  disabled?: boolean;
}

export function UserNetworkPrivilegesForm({
  userId,
  userDisplayName = 'User',
  userPronoun = 'their',
  canApproveOwn: initialCanApprove,
  canAllowAllAgents: initialCanAllowAll,
  isAdminViewer,
  onSave,
  disabled = false,
}: UserNetworkPrivilegesFormProps) {
  const [canApproveOwn, setCanApproveOwn] = React.useState(initialCanApprove);
  const [canAllowAllAgents, setCanAllowAllAgents] = React.useState(initialCanAllowAll);
  const [showWarning, setShowWarning] = React.useState(false);

  // Sync state if props change
  React.useEffect(() => {
    setCanApproveOwn(initialCanApprove);
    setCanAllowAllAgents(initialCanAllowAll);
  }, [initialCanApprove, initialCanAllowAll]);

  if (!isAdminViewer) {
    return (
      <div className="rounded-xl border border-border bg-card p-4 space-y-2">
        <h4 className="font-semibold text-sm">Network access</h4>
        <p className="text-sm text-muted-foreground">
          Your administrator approves network requests for your agents.
        </p>
      </div>
    );
  }

  // Capitalize pronoun for sentence start ("Her" or "Their")
  const capitalizedPronoun =
    userPronoun.toLowerCase() === 'her'
      ? 'Her'
      : userPronoun.toLowerCase() === 'his'
        ? 'His'
        : 'Their';

  const handleApproveOwnChange = (checked: boolean) => {
    const nextApprove = checked;
    let nextAllowAll = canAllowAllAgents;
    if (!nextApprove) {
      // If Box 1 is unticked, Box 2 cannot be on
      nextAllowAll = false;
      if (canAllowAllAgents || canApproveOwn) {
        setShowWarning(true);
      }
    }
    setCanApproveOwn(nextApprove);
    setCanAllowAllAgents(nextAllowAll);
    if (onSave) {
      void onSave({ canApproveOwn: nextApprove, canAllowAllAgents: nextAllowAll });
    }
  };

  const handleAllowAllChange = (checked: boolean) => {
    const nextAllowAll = checked;
    if (!nextAllowAll && canAllowAllAgents) {
      // Unticking Box 2 opens confirmation warning
      setShowWarning(true);
    } else if (nextAllowAll) {
      setShowWarning(false);
    }
    setCanAllowAllAgents(nextAllowAll);
    if (onSave) {
      void onSave({ canApproveOwn, canAllowAllAgents: nextAllowAll });
    }
  };

  return (
    <div className="rounded-xl border border-border bg-card p-4 space-y-4">
      <h4 className="font-semibold text-base">Network access</h4>

      <div className="space-y-4">
        <div className="flex items-start space-x-3">
          <Checkbox
            id="user-priv-can-approve"
            checked={canApproveOwn}
            onChange={(e) => handleApproveOwnChange(e.target.checked)}
            disabled={disabled}
            className="mt-0.5"
          />
          <div className="space-y-0.5">
            <label
              htmlFor="user-priv-can-approve"
              className="text-sm font-medium leading-none cursor-pointer"
            >
              Can approve their own agents' network requests
            </label>
            <p className="text-xs text-muted-foreground">
              If off, requests from this person's agents come to an administrator.
            </p>
          </div>
        </div>

        <div className="flex items-start space-x-3 pl-1">
          <Checkbox
            id="user-priv-can-allow-all"
            checked={canAllowAllAgents}
            onChange={(e) => handleAllowAllChange(e.target.checked)}
            disabled={disabled || !canApproveOwn}
            className="mt-0.5"
          />
          <div className="space-y-0.5">
            <label
              htmlFor="user-priv-can-allow-all"
              className={`text-sm font-medium leading-none ${!canApproveOwn ? 'cursor-not-allowed opacity-60' : 'cursor-pointer'}`}
            >
              Can allow a website for all of their agents, including new ones
            </label>
          </div>
        </div>
      </div>

      {showWarning && (
        <Alert variant="attention" className="text-sm space-y-1">
          <p>
            This pauses all of {userDisplayName}'s 'all my agents' permissions, including for agents
            they create later. Re-ticking restores them. {capitalizedPronoun} rules for single agents
            still apply.{' '}
            <Link
              to={userId ? `/network-access?userId=${encodeURIComponent(userId)}` : '/network-access'}
              className="font-medium underline hover:text-foreground inline-block"
            >
              Review them?
            </Link>
          </p>
        </Alert>
      )}
    </div>
  );
}
