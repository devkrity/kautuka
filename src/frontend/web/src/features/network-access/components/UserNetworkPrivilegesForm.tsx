import React from 'react';
import { Checkbox } from '@/components/ui/checkbox';
import type { UpdateUserNetworkPrivilegesDto } from '../networkAccess';

interface UserNetworkPrivilegesFormProps {
  userId?: string;
  userDisplayName?: string;
  userPronoun?: string;
  canApproveOwn: boolean;
  isAdminViewer: boolean;
  onSave?: (privileges: UpdateUserNetworkPrivilegesDto) => Promise<unknown> | void;
  disabled?: boolean;
}

export function UserNetworkPrivilegesForm({
  canApproveOwn: initialCanApprove,
  isAdminViewer,
  onSave,
  disabled = false,
}: UserNetworkPrivilegesFormProps) {
  const [canApproveOwn, setCanApproveOwn] = React.useState(initialCanApprove);
  React.useEffect(() => setCanApproveOwn(initialCanApprove), [initialCanApprove]);

  if (!isAdminViewer) {
    return (
      <div className="rounded-xl border border-border bg-card p-4 space-y-2">
        <h4 className="font-semibold text-sm">Network access</h4>
        <p className="text-sm text-muted-foreground">
          {canApproveOwn ? 'You can manage your own network access.' : 'Your administrator manages your network access.'}
        </p>
      </div>
    );
  }

  return (
    <div className="rounded-xl border border-border bg-card p-4 space-y-4">
      <h4 className="font-semibold text-base">Network access</h4>
      <div className="flex items-start space-x-3">
        <Checkbox id="user-priv-can-approve" checked={canApproveOwn} disabled={disabled} className="mt-0.5"
          onChange={(event) => {
            const allowed = event.target.checked;
            setCanApproveOwn(allowed);
            void onSave?.({ canApproveOwn: allowed });
          }} />
        <div className="space-y-0.5">
          <label htmlFor="user-priv-can-approve" className="text-sm font-medium leading-none cursor-pointer">
            Can manage their own network access
          </label>
          <p className="text-xs text-muted-foreground">Allows this person to manage their user, agent and connector rules.</p>
        </div>
      </div>
    </div>
  );
}
