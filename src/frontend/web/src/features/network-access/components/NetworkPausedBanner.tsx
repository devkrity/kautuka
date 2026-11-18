import React from 'react';
import { AlertCircle } from 'lucide-react';
import { Button } from '@/components/ui/button';
import { useCurrentUser } from '@/features/auth';
import {
  useNetworkBrake,
  useUpdateSystemBrake,
  useUpdateUserBrake,
} from '../networkAccess';

export function NetworkPausedBanner() {
  const { data: currentUser } = useCurrentUser();
  const { data: brake } = useNetworkBrake();
  const updateUserBrake = useUpdateUserBrake();
  const updateSystemBrake = useUpdateSystemBrake();

  if (!currentUser || !brake) return null;

  const isAdmin = currentUser.role?.toLowerCase() === 'admin';
  const isSystemPaused = Boolean(brake.systemPaused);
  const isUserPaused = Boolean(brake.userPaused);

  if (!isSystemPaused && !isUserPaused) return null;

  // Determine who paused it
  const pausedBy = isSystemPaused
    ? brake.systemPausedBy || 'Administrator'
    : brake.userPausedBy || 'You';

  // Can the viewer resume?
  // 1. Under system pause: only admin can resume
  // 2. Under user pause:
  //    - If userLockedByAdmin: only admin can resume (absent for ordinary user)
  //    - If not locked by admin: user can resume
  let canResume = false;
  if (isSystemPaused) {
    canResume = isAdmin;
  } else if (isUserPaused) {
    if (brake.userLockedByAdmin) {
      canResume = isAdmin;
    } else {
      canResume = true;
    }
  }

  const handleResume = async () => {
    if (isSystemPaused) {
      await updateSystemBrake.mutateAsync({ on: false });
    } else if (isUserPaused) {
      await updateUserBrake.mutateAsync({ on: false });
    }
  };

  const isBusy = updateUserBrake.isPending || updateSystemBrake.isPending;

  return (
    <div
      role="alert"
      data-testid="network-paused-banner"
      className="bg-destructive/15 border-b border-destructive/30 px-4 py-2.5 text-destructive dark:text-red-400"
    >
      <div className="flex flex-wrap items-center justify-between gap-3 max-w-7xl mx-auto">
        <div className="flex items-center gap-2.5 min-w-0">
          <AlertCircle className="h-5 w-5 shrink-0" />
          <div className="text-sm font-medium">
            <span className="font-semibold">Network paused</span> — Paused by {pausedBy}. Outbound
            network access is currently stopped.
          </div>
        </div>

        {canResume && (
          <Button
            type="button"
            variant="outline"
            size="sm"
            disabled={isBusy}
            onClick={() => void handleResume()}
            className="border-destructive/40 hover:bg-destructive/10 shrink-0 text-xs font-semibold"
          >
            Resume
          </Button>
        )}
      </div>
    </div>
  );
}
