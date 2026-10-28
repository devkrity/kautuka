/**
 * @vitest-environment jsdom
 */
import { HitlWorkspaceSummary } from '@/features/approvals';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { ReactNode } from 'react';
import { describe, expect, it, vi } from 'vitest';

vi.mock('@/features/approvals/api/queries', () => ({
  useHitlStats: () => ({
    data: { windowDays: 30, since: '', items: [] },
    isLoading: false,
    isError: false,
  }),
  useHitlDeniedCalls: () => ({ data: [], isLoading: false }),
}));

vi.mock('react-router-dom', async () => {
  const actual = await vi.importActual<typeof import('react-router-dom')>('react-router-dom');
  return {
    ...actual,
    Link: ({ children, to }: { children: ReactNode; to: string }) => <a href={to}>{children}</a>,
  };
});

function renderWithQuery(node: ReactNode) {
  const queryClient = new QueryClient({
    defaultOptions: { queries: { retry: false } },
  });
  return render(<QueryClientProvider client={queryClient}>{node}</QueryClientProvider>);
}

describe('HitlWorkspaceSummary', () => {
  it('keeps a chrome button visible when there are no approval rows yet', () => {
    renderWithQuery(<HitlWorkspaceSummary agentId="agent-1" onConfigure={() => undefined} />);
    expect(screen.getByRole('button', { name: /Stats/i })).toBeInTheDocument();
  });

  it('opens Configure Agent from the compact summary', async () => {
    const onConfigure = vi.fn();
    renderWithQuery(<HitlWorkspaceSummary agentId="agent-1" compact onConfigure={onConfigure} />);

    await userEvent.click(screen.getByRole('button', { name: 'Stats' }));
    await userEvent.click(await screen.findByRole('button', { name: 'Open Configure Agent' }));

    expect(onConfigure).toHaveBeenCalledOnce();
  });
});
