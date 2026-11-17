import React from 'react';
import { Button } from '@/components/ui/button';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import type { NetworkAccessBrakeDto } from '../networkAccess';

interface EmergencyBrakeControlsProps {
  brake?: NetworkAccessBrakeDto;
  currentUser?: { id?: string; displayName?: string | null; role?: string };
  onUpdateUserBrake: (on: boolean, userId?: string) => Promise<unknown>;
  onUpdateSystemBrake?: (on: boolean) => Promise<unknown>;
  targetUser?: { id: string; displayName: string };
  disabled?: boolean;
}

export function EmergencyBrakeControls({
  brake,
  currentUser,
  onUpdateUserBrake,
  onUpdateSystemBrake,
  targetUser,
  disabled = false,
}: EmergencyBrakeControlsProps) {
  const isAdmin = currentUser?.role?.toLowerCase() === 'admin';
  const isUserPaused = Boolean(brake?.userPaused);
  const isSystemPaused = Boolean(brake?.systemPaused);

  const [confirmTarget, setConfirmTarget] = React.useState<'user' | 'system' | 'targetUser' | null>(
    null
  );
  const [submitting, setSubmitting] = React.useState(false);
  const [error, setError] = React.useState<string | null>(null);

  // User brake toggle
  const handleToggleUserBrake = async () => {
    setError(null);
    if (!isUserPaused) {
      setConfirmTarget('user');
    } else {
      setSubmitting(true);
      try {
        await onUpdateUserBrake(false);
      } catch {
        setError('The change could not be confirmed. Refresh to check network access before trying again.');
      } finally {
        setSubmitting(false);
      }
    }
  };

  // System brake toggle (admin)
  const handleToggleSystemBrake = async () => {
    setError(null);
    if (!isSystemPaused) {
      setConfirmTarget('system');
    } else {
      if (!onUpdateSystemBrake) return;
      setSubmitting(true);
      try {
        await onUpdateSystemBrake(false);
      } catch {
        setError('The change could not be confirmed. Refresh to check network access before trying again.');
      } finally {
        setSubmitting(false);
      }
    }
  };

  // Target user brake toggle (admin)
  const handleToggleTargetUserBrake = async () => {
    if (!targetUser) return;
    setError(null);
    setConfirmTarget('targetUser');
  };

  const handleConfirmPause = async () => {
    setError(null);
    setSubmitting(true);
    try {
      if (confirmTarget === 'user') {
        await onUpdateUserBrake(true);
      } else if (confirmTarget === 'system' && onUpdateSystemBrake) {
        await onUpdateSystemBrake(true);
      } else if (confirmTarget === 'targetUser' && targetUser) {
        await onUpdateUserBrake(true, targetUser.id);
      }
      setConfirmTarget(null);
    } catch {
      setError('The change could not be confirmed. Refresh to check network access before trying again.');
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <div className="flex flex-wrap items-center gap-2">
      {error && !confirmTarget && <p role="alert" className="text-sm text-destructive">{error}</p>}
      {/* User's own brake button */}
      <Button
        type="button"
        variant={isUserPaused ? 'default' : 'outline'}
        disabled={disabled || submitting}
        onClick={() => void handleToggleUserBrake()}
        className={isUserPaused ? 'bg-amber-600 hover:bg-amber-700 text-white' : ''}
      >
        {isUserPaused ? 'Resume network access' : 'Pause all network access for my agents'}
      </Button>

      {/* Admin system-wide brake button */}
      {isAdmin && onUpdateSystemBrake && (
        <Button
          type="button"
          variant={isSystemPaused ? 'default' : 'outline'}
          disabled={disabled || submitting}
          onClick={() => void handleToggleSystemBrake()}
          className={isSystemPaused ? 'bg-destructive hover:bg-destructive/90 text-white' : ''}
        >
          {isSystemPaused
            ? 'Resume network access for everyone'
            : 'Pause network access for everyone'}
        </Button>
      )}

      {/* Admin per-user pause button */}
      {isAdmin && targetUser && (
        <Button
          type="button"
          variant="outline"
          disabled={disabled || submitting}
          onClick={() => void handleToggleTargetUserBrake()}
        >
          Pause {targetUser.displayName}
        </Button>
      )}

      {/* Confirmation Dialog */}
      <Dialog open={confirmTarget !== null} onOpenChange={(open) => !open && setConfirmTarget(null)}>
        <DialogContent className="max-w-md">
          <DialogHeader>
            <DialogTitle>Pause network access</DialogTitle>
            <DialogDescription className="text-sm pt-2 text-foreground">
              {confirmTarget === 'system'
                ? "This stops everyone's agents from reaching the internet now and ends open connections. Your rules are kept."
                : 'This stops your agents from reaching the internet now and ends open connections. Your rules are kept.'}
            </DialogDescription>
          </DialogHeader>
          {error && <p role="alert" className="text-sm text-destructive">{error}</p>}

          <DialogFooter className="mt-4 gap-2 sm:gap-0">
            <Button
              type="button"
              variant="outline"
              disabled={submitting}
              onClick={() => setConfirmTarget(null)}
            >
              Cancel
            </Button>
            <Button
              type="button"
              variant="destructive"
              disabled={submitting}
              onClick={() => void handleConfirmPause()}
            >
              Pause network
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  );
}
