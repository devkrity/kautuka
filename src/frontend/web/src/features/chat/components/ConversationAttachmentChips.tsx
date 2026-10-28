import { fetchConversationAssetBlob } from '../api/sessions';
import type { ConversationAssetPayload } from '../schemas/sessionSchema';
import { cn } from '@/lib/utils';
import { Download, FileText } from 'lucide-react';
import React from 'react';

const triggerBrowserDownload = (blob: Blob, fileName: string) => {
  const url = URL.createObjectURL(blob);
  const link = document.createElement('a');
  link.href = url;
  link.download = fileName;
  link.rel = 'noreferrer';
  document.body.append(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(url);
};

export const ConversationAttachmentChips: React.FC<{
  attachmentIds: readonly string[];
  assetsById: ReadonlyMap<string, ConversationAssetPayload>;
  className?: string;
}> = ({ attachmentIds, assetsById, className }) => {
  if (attachmentIds.length === 0) {
    return null;
  }

  return (
    <div className={cn('mt-3 flex flex-wrap gap-2', className)}>
      {attachmentIds.map((assetId) => {
        const asset = assetsById.get(assetId);
        const fileName = asset?.fileName ?? assetId;
        return (
          <button
            key={assetId}
            type="button"
            className="inline-flex max-w-full items-center gap-2 rounded-full border border-border/70 bg-background px-3 py-1 text-xs hover:bg-muted"
            onClick={() => {
              if (!asset) {
                return;
              }
              void fetchConversationAssetBlob(asset.downloadUrl).then((blob) =>
                triggerBrowserDownload(blob, asset.fileName)
              );
            }}
          >
            <FileText className="h-3.5 w-3.5 shrink-0 text-muted-foreground" />
            <span className="truncate">{fileName}</span>
            <Download className="h-3 w-3 shrink-0 text-muted-foreground" />
          </button>
        );
      })}
    </div>
  );
};
