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
});
