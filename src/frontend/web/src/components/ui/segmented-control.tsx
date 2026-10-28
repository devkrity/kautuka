import { cn } from '@/lib/utils';
import React from 'react';

export type SegmentTone = 'primary' | 'neutral' | 'done' | 'attention' | 'attention-soft' | 'muted' | 'info';

const selectedTone: Record<SegmentTone, string> = {
  primary: 'bg-primary-fill text-primary-foreground shadow-sm',
  neutral: 'bg-background text-foreground shadow-sm ring-1 ring-border/40',
  done: 'bg-done text-done-foreground shadow-sm',
  attention: 'bg-attention text-attention-foreground shadow-sm',
  'attention-soft': 'bg-attention/15 text-attention',
  muted: 'bg-muted text-muted-foreground',
  info: 'bg-primary/10 text-primary',
};

export function SegmentedControl<T extends string>({
  value,
  onChange,
  options,
  disabled = false,
  shape = 'rounded',
  size = 'sm',
  className,
}: {
  value: T;
  onChange: (value: T) => void;
  options: Array<{
    value: T;
    label: React.ReactNode;
    tone?: SegmentTone;
    disabled?: boolean;
    title?: string;
  }>;
  disabled?: boolean;
  shape?: 'rounded' | 'pill';
  size?: 'xs' | 'sm' | 'md';
  className?: string;
}) {
  return (
    <div
      className={cn(
        'inline-flex items-center gap-0.5 bg-muted p-0.5',
        shape === 'pill' ? 'rounded-full border border-border p-1' : 'rounded-lg',
        className
      )}
    >
      {options.map((option) => {
        const selected = option.value === value;
        const optionDisabled = disabled || option.disabled;
        return (
          <button
            key={option.value}
            type="button"
            title={option.title}
            disabled={optionDisabled}
            onClick={() => onChange(option.value)}
            className={cn(
              'font-medium transition-colors disabled:cursor-not-allowed disabled:opacity-50',
              shape === 'pill' ? 'rounded-full' : 'rounded-md',
              size === 'xs' && 'px-2.5 py-0.5 text-[11px]',
              size === 'sm' && 'px-2.5 py-1 text-xs',
              size === 'md' && 'px-4 py-1.5 text-sm',
              selected
                ? selectedTone[option.tone ?? 'primary']
                : 'text-muted-foreground hover:text-foreground'
            )}
          >
            {option.label}
          </button>
        );
      })}
    </div>
  );
}
