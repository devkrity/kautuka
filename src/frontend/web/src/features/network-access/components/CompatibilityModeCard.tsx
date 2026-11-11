import React from 'react';
import { Button } from '@/components/ui/button';
import { Checkbox } from '@/components/ui/checkbox';
import type { CreateCompatProfileDto, ImageCompatRequestDto } from '../networkAccess';

export const RELAXATION_LABELS: Record<
  'writable-rootfs' | 'root-user' | 'file-ownership-caps',
  string
> = {
  'writable-rootfs': 'Allow it to write to its own files',
  'root-user': "Allow it to run as the container's administrator account",
  'file-ownership-caps': 'Allow it to change file ownership inside its container',
};

interface CompatibilityModeCardProps {
  request: ImageCompatRequestDto;
  onApply: (profile: CreateCompatProfileDto) => Promise<unknown>;
  onDismiss?: () => void;
  disabled?: boolean;
}

export function CompatibilityModeCard({
  request,
  onApply,
  onDismiss,
  disabled = false,
}: CompatibilityModeCardProps) {
  const [writableRootfs, setWritableRootfs] = React.useState(true);
  const [rootUser, setRootUser] = React.useState(true);
  const [fileOwnershipCaps, setFileOwnershipCaps] = React.useState(true);
  const [submitting, setSubmitting] = React.useState(false);

  const connectorName = request.connectorDisplayName || 'Connector';

  const handleApply = async () => {
    setSubmitting(true);
    try {
      const relaxations: ('writable-rootfs' | 'root-user' | 'file-ownership-caps')[] = [];
      if (writableRootfs) relaxations.push('writable-rootfs');
      if (rootUser) relaxations.push('root-user');
      if (fileOwnershipCaps) relaxations.push('file-ownership-caps');

      await onApply({
        connectorId: request.connectorId,
        connectorDisplayName: request.connectorDisplayName,
        relaxations,
      });
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <article
      data-testid={`compat-card-${request.imageDigest}`}
      className="rounded-xl border border-border bg-card p-4 shadow-sm space-y-4"
    >
      <div className="space-y-1">
        <h3 className="font-semibold text-base">
          {connectorName} uses a container image that Kautuka doesn't control. It needs relaxed
          settings to run. Network access control still applies.
        </h3>
        <p className="text-xs text-muted-foreground font-mono break-all">{request.imageDigest}</p>
      </div>

      <div className="rounded-lg bg-muted/40 p-3 space-y-2 border border-border/50">
        <p className="text-xs font-semibold text-muted-foreground uppercase tracking-wider">
          Relaxed settings for this image
        </p>
        <div className="space-y-2">
          <div className="flex items-center space-x-2">
            <Checkbox
              id={`compat-write-${request.imageDigest}`}
              checked={writableRootfs}
              onChange={(e) => setWritableRootfs(e.target.checked)}
              disabled={disabled || submitting}
            />
            <label
              htmlFor={`compat-write-${request.imageDigest}`}
              className="text-sm cursor-pointer"
            >
              {RELAXATION_LABELS['writable-rootfs']}
            </label>
          </div>

          <div className="flex items-center space-x-2">
            <Checkbox
              id={`compat-root-${request.imageDigest}`}
              checked={rootUser}
              onChange={(e) => setRootUser(e.target.checked)}
              disabled={disabled || submitting}
            />
            <label
              htmlFor={`compat-root-${request.imageDigest}`}
              className="text-sm cursor-pointer"
            >
              {RELAXATION_LABELS['root-user']}
            </label>
          </div>

          <div className="flex items-center space-x-2">
            <Checkbox
              id={`compat-caps-${request.imageDigest}`}
              checked={fileOwnershipCaps}
              onChange={(e) => setFileOwnershipCaps(e.target.checked)}
              disabled={disabled || submitting}
            />
            <label
              htmlFor={`compat-caps-${request.imageDigest}`}
              className="text-sm cursor-pointer"
            >
              {RELAXATION_LABELS['file-ownership-caps']}
            </label>
          </div>
        </div>
      </div>

      <div className="flex flex-wrap items-center gap-2 pt-1">
        <Button
          type="button"
          variant="default"
          disabled={disabled || submitting}
          onClick={() => void handleApply()}
        >
          Use compatibility mode for this image
        </Button>
        <Button
          type="button"
          variant="outline"
          disabled={disabled || submitting}
          onClick={onDismiss}
        >
          Keep as is
        </Button>
      </div>
    </article>
  );
}
