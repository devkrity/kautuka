import React from 'react';
import type { ComposerAsset, ConversationAsset } from '../types/chatTypes';

export type ComposerAttachmentUpload = (
  file: File,
  origin: 'userUpload' | 'clipboardPaste'
) => Promise<ConversationAsset | null>;

const nextLocalId = (): string => {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }
  return `local-${Date.now()}-${Math.random().toString(16).slice(2)}`;
};

export function useComposerAttachments(upload: ComposerAttachmentUpload) {
  const [items, setItems] = React.useState<ComposerAsset[]>([]);
  const uploadRef = React.useRef(upload);
  uploadRef.current = upload;

  const addFiles = React.useCallback((files: File[], origin: 'userUpload' | 'clipboardPaste') => {
    const staged: ComposerAsset[] = files.map((file) => ({
      localId: nextLocalId(),
      fileName: file.name,
      contentType: file.type || 'application/octet-stream',
      sizeBytes: file.size,
      origin,
      status: 'uploading',
    }));

    setItems((current) => [...current, ...staged]);

    for (const [index, file] of files.entries()) {
      const localId = staged[index]!.localId;
      void uploadRef
        .current(file, origin)
        .then((asset) => {
          setItems((current) =>
            current.map((item) =>
              item.localId === localId
                ? {
                    ...item,
                    status: asset ? 'ready' : 'error',
                    asset: asset ?? undefined,
                    errorMessage: asset ? undefined : 'Upload failed',
                  }
                : item
            )
          );
        })
        .catch((error: unknown) => {
          setItems((current) =>
            current.map((item) =>
              item.localId === localId
                ? {
                    ...item,
                    status: 'error',
                    errorMessage: error instanceof Error ? error.message : 'Upload failed',
                  }
                : item
            )
          );
        });
    }
  }, []);

  const remove = React.useCallback((localId: string) => {
    setItems((current) => current.filter((item) => item.localId !== localId));
  }, []);

  const clear = React.useCallback(() => setItems([]), []);

  const readyAssets = items
    .filter((item) => item.status === 'ready' && item.asset)
    .map((item) => item.asset!);
  const hasUploading = items.some((item) => item.status === 'uploading');
  const hasError = items.some((item) => item.status === 'error');

  return { items, addFiles, remove, clear, readyAssets, hasUploading, hasError };
}
