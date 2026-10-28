import React from 'react';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
  DialogClose,
} from '@/components/ui/dialog';

export function Banner({ success, message }: { success: boolean; message: string }) {
  return (
    <Alert variant={success ? 'success' : 'destructive'}>
      <AlertDescription>{message}</AlertDescription>
    </Alert>
  );
}

export function ConfirmDeleteButton({
  label,
  title,
  description,
  onConfirm,
}: {
  label?: string;
  title: string;
  description: string;
  onConfirm: () => void;
}) {
  return (
    <Dialog>
      <DialogTrigger asChild>
        <Button variant="destructive">{label ?? 'Delete'}</Button>
      </DialogTrigger>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{title}</DialogTitle>
          <DialogDescription>{description}</DialogDescription>
        </DialogHeader>
        <DialogFooter>
          <DialogClose asChild>
            <Button variant="secondary">Cancel</Button>
          </DialogClose>
          <DialogClose asChild>
            <Button variant="destructive" onClick={onConfirm}>
              Delete
            </Button>
          </DialogClose>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

export function CapabilityBadge({
  label,
  value,
}: {
  label: string;
  value: boolean | null | undefined;
}) {
  if (value === null || value === undefined) return null;
  return (
    <Badge variant={value ? 'success' : 'danger'}>
      {label}: {value ? 'yes' : 'no'}
    </Badge>
  );
}
