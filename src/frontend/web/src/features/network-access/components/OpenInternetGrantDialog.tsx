import React from 'react';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog';
import { Button } from '@/components/ui/button';

interface OpenInternetGrantDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  connectorDisplayName: string;
  userDisplayName?: string;
  onConfirm: () => Promise<void> | void;
  isPending?: boolean;
}

export function OpenInternetGrantDialog({
  open,
  onOpenChange,
  connectorDisplayName,
  userDisplayName = 'User',
  onConfirm,
  isPending = false,
}: OpenInternetGrantDialogProps) {
  const handleConfirm = async () => {
    await onConfirm();
    onOpenChange(false);
  };

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-md">
        <DialogHeader>
          <DialogTitle>Allow {connectorDisplayName} to reach any public website</DialogTitle>
          <DialogDescription className="space-y-2 pt-2 text-foreground">
            <p className="text-sm font-medium text-amber-600 dark:text-amber-400">
              This connector will be able to send anything it can read, including{' '}
              {userDisplayName}'s files and data, to any public website. Only allow this for
              connectors you trust.
            </p>
            <p className="text-sm text-muted-foreground">
              Local network, private, and cloud-metadata addresses stay blocked.
            </p>
          </DialogDescription>
        </DialogHeader>

        <DialogFooter className="mt-4 gap-2 sm:gap-0">
          <Button
            type="button"
            variant="outline"
            disabled={isPending}
            onClick={() => onOpenChange(false)}
          >
            Cancel
          </Button>
          <Button
            type="button"
            variant="destructive"
            disabled={isPending}
            onClick={() => void handleConfirm()}
          >
            Allow any public website
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
