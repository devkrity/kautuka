/**
 * @vitest-environment jsdom
 */
import { MessageAttachmentComposer } from '@/features/chat';
import { fireEvent, render, screen } from '@testing-library/react';
import React from 'react';
import { describe, expect, it, vi } from 'vitest';

describe('MessageAttachmentComposer', () => {
  it('adds dropped files and can remove chips', () => {
    const onAddFiles = vi.fn();
    const onRemove = vi.fn();
    render(
      <MessageAttachmentComposer
        items={[
          {
            localId: 'local-1',
            fileName: 'notes.txt',
            contentType: 'text/plain',
            sizeBytes: 12,
            origin: 'userUpload',
            status: 'ready',
          },
        ]}
        onAddFiles={onAddFiles}
        onRemove={onRemove}
      >
        <textarea aria-label="Message" />
      </MessageAttachmentComposer>
    );

    expect(screen.getByText('notes.txt')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Remove notes.txt' }));
    expect(onRemove).toHaveBeenCalledWith('local-1');

    const file = new File(['hello'], 'drop.txt', { type: 'text/plain' });
    fireEvent.drop(screen.getByRole('textbox', { name: 'Message' }).parentElement!.parentElement!, {
      dataTransfer: { files: [file] },
    });
    expect(onAddFiles).toHaveBeenCalledWith([file], 'userUpload');
  });
});
