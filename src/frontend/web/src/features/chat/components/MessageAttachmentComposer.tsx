import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';
import { LoaderCircle, Paperclip, X } from 'lucide-react';
import React from 'react';
import type { ComposerAsset } from '../types/chatTypes';

export type MessageAttachmentComposerProps = Readonly<{
  items: readonly ComposerAsset[];
  disabled?: boolean;
  onAddFiles: (files: File[], origin: 'userUpload' | 'clipboardPaste') => void;
  onRemove: (localId: string) => void;
  children: React.ReactNode;
  className?: string;
}>;

const formatSize = (value: number): string => {
  if (value < 1024) {
    return `${value} B`;
  }
  if (value < 1024 * 1024) {
    return `${(value / 1024).toFixed(1)} KB`;
  }
  return `${(value / (1024 * 1024)).toFixed(1)} MB`;
};

export const MessageAttachmentComposer: React.FC<MessageAttachmentComposerProps> = ({
  items,
  disabled,
  onAddFiles,
  onRemove,
  children,
  className,
}) => {
  const fileInputRef = React.useRef<HTMLInputElement | null>(null);
  const [isDragActive, setIsDragActive] = React.useState(false);
  const dragDepthRef = React.useRef(0);

  const addFromList = React.useCallback(
    (fileList: FileList | File[] | null, origin: 'userUpload' | 'clipboardPaste') => {
      if (!fileList || disabled) {
        return;
      }
      const files = Array.from(fileList).filter((file) => file.size > 0);
      if (files.length > 0) {
        onAddFiles(files, origin);
      }
    },
    [disabled, onAddFiles]
  );

  return (
    <div
      className={cn(
        'flex flex-col gap-2 rounded-3xl border border-border bg-card px-4 py-3 shadow-sm',
        isDragActive && 'border-primary/50 bg-primary/5',
        className
      )}
      onDragEnter={(event) => {
        event.preventDefault();
        dragDepthRef.current += 1;
        setIsDragActive(true);
      }}
      onDragOver={(event) => event.preventDefault()}
      onDragLeave={(event) => {
        event.preventDefault();
        dragDepthRef.current = Math.max(0, dragDepthRef.current - 1);
        if (dragDepthRef.current === 0) {
          setIsDragActive(false);
        }
      }}
      onDrop={(event) => {
        event.preventDefault();
        dragDepthRef.current = 0;
        setIsDragActive(false);
        addFromList(event.dataTransfer.files, 'userUpload');
      }}
      onPaste={(event) => {
        const files = Array.from(event.clipboardData.items)
          .filter((item) => item.kind === 'file')
          .map((item) => item.getAsFile())
          .filter((file): file is File => file instanceof File);
        if (files.length > 0) {
          event.preventDefault();
          addFromList(files, 'clipboardPaste');
        }
      }}
    >
      {items.length > 0 ? (
        <div className="flex flex-wrap gap-2">
          {items.map((item) => (
            <div
              key={item.localId}
              className="flex max-w-full items-center gap-2 rounded-full border border-border/70 bg-background px-3 py-1 text-xs"
            >
              {item.status === 'uploading' ? (
                <LoaderCircle className="h-3 w-3 shrink-0 animate-spin text-muted-foreground" />
              ) : null}
              <span className="truncate font-medium">{item.fileName}</span>
              <span className="shrink-0 text-muted-foreground">{formatSize(item.sizeBytes)}</span>
              {item.status === 'error' ? (
                <span className="max-w-40 truncate text-destructive" title={item.errorMessage}>
                  {item.errorMessage ?? 'Upload failed'}
                </span>
              ) : null}
              <button
                type="button"
                className="rounded-full p-0.5 text-muted-foreground hover:bg-muted hover:text-foreground"
                aria-label={`Remove ${item.fileName}`}
                disabled={disabled}
                onClick={() => onRemove(item.localId)}
              >
                <X className="h-3 w-3" />
              </button>
            </div>
          ))}
        </div>
      ) : null}

      <div className="flex items-end gap-2">
        <input
          ref={fileInputRef}
          type="file"
          multiple
          className="hidden"
          disabled={disabled}
          onChange={(event) => {
            addFromList(event.target.files, 'userUpload');
            event.target.value = '';
          }}
        />
        <Button
          type="button"
          variant="ghost"
          size="icon"
          disabled={disabled}
          aria-label="Attach files"
          onClick={() => fileInputRef.current?.click()}
        >
          <Paperclip className="h-4 w-4" />
        </Button>
        {children}
      </div>
    </div>
  );
};
