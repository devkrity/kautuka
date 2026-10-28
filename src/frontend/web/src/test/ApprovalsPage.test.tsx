import React from 'react';
import { render } from '@testing-library/react';
import { screen, waitFor } from '@testing-library/dom';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router-dom';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { vi, describe, it, expect, beforeEach } from 'vitest';
import { ApprovalsPage } from '@/pages/ApprovalsPage';

const { resolveMock, useApprovalsPageMock } = vi.hoisted(() => ({
  resolveMock: vi.fn(),
  useApprovalsPageMock: vi.fn(),
}));

vi.mock('@/features/approvals', async () => {
  const actual =
    await vi.importActual<typeof import('@/features/approvals')>('@/features/approvals');
  return {
    ...actual,
    useApprovalsPage: useApprovalsPageMock,
  };
});

const pendingApproval = {
  id: 'req-1',
  agentId: 'agent-1',
  agentName: 'Desk Assistant',
  toolCallId: 'tc-1',
  toolName: 'run_shell',
  toolType: 'mcp',
  argumentsJson: '{"cmd":"ls"}',
  requestedAt: new Date().toISOString(),
  expiresAt: new Date(Date.now() + 300_000).toISOString(),
  runId: 'run-1',
  decision: undefined,
  actions: [
    { label: 'Approve', value: 'approve', style: 'primary' },
    { label: 'Deny', value: 'deny', style: 'danger' },
    { label: 'Stop Run', value: 'stop_run', style: 'danger', requiresRunId: true },
  ],
};

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter>
        <ApprovalsPage />
      </MemoryRouter>
    </QueryClientProvider>
  );
}

