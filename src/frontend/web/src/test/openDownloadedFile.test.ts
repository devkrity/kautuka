import { openDownloadedFile } from '@/features/agent-files';
import { describe, expect, it, vi } from 'vitest';

describe('openDownloadedFile', () => {
  it('renders html in a sandboxed frame instead of a same-origin document', async () => {
    const frame = document.createElement('iframe');
    const setAttribute = vi.spyOn(frame, 'setAttribute');
    const popup = {
      opener: window,
      document: {
        body: document.createElement('body'),
        createElement: vi.fn(() => frame),
      },
    };
    vi.spyOn(window, 'open').mockReturnValue(popup as unknown as Window);
    const createObjectUrl = vi.spyOn(URL, 'createObjectURL');

    await openDownloadedFile(new Blob(['<script>alert(1)</script>'], { type: 'text/html' }), 'message.html');

    expect(window.open).toHaveBeenCalledWith('', '_blank');
    expect(popup.opener).toBeNull();
    expect(setAttribute).toHaveBeenCalledWith('sandbox', '');
    expect(frame.srcdoc).toBe('<script>alert(1)</script>');
    expect(createObjectUrl).not.toHaveBeenCalled();
  });

  it('still opens an image in a new tab', async () => {
    const open = vi.spyOn(window, 'open').mockReturnValue(null);
    vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:image');

    await openDownloadedFile(new Blob(['png'], { type: 'image/png' }), 'photo.png');

    expect(open).toHaveBeenCalledWith('blob:image', '_blank', 'noopener');
  });
});
