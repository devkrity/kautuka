import { cn } from '@/lib/utils';
import React from 'react';

export function CatalogIcon({
  children,
  className,
}: {
  children: React.ReactNode;
  className?: string;
}) {
  return (
    <div
      className={cn(
        'flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-muted',
        className
      )}
    >
      {children}
    </div>
  );
}

export function CatalogCard({
  icon,
  title,
  badges,
  description,
  meta,
  actions,
  footer,
  children,
  className,
}: {
  icon?: React.ReactNode;
  title: React.ReactNode;
  badges?: React.ReactNode;
  description?: React.ReactNode;
  meta?: React.ReactNode;
  actions?: React.ReactNode;
  footer?: React.ReactNode;
  children?: React.ReactNode;
  className?: string;
}) {
  return (
    <div
      className={cn(
        'relative flex flex-col gap-3 rounded-xl border border-border bg-card p-4 shadow-sm',
        className
      )}
    >
      <div className="flex items-start gap-3">
        {icon}
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-2">
            <span className="truncate text-sm font-semibold">{title}</span>
            {badges}
          </div>
          {description ? (
            <p className="mt-0.5 line-clamp-2 text-xs text-muted-foreground">{description}</p>
          ) : null}
          {meta}
        </div>
        {actions ? <div className="flex shrink-0 gap-1">{actions}</div> : null}
      </div>
      {children}
      {footer ? (
        <div className="flex items-center justify-between border-t border-border pt-1">{footer}</div>
      ) : null}
    </div>
  );
}
