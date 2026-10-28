import React from 'react';
import { Button } from '@/components/ui/button';
import { Separator } from '@/components/ui/separator';
import { ChevronDown, Loader2, RefreshCw, RotateCcw, UploadCloud } from 'lucide-react';
import { cn } from '@/lib/utils';

interface UserVersionInfoProps {
  currentVersion?: string;
  canCheckForUpdates?: boolean;
  isCheckingForUpdates?: boolean;
  onCheckForUpdates?: () => void;
  updateTitle?: string;
  updateDescription?: string;
  updateTone?: 'neutral' | 'available' | 'busy' | 'error';
  availableVersion?: string;
  releaseChannel?: string;
  canApplyUpdate?: boolean;
  isApplyingUpdate?: boolean;
  onApplyUpdate?: () => void;
  canRollbackUpdate?: boolean;
  isRollingBackUpdate?: boolean;
  onRollbackUpdate?: () => void;
  className?: string;
}

export function UserVersionInfo({
  currentVersion,
  canCheckForUpdates = false,
  isCheckingForUpdates = false,
  onCheckForUpdates,
  updateTitle,
  updateDescription,
  updateTone = 'neutral',
  availableVersion,
  releaseChannel,
  canApplyUpdate = false,
  isApplyingUpdate = false,
  onApplyUpdate,
  canRollbackUpdate = false,
  isRollingBackUpdate = false,
  onRollbackUpdate,
  className,
}: Readonly<UserVersionInfoProps>) {
  const buildVersion = import.meta.env.VITE_KAUTUKA_VERSION || '0.1.20-dev';
  const buildSha = import.meta.env.VITE_KAUTUKA_SOURCE_SHA || 'local';
  if (!currentVersion && !(canCheckForUpdates && onCheckForUpdates)) {
    return null;
  }

  return (
    <div className={className}>
      <Separator className="my-2" />
      <div className="min-w-0 text-left text-[11px]">
        {updateTitle ? (
          <details className="group rounded-xl bg-muted/25 px-2.5 py-2 open:bg-muted/45">
            <summary className="flex cursor-pointer list-none items-center gap-2 font-medium text-foreground/80">
              <span
                className={cn(
                  'h-2 w-2 shrink-0 rounded-full bg-muted-foreground/50',
                  updateTone === 'available' && 'bg-attention',
                  updateTone === 'busy' && 'animate-pulse bg-primary',
                  updateTone === 'error' && 'bg-destructive'
                )}
              />
              <span className="min-w-0 flex-1 truncate">{updateTitle}</span>
              <ChevronDown className="h-3 w-3 shrink-0 transition-transform group-open:rotate-180" />
            </summary>
            <div className="mt-2 space-y-2 border-t border-border/50 pt-2 text-muted-foreground">
              {updateDescription ? <p className="leading-4">{updateDescription}</p> : null}
              <div className="space-y-0.5">
                {currentVersion ? <p>Current: {currentVersion}</p> : null}
                <p>Build: {buildVersion} ({buildSha})</p>
                {availableVersion ? <p>Available: {availableVersion}</p> : null}
                {releaseChannel ? <p>Track: {releaseChannel}</p> : null}
              </div>
              <div className="flex flex-wrap gap-1">
                {canApplyUpdate && onApplyUpdate ? (
                  <Button
                    type="button"
                    size="sm"
                    onClick={onApplyUpdate}
                    disabled={isApplyingUpdate}
                    className="h-7 px-2 text-[11px]"
                  >
                    {isApplyingUpdate ? (
                      <Loader2 className="mr-1 h-3 w-3 animate-spin" />
                    ) : (
                      <UploadCloud className="mr-1 h-3 w-3" />
                    )}
                    Apply
                  </Button>
                ) : null}
                {canRollbackUpdate && onRollbackUpdate ? (
                  <Button
                    type="button"
                    variant="outline"
                    size="sm"
                    onClick={onRollbackUpdate}
                    disabled={isRollingBackUpdate}
                    className="h-7 px-2 text-[11px]"
                  >
                    {isRollingBackUpdate ? (
                      <Loader2 className="mr-1 h-3 w-3 animate-spin" />
                    ) : (
                      <RotateCcw className="mr-1 h-3 w-3" />
                    )}
                    Roll back
                  </Button>
                ) : null}
              </div>
            </div>
          </details>
        ) : currentVersion ? (
          <p className="truncate text-muted-foreground">Version {currentVersion}</p>
        ) : null}
        {canCheckForUpdates && onCheckForUpdates ? (
          <Button
            type="button"
            variant="ghost"
            size="sm"
            onClick={onCheckForUpdates}
            className="mt-1 h-7 px-0 text-[11px] font-medium text-primary hover:bg-transparent hover:text-primary/80"
          >
            {isCheckingForUpdates ? (
              <Loader2 className="mr-1 h-3 w-3 animate-spin" />
            ) : (
              <RefreshCw className="mr-1 h-3 w-3" />
            )}
            Check for updates
          </Button>
        ) : null}
      </div>
    </div>
  );
}
