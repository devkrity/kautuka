/**
 * @vitest-environment jsdom
 */
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const mocks = vi.hoisted(() => ({
  pendingApprovals: [] as Array<Record<string, unknown>>,
  approvalQuery: vi.fn(),
  instances: [
    {
      id: 'run-1',
      workflowId: 'workflow-1',
      status: 'paused',
      envelope: { title: 'Invoice run' },
      snapshot: { name: 'Invoice workflow' },
    },
  ],
}));

vi.mock('@/features/approvals', () => ({
  usePendingApprovals: (...args: unknown[]) => {
    mocks.approvalQuery(...args);
    return { data: mocks.pendingApprovals };
  },
}));
vi.mock('@/features/settings', () => ({ useAgents: () => ({ data: [], isLoading: false }) }));
vi.mock('@/features/connectors', () => ({ useConnectors: () => ({ data: [] }) }));
vi.mock('@/features/workflows/api/queries', () => ({
  useCreateWorkflow: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useWorkflows: () => ({ data: [], isLoading: false, error: null }),
  useWorkflowInstances: () => ({ data: mocks.instances, error: null }),
}));
vi.mock('@/features/workflows/components/WorkflowSettingsDialog', () => ({
  WorkflowSettingsDialog: () => null,
}));

import { WorkflowCatalogView } from '@/features/workflows/components/WorkflowCatalogView';

describe('WorkflowCatalogView paused run labels', () => {
  beforeEach(() => {
    mocks.pendingApprovals = [];
    mocks.approvalQuery.mockClear();
  });

  it('uses review wording only for the run with a pending workflow review', () => {
    mocks.pendingApprovals = [
      { id: 'review-1', instanceId: 'run-1', kind: 'workflow_review' },
      { id: 'review-2', instanceId: 'other-run', kind: 'workflow_review' },
    ];

    render(
      <MemoryRouter>
        <WorkflowCatalogView />
      </MemoryRouter>
    );

    expect(screen.getByText('Waiting for your review')).toBeInTheDocument();
    expect(screen.queryByText('Waiting for you')).toBeNull();
  });

  it('uses Paused when no review belongs to the run', () => {
    mocks.pendingApprovals = [{ id: 'review-2', instanceId: 'other-run', kind: 'workflow_review' }];

    render(
      <MemoryRouter>
        <WorkflowCatalogView />
      </MemoryRouter>
    );

    expect(screen.getByText('Paused')).toBeInTheDocument();
    expect(screen.queryByText('Waiting for your review')).toBeNull();
  });

  it('loads review status in the compact workflow list', () => {
    mocks.pendingApprovals = [{ id: 'review-1', instanceId: 'run-1', kind: 'workflow_review' }];
    render(
      <MemoryRouter>
        <WorkflowCatalogView compact />
      </MemoryRouter>
    );

    expect(mocks.approvalQuery).toHaveBeenCalledWith('mine');
    expect(screen.queryByText('Waiting for your review')).toBeNull();
  });
});
