import React from 'react';
import { describe, expect, it, vi, beforeEach } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { SessionQueue } from '@/features/chat/components/SessionQueue';
import apiClient from '@/lib/apiClient';

vi.mock('@/lib/apiClient', () => ({ default: { get: vi.fn(), post: vi.fn() } }));

describe('Gateway session queue', () => {
  beforeEach(() => vi.resetAllMocks());
  function show() {
    const client = new QueryClient({
      defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
    });
    return render(
      <QueryClientProvider client={client}>
        <SessionQueue agentId="agent-1" sessionId="session-1" />
      </QueryClientProvider>
    );
  }
  it('renders server order and removes by stable identity', async () => {
    vi.mocked(apiClient.get).mockResolvedValue({
      data: {
        messages: [
          { id: 'turn-446', order: 10, content: 'roll d446' },
          { id: 'turn-777', order: 11, content: 'roll d777' },
        ],
        throughOrder: 11,
      },
    });
    vi.mocked(apiClient.post).mockResolvedValue({ data: { removed: 1 } });
    show();
    expect(await screen.findByText('2 queued')).toBeInTheDocument();
    expect(screen.getByText('roll d446')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Hide queued messages' }));
    expect(screen.queryByText('roll d446')).not.toBeInTheDocument();
    const summary = screen.getByRole('button', { name: 'Queued 2' });
    expect(summary).toHaveAttribute('aria-expanded', 'false');
    fireEvent.click(summary);
    expect(screen.getByText('roll d446')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Remove waiting message 2' }));
    await waitFor(() =>
      expect(apiClient.post).toHaveBeenCalledWith(
        '/narad/v1/agents/agent-1/sessions/session-1/queue/remove',
        { messageId: 'turn-777' }
      )
    );
  });
  it('clears only through the displayed server snapshot', async () => {
    vi.mocked(apiClient.get).mockResolvedValue({
      data: { messages: [{ id: 'turn-446', order: 10, content: 'roll d446' }], throughOrder: 10 },
    });
    vi.mocked(apiClient.post).mockResolvedValue({ data: { removed: 1 } });
    show();
    fireEvent.click(await screen.findByRole('button', { name: 'Clear queue' }));
    await waitFor(() =>
      expect(apiClient.post).toHaveBeenCalledWith(
        '/narad/v1/agents/agent-1/sessions/session-1/queue/remove',
        { throughOrder: 10 }
      )
    );
  });

  it('keeps the maintenance reason visible when the owner hides queued messages', async () => {
    const maintenanceMessage = 'Kautuka couldn’t renew its channel connection. New messages are queued while Kautuka retries.';
    vi.mocked(apiClient.get).mockResolvedValue({
      data: { messages: [{ id: 'turn-446', order: 10, content: 'roll d446' }], throughOrder: 10, maintenanceMessage },
    });
    show();
    fireEvent.click(await screen.findByRole('button', { name: 'Hide queued messages' }));
    expect(screen.getByRole('status')).toHaveTextContent(maintenanceMessage);
    expect(screen.queryByText('roll d446')).not.toBeInTheDocument();
  });

  it('explains the pause before the owner sends a message', async () => {
    const maintenanceMessage = 'Kautuka is waiting for the active helper to finish before renewing its channel connection.';
    vi.mocked(apiClient.get).mockResolvedValue({ data: { messages: [], throughOrder: 0, maintenanceMessage } });
    show();
    expect(await screen.findByRole('status')).toHaveTextContent(maintenanceMessage);
    expect(screen.queryByRole('button', { name: /queued/i })).not.toBeInTheDocument();
  });
});
