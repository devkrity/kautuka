import React, { useState } from 'react';

export const ThinkingDisclosure: React.FC<{ thinking: string }> = ({ thinking }) => {
  const [open, setOpen] = useState(false);
  return (
    <div className="mb-2">
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        aria-expanded={open}
        className="flex items-center gap-1 text-[11px] text-muted-foreground transition-colors duration-200 hover:text-foreground"
      >
        <span className={`transition-transform duration-200 ease-out ${open ? 'rotate-90' : ''}`}>
          ▶
        </span>{' '}
        Thinking
      </button>
      {open && (
        <div
          className="motion-safe:reveal-up mt-1.5 max-h-64 overflow-y-auto rounded-lg border border-border/70 bg-muted/30 px-3 py-2 text-xs leading-relaxed text-foreground/80 whitespace-pre-wrap wrap-break-word"
          style={{ animationDuration: '240ms' }}
        >
          {thinking}
        </div>
      )}
    </div>
  );
};
