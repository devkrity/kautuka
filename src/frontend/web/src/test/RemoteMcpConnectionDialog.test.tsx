import { fireEvent, screen, waitFor } from '@testing-library/dom';
import { render } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { RemoteMcpConnectionDialog } from '@/features/connectors/components/RemoteMcpConnectionDialog';
import {
  cancelRemoteMcpDraft,
  createRemoteMcpDraft,
  fetchConnectorCredentialStatus,
  saveRemoteMcpDraft,
  testRemoteMcpDraft,
} from '@/features/connectors/api/connectors';
import { connectorSchema } from '@/features/connectors/schemas/connectorSchema';

vi.mock('@/features/connectors/api/connectors', () => ({
  cancelRemoteMcpDraft: vi.fn(),
  createRemoteMcpDraft: vi.fn(),
  saveRemoteMcpDraft: vi.fn(),
  testRemoteMcpDraft: vi.fn(),
  fetchConnectorCredentialStatus: vi.fn(),
}));
const saved = connectorSchema.parse({
  id: 'mcp-example-com',
  name: 'mcp.example.com',
  transport: 'http',
  httpUrl: 'https://mcp.example.com/mcp',
});

function enter() {
  fireEvent.change(screen.getByLabelText('Server URL'), {
    target: { value: 'https://mcp.example.com/mcp' },
  });
  fireEvent.change(screen.getByLabelText('Authentication'), { target: { value: 'bearer' } });
  fireEvent.change(screen.getByLabelText('Bearer token (required)'), {
    target: { value: 'synthetic-owner-key' },
  });
}

