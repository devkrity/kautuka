import React, { useState, useCallback } from 'react';
import { Copy, Check } from 'lucide-react';

export const CopyButton: React.FC<{ text: string; align: 'start' | 'end' }> = ({ text, align }) => {
  const [copied, setCopied] = useState(false);
  const handleCopy = useCallback(async () => {
    await navigator.clipboard.writeText(text);
    setCopied(true);
    setTimeout(() => setCopied(false), 1500);
  }, [text]);
  return (
    <button
      type="button"
      onClick={handleCopy}
      title="Copy message"
      aria-label={copied ? 'Message copied' : 'Copy message'}
      className={`mt-1 flex items-center gap-1 rounded px-1 py-0.5 text-[11px] text-muted-foreground opacity-0 transition-opacity group-hover/msg:opacity-100 focus-visible:opacity-100 hover:text-foreground ${
        align === 'end' ? 'ml-auto' : ''
      }`}
    >
      {copied ? <Check className="h-3 w-3 text-done" /> : <Copy className="h-3 w-3" />}
      {copied ? 'Copied' : 'Copy'}
    </button>
  );
};
