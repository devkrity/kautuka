import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { useEffect, useRef } from 'react';
import { MemoryRouter, Route, Routes } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const mocks = vi.hoisted(() => ({
  workflows: [
    { id: 'wf-1', name: 'Review', nodes: [] },
    { id: 'wf-2', name: 'Summarize', nodes: [] },
  ],
  instances: [
    { id: 'run-1', workflowId: 'wf-1', createdAt: '2026-10-23T10:00:00Z', status: 'running' },
  ],
  navigate: vi.fn(),
}));

vi.mock('@/features/settings', () => ({ useAgents: () => ({ data: [{ id: 'agent-1' }] }) }));
vi.mock('@/features/approvals', () => ({ usePendingApprovals: () => ({ data: [] }) }));
vi.mock('@/features/workflows', async (importOriginal) => {
  const actual = await importOriginal<typeof import('@/features/workflows')>();
  return {
    ...actual,
    useWorkflows: (archived = false) => ({ data: archived ? [] : mocks.workflows }),
    useWorkflowInstances: (id?: string) => ({ data: id === 'wf-1' ? mocks.instances : [] }),
    useCreateWorkflow: () => ({ isPending: false, mutateAsync: vi.fn() }),
    useArchiveWorkflow: () => ({ mutateAsync: vi.fn() }),
    useRestoreWorkflow: () => ({ mutateAsync: vi.fn() }),
    useDeleteWorkflow: () => ({ mutateAsync: vi.fn() }),
    createStepDraft: vi.fn(() => ({ agentId: 'agent-1' })),
    toSaveWorkflowRequest: vi.fn(() => ({})),
    WorkflowCatalogView: () => <div>Workflow templates</div>,
    WorkflowDefinitionView: ({
      focusRun,
      onRunFocused,
      workflowsListOpen,
      onToggleWorkflowsList,
      readOnlyOverview,
    }: {
      focusRun?: boolean;
      onRunFocused?: () => void;
      workflowsListOpen?: boolean;
      onToggleWorkflowsList?: () => void;
      readOnlyOverview?: boolean;
    }) => {
      const ref = useRef<HTMLInputElement>(null);
      useEffect(() => {
        if (focusRun) {
          ref.current?.focus();
          onRunFocused?.();
        }
      }, [focusRun, onRunFocused]);
      return (
        <div>
          {readOnlyOverview ? (
            <button type="button">Edit workflow</button>
          ) : (
            <button type="button" onClick={onToggleWorkflowsList}>
              {workflowsListOpen ? 'Hide workflows' : 'Show workflows'}
            </button>
          )}
          <label htmlFor="run-form">Run form</label>
          <input id="run-form" ref={ref} />
        </div>
      );
    },
    WorkflowInstanceView: ({ instanceId }: { instanceId: string }) => (
      <div>Instance view {instanceId}</div>
    ),
    WorkflowStatusBadge: ({ status }: { status: string }) => <span>{status}</span>,
  };
});

import { WorkflowsPage } from '@/pages/WorkflowsPage';

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/workflows" element={<WorkflowsPage />} />
        <Route path="/workflows/:workflowId" element={<WorkflowsPage />} />
        <Route path="/workflows/:workflowId/edit" element={<WorkflowsPage />} />
        <Route path="/workflows/:workflowId/instances/:instanceId" element={<WorkflowsPage />} />
      </Routes>
    </MemoryRouter>
  );
}

describe('WorkflowsPage workspace layout', () => {
  beforeEach(() => vi.clearAllMocks());

  it('opens the overview with the list visible and runs available', () => {
    renderAt('/workflows/wf-1');
    expect(screen.getByRole('button', { name: 'Edit workflow' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Show workflows' })).not.toBeInTheDocument();
    expect(screen.getByRole('navigation', { name: 'Workflows and runs' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /Review\d+ steps/ })).toHaveAttribute(
      'aria-expanded',
      'true'
    );
    expect(screen.getByText('Runs')).toBeInTheDocument();
    expect(screen.getByText('running')).toBeInTheDocument();
    expect(document.querySelector('a[href="/workflows/wf-1/instances/run-1"]')).toBeInTheDocument();
  });

  it('opens an instance view when a run is selected', async () => {
    renderAt('/workflows/wf-1');
    fireEvent.click(document.querySelector('a[href="/workflows/wf-1/instances/run-1"]')!);
    await waitFor(() => expect(screen.getByText('Instance view run-1')).toBeInTheDocument());
  });

  it('focuses the run form after New run is selected', async () => {
    renderAt('/workflows/wf-1');
    fireEvent.click(screen.getByRole('button', { name: 'New run' }));
    await waitFor(() => expect(screen.getByLabelText('Run form')).toHaveFocus());
  });

  it('shows empty states when there are no workflows', () => {
    mocks.workflows = [];
    renderAt('/workflows');
    expect(screen.getByText('No workflows yet.')).toBeInTheDocument();
    expect(screen.getByText('Workflow templates')).toBeInTheDocument();
  });
});