describe('ApprovalsPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    useApprovalsPageMock.mockReturnValue({
      tab: 'pending',
      scope: 'mine',
      pending: [pendingApproval],
      resolving: {},
      history: [],
      historyHasMore: false,
      historyLoading: false,
      loading: false,
      error: null,
      isAdmin: true,
      onChangeTab: vi.fn(),
      onChangeScope: vi.fn(),
      resolveApproval: resolveMock,
      loadMore: vi.fn(),
    });
  });

  it('renders pending approval entries', async () => {
    renderPage();
    await waitFor(() => expect(screen.getAllByText('run_shell').length).toBeGreaterThan(0));
    expect(screen.getByTestId('pending-approval')).toHaveAttribute(
      'data-approval-request-id',
      'req-1'
    );
    expect(screen.getByRole('button', { name: /^approve$/i })).toHaveAttribute(
      'data-approval-action',
      'approve'
    );
    expect(
      ['Approve', 'Deny', 'Stop Run'].map((name) =>
        screen.getByRole('button', { name })
      )
    ).toHaveLength(3);
  });

  it('shows outer run context for nested approvals', async () => {
    useApprovalsPageMock.mockReturnValue({
      tab: 'pending',
      scope: 'mine',
      pending: [
        {
          ...pendingApproval,
          outerSessionId: 'session-outer',
          outerAgentId: 'agent-outer',
          outerAgentName: 'Research Agent',
        },
      ],
      resolving: {},
      history: [],
      historyHasMore: false,
      historyLoading: false,
      loading: false,
      error: null,
      isAdmin: true,
      onChangeTab: vi.fn(),
      onChangeScope: vi.fn(),
      resolveApproval: resolveMock,
      loadMore: vi.fn(),
    });

    renderPage();

    await waitFor(() => expect(screen.getByText('Outer run')).toBeInTheDocument());
    expect(screen.getByText('Part of: Research Agent')).toBeInTheDocument();
    expect(screen.getByRole('link', { name: /view outer session/i })).toHaveAttribute(
      'href',
      '/workspace/agent-outer?sessionId=session-outer'
    );
  });

  it('renders backend-provided agent display names instead of raw agent ids', async () => {
    renderPage();

    await waitFor(() => expect(screen.getAllByText('run_shell').length).toBeGreaterThan(0));
    expect(screen.getByText('Agent: Desk Assistant')).toBeInTheDocument();
    expect(screen.queryByText('Agent: agent-1')).not.toBeInTheDocument();
  });

  it('calls resolve with approved=true when approve button clicked', async () => {
    resolveMock.mockResolvedValue({});
    renderPage();
    await waitFor(() => expect(screen.getAllByText('run_shell').length).toBeGreaterThan(0));
    const approveButtons = screen.getAllByRole('button', { name: /^approve$/i });
    await userEvent.click(approveButtons[0]);
    await waitFor(() =>
      expect(resolveMock).toHaveBeenCalledWith('req-1', 'approve', undefined)
    );
  });

  it('calls resolve with approved=false when deny button clicked', async () => {
    resolveMock.mockResolvedValue({});
    renderPage();
    await waitFor(() => expect(screen.getAllByText('run_shell').length).toBeGreaterThan(0));
    const rejectButtons = screen.getAllByRole('button', { name: /^deny$/i });
    await userEvent.click(rejectButtons[0]);
    await waitFor(() =>
      expect(resolveMock).toHaveBeenCalledWith('req-1', 'deny', undefined)
    );
  });

  it('passes stop_run as a distinct terminal action', async () => {
    resolveMock.mockResolvedValue({});
    renderPage();
    await waitFor(() => expect(screen.getAllByText('run_shell').length).toBeGreaterThan(0));

    await userEvent.click(screen.getByRole('button', { name: 'Stop Run' }));
    expect(resolveMock).toHaveBeenLastCalledWith('req-1', 'stop_run', undefined);
  });

  it('sends the owner note as the reason for a workflow change request', async () => {
    useApprovalsPageMock.mockReturnValue({
      tab: 'pending',
      scope: 'mine',
      pending: [{
        ...pendingApproval,
        kind: 'workflow_review',
        toolType: 'workflow_review',
        workflowId: 'wf-1',
        instanceId: 'instance-1',
        nodeId: 'node-1',
        remainingChangeRequests: 2,
        actions: [
          ...pendingApproval.actions,
          { label: 'Request changes', value: 'request_changes', style: 'secondary' },
        ],
      }],
      resolving: {},
      history: [],
      historyHasMore: false,
      historyLoading: false,
      loading: false,
      error: null,
      isAdmin: true,
      onChangeTab: vi.fn(),
      onChangeScope: vi.fn(),
      resolveApproval: resolveMock,
      loadMore: vi.fn(),
    });
    const user = userEvent.setup();
    renderPage();

    await user.click(screen.getByRole('button', { name: 'Request changes' }));
    await user.type(screen.getByLabelText('What should change?'), 'Include the invoice date.');
    await user.click(screen.getByRole('button', { name: 'Send' }));

    expect(resolveMock).toHaveBeenCalledWith('req-1', 'request_changes', 'Include the invoice date.');
  });

  it('hides Request changes after both change requests have been used', async () => {
    useApprovalsPageMock.mockReturnValue({
      tab: 'pending',
      scope: 'mine',
      pending: [{
        ...pendingApproval,
        kind: 'workflow_review',
        toolType: 'workflow_review',
        workflowId: 'wf-1',
        instanceId: 'instance-1',
        nodeId: 'node-1',
        actions: [
          { label: 'Approve', value: 'approve', style: 'primary' },
          { label: 'Stop', value: 'stop_run', style: 'danger' },
        ],
      }],
      resolving: {},
      history: [],
      historyHasMore: false,
      historyLoading: false,
      loading: false,
      error: null,
      isAdmin: true,
      onChangeTab: vi.fn(),
      onChangeScope: vi.fn(),
      resolveApproval: resolveMock,
      loadMore: vi.fn(),
    });

    renderPage();

    await waitFor(() => expect(screen.getByTestId('pending-approval')).toBeInTheDocument());
    expect(screen.queryByRole('button', { name: 'Request changes' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Approve' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Stop' })).toBeInTheDocument();
  });
});
