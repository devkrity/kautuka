import { cn } from '@/lib/utils';
import React from 'react';

export function StatePill({
  active,
  disabled,
  onClick,
  title,
  children,
}: {
  active: boolean;
  disabled?: boolean;
  onClick?: () => void;
  title?: string;
  children: React.ReactNode;
}) {
  const className = cn(
    'inline-flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-xs transition-colors',
    active
      ? 'border-done/50 bg-done/10 text-done'
      : 'border-border bg-muted text-muted-foreground',
    onClick && !disabled && (active ? 'hover:bg-done/20' : 'hover:border-primary/50'),
    disabled && 'cursor-not-allowed opacity-50'
  );

  if (!onClick) {
    return (
      <span className={className} title={title}>
        {children}
      </span>
    );
  }

  return (
    <button type="button" className={className} disabled={disabled} onClick={onClick} title={title}>
      {children}
    </button>
  );
}