describe('RemoteMcpConnectionDialog credential custody', () => {
  beforeEach(() => {
    vi.resetAllMocks();
    vi.mocked(createRemoteMcpDraft).mockResolvedValue({
      draftId: 'draft-1',
      expiresAt: '2026-11-05T12:00:00Z',
    });
    vi.mocked(testRemoteMcpDraft).mockResolvedValue({
      success: true,
      message: 'Connected.',
      toolCount: 1,
      toolNames: ['search'],
      toolDescriptions: {},
      packageChecked: null,
    });
    vi.mocked(saveRemoteMcpDraft).mockResolvedValue(saved);
    vi.mocked(cancelRemoteMcpDraft).mockResolvedValue(undefined);
  });

  it('sends new values only to credential administration, then tests and saves by opaque reference', async () => {
    const onSaved = vi.fn();
    render(<RemoteMcpConnectionDialog onSaved={onSaved} onClose={vi.fn()} />);
    enter();
    fireEvent.click(screen.getByRole('button', { name: 'Test connection' }));
    await waitFor(() => expect(testRemoteMcpDraft).toHaveBeenCalledWith('draft-1'));
    const request = vi.mocked(createRemoteMcpDraft).mock.calls[0][0];
    expect(request.entries).toEqual({ 'api-key': 'synthetic-owner-key' });
    expect(JSON.stringify(request.candidate)).not.toContain('synthetic-owner-key');
    expect(request.removeKeys).toEqual([]);
    fireEvent.click(await screen.findByRole('button', { name: 'Save' }));
    await waitFor(() => expect(onSaved).toHaveBeenCalledWith(saved));
    expect(createRemoteMcpDraft).toHaveBeenCalledOnce();
    expect(saveRemoteMcpDraft).toHaveBeenCalledWith('draft-1');
    expect(cancelRemoteMcpDraft).not.toHaveBeenCalled();
  });

  it('discards a tested candidate after edits and uses a fresh reference', async () => {
    vi.mocked(createRemoteMcpDraft)
      .mockResolvedValueOnce({ draftId: 'draft-1', expiresAt: 'later' })
      .mockResolvedValueOnce({ draftId: 'draft-2', expiresAt: 'later' });
    render(<RemoteMcpConnectionDialog onSaved={vi.fn()} onClose={vi.fn()} />);
    enter();
    fireEvent.click(screen.getByRole('button', { name: 'Test connection' }));
    await waitFor(() => expect(testRemoteMcpDraft).toHaveBeenCalledWith('draft-1'));
    await screen.findByRole('status');
    fireEvent.change(screen.getByLabelText('Name'), { target: { value: 'Edited after test' } });
    expect(cancelRemoteMcpDraft).toHaveBeenCalledWith('draft-1');
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(saveRemoteMcpDraft).toHaveBeenCalledWith('draft-2'));
  });

  it('retries a partial save using the same reference and retains entered values', async () => {
    vi.mocked(saveRemoteMcpDraft)
      .mockRejectedValueOnce(new Error('Synthetic save failure'))
      .mockResolvedValueOnce(saved);
    render(<RemoteMcpConnectionDialog onSaved={vi.fn()} onClose={vi.fn()} />);
    enter();
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    await screen.findByRole('alert');
    expect(screen.getByLabelText('Bearer token (required)')).toHaveValue('synthetic-owner-key');
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(saveRemoteMcpDraft).toHaveBeenCalledTimes(2));
    expect(vi.mocked(saveRemoteMcpDraft).mock.calls).toEqual([['draft-1'], ['draft-1']]);
    expect(createRemoteMcpDraft).toHaveBeenCalledOnce();
  });

  it('attempts cancellation when an unsaved dialog is closed', async () => {
    const onClose = vi.fn();
    render(<RemoteMcpConnectionDialog onSaved={vi.fn()} onClose={onClose} />);
    enter();
    fireEvent.click(screen.getByRole('button', { name: 'Test connection' }));
    await screen.findByRole('status');
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(cancelRemoteMcpDraft).toHaveBeenCalledWith('draft-1');
    expect(onClose).toHaveBeenCalledOnce();
  });

  it('keeps entered values and the tested draft on outside pointer down, then cancels explicitly', async () => {
    const onClose = vi.fn();
    render(<RemoteMcpConnectionDialog onSaved={vi.fn()} onClose={onClose} />);
    enter();
    fireEvent.click(screen.getByRole('button', { name: 'Test connection' }));
    await screen.findByRole('status');
    fireEvent.pointerDown(document.body, { button: 0, pointerType: 'mouse' });
    expect(onClose).not.toHaveBeenCalled();
    expect(cancelRemoteMcpDraft).not.toHaveBeenCalled();
    expect(screen.getByLabelText('Server URL')).toHaveValue('https://mcp.example.com/mcp');
    expect(screen.getByLabelText('Bearer token (required)')).toHaveValue('synthetic-owner-key');
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(cancelRemoteMcpDraft).toHaveBeenCalledExactlyOnceWith('draft-1');
    expect(onClose).toHaveBeenCalledOnce();
  });

  it('does not dismiss while credential status is loading when clicking outside', () => {
    vi.mocked(fetchConnectorCredentialStatus).mockReturnValue(new Promise(() => {}));
    const onClose = vi.fn();
    render(<RemoteMcpConnectionDialog connector={saved} onSaved={vi.fn()} onClose={onClose} />);
    fireEvent.pointerDown(document.body, { button: 0, pointerType: 'mouse' });
    expect(onClose).not.toHaveBeenCalled();
    expect(screen.getByText('Checking saved credential status…')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(onClose).toHaveBeenCalledOnce();
  });

  it('recreates an expired opaque reference only after an explicit retry', async () => {
    vi.mocked(createRemoteMcpDraft)
      .mockResolvedValueOnce({ draftId: 'draft-1', expiresAt: 'earlier' })
      .mockResolvedValueOnce({ draftId: 'draft-2', expiresAt: 'later' });
    vi.mocked(saveRemoteMcpDraft)
      .mockRejectedValueOnce({ response: { status: 404 } })
      .mockResolvedValueOnce(saved);
    render(<RemoteMcpConnectionDialog onSaved={vi.fn()} onClose={vi.fn()} />);
    enter();
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    await screen.findByRole('alert');
    expect(createRemoteMcpDraft).toHaveBeenCalledOnce();
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(saveRemoteMcpDraft).toHaveBeenCalledWith('draft-2'));
    expect(createRemoteMcpDraft).toHaveBeenCalledTimes(2);
  });
});
