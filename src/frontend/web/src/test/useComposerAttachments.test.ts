/**
 * @vitest-environment jsdom
 */
import { act, renderHook, waitFor } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { useComposerAttachments } from '@/features/chat';
import type { ConversationAsset } from '@/features/chat';

describe('useComposerAttachments', () => {
  it('uploads files and exposes ready assets', async () => {
    const uploaded: ConversationAsset = {
      id: 'asset-1',
      fileName: 'notes.txt',
      contentType: 'text/plain',
      sizeBytes: 5,
      origin: 'userUpload',
      disposition: 'download',
      downloadUrl: '/api/sessions/s/assets/asset-1',
      createdAt: '2026-08-19T00:00:00Z',
    };
    const { result } = renderHook(() =>
      useComposerAttachments(async () => uploaded)
    );

    await act(async () => {
      result.current.addFiles([new File(['hello'], 'notes.txt', { type: 'text/plain' })], 'userUpload');
    });

    await waitFor(() => expect(result.current.readyAssets).toEqual([uploaded]));
    expect(result.current.hasUploading).toBe(false);
  });
});
