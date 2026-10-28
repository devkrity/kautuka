/**
 * @vitest-environment jsdom
 */
import { render, screen } from '@testing-library/react';
import type { ReactNode } from 'react';
import { MemoryRouter } from 'react-router-dom';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const mocks = vi.hoisted(() => ({
  approvalsLoading: true,
  pendingApprovals: [] as Array<Record<string, unknown>>,
  instance: {
    id: 'run-1',
    status: 'paused',
    currentNodeId: null,
    stopRequested: false,
    envelope: { title: 'Paused workflow', supplier: 'Acme Supplies', amount: 425 },
    envelopeRevision: 1,
    envelopeHash: 'hash-1',
    snapshot: {
      name: 'Paused workflow',
      nodes: [] as Array<Record<string, unknown>>,
      inputSchema: { type: 'object', properties: {} },
      workingSchema: { type: 'object', properties: {} },
    },
    steps: [],
    failureCode: null,
    error: null,
  },
}));

vi.mock('@/features/workflows/api/queries', () => ({
  useStopWorkflowInstance: () => ({ mutateAsync: vi.fn(), isPending: false }),
  useWorkflow: () => ({ data: undefined }),
  useWorkflowInstance: () => ({ data: mocks.instance, isLoading: false, error: null }),
  useWorkflowJournal: () => ({ data: [], isLoading: false, error: null }),
}));
vi.mock('@/features/approvals', () => ({
  usePendingApprovals: () => ({
    data: mocks.pendingApprovals,
    isLoading: mocks.approvalsLoading,
    error: null,
  }),
  useResolveApproval: () => ({ mutateAsync: vi.fn(), isPending: false }),
}));
vi.mock('@/features/settings', () => ({
  useAgents: () => ({ data: [] }),
  apiError: () => 'Error',
}));
vi.mock('@/features/workflows/narad/WorkflowNaradProvider', () => ({
  WorkflowNaradProvider: ({ children }: { children: ReactNode }) => children,
  useWorkflowLiveState: () => null,
  useWorkflowNaradAttachError: () => null,
}));
vi.mock('@/features/workflows/components/WorkflowDiagram', () => ({
  WorkflowDiagram: () => null,
}));
vi.mock('@/features/workflows/components/WorkflowRunTimeline', () => ({
  WorkflowRunTimeline: () => null,
}));
vi.mock('@/features/workflows/components/WorkflowStepLedger', () => ({
  WorkflowStepLedger: ({ stepNumber, node }: { stepNumber: number; node: { nodeId: string } }) => (
    <li>{`Step ${stepNumber}: ${node.nodeId}`}</li>
  ),
}));

import { WorkflowInstanceView } from '@/features/workflows/components/WorkflowInstanceView';

describe('WorkflowInstanceView paused review state', () => {
  beforeEach(() => {
    mocks.approvalsLoading = true;
    mocks.pendingApprovals = [];
    mocks.instance.status = 'paused';
  });

  it('shows Stop run while looking for a review without claiming the allowance is exhausted', () => {
    render(
      <MemoryRouter>
        <WorkflowInstanceView workflowId="workflow-1" instanceId="run-1" />
      </MemoryRouter>
    );

    expect(screen.getByText('Finding the approval…')).toBeInTheDocument();
    expect(screen.getAllByRole('button', { name: 'Stop run' }).length).toBeGreaterThan(0);
    expect(screen.getByText('Paused', { selector: '.badge' })).toBeInTheDocument();
    expect(screen.queryByText('No change requests left.')).toBeNull();
  });

  it('labels a paused run as Paused after confirming there is no open review', () => {
    mocks.approvalsLoading = false;
    render(
      <MemoryRouter>
        <WorkflowInstanceView workflowId="workflow-1" instanceId="run-1" />
      </MemoryRouter>
    );

    expect(screen.getByText('Paused', { selector: '.badge' })).toBeInTheDocument();
    expect(screen.queryByText('Waiting for you')).toBeNull();
  });

  it('shows run values without requiring the owner to open Details', () => {
    mocks.approvalsLoading = false;
    render(
      <MemoryRouter>
        <WorkflowInstanceView workflowId="workflow-1" instanceId="run-1" />
      </MemoryRouter>
    );

    expect(screen.getByText('All run values')).toBeVisible();
    expect(screen.getByText('Supplier')).toBeVisible();
    expect(screen.getByText('Acme Supplies')).toBeVisible();
    expect(screen.getByText('Amount')).toBeVisible();
    expect(screen.getByText('425')).toBeVisible();
    expect(screen.queryByText('Canonical envelope')).toBeNull();
  });

  it('uses the review wording when a pending workflow review belongs to this run', () => {
    mocks.approvalsLoading = false;
    mocks.pendingApprovals = [
      { id: 'review-1', instanceId: 'run-1', kind: 'workflow_review', actions: [] },
    ];
    render(
      <MemoryRouter>
        <WorkflowInstanceView workflowId="workflow-1" instanceId="run-1" />
      </MemoryRouter>
    );

    expect(screen.getByText('Waiting for your review.')).toBeInTheDocument();
    expect(screen.getByText('Waiting for your review', { selector: '.badge' })).toBeInTheDocument();
    expect(screen.queryByText('Paused', { selector: '.badge' })).toBeNull();
  });

  it.each([
    ['finished', 'Finished'],
    ['error', 'Failed'],
  ])('shows the %s status on its run page', (status, label) => {
    mocks.approvalsLoading = false;
    mocks.instance.status = status;
    render(
      <MemoryRouter>
        <WorkflowInstanceView workflowId="workflow-1" instanceId="run-1" />
      </MemoryRouter>
    );

    expect(screen.getByText(label, { selector: '.badge' })).toBeInTheDocument();
    expect(screen.queryByText('Waiting for your review')).toBeNull();
    expect(screen.queryByText('Paused', { selector: '.badge' })).toBeNull();
  });

  it('lists run steps in the same order as their displayed numbers', () => {
    const node = (
      nodeId: string,
      defaultGoTo: string | null = null,
      switchCases: Array<Record<string, string>> = []
    ) => ({
      nodeId,
      agentId: 'agent',
      promptTemplate: '',
      outputKind: 'schema',
      outputSchema: { type: 'object', properties: {} },
      maps: [],
      sideEffects: 'none',
      memoryWrites: false,
      requiresReview: false,
      maxRetries: 0,
      switchCases,
      defaultGoTo,
    });
    mocks.approvalsLoading = false;
    mocks.instance.snapshot = {
      ...mocks.instance.snapshot,
      nodes: [
        node('first', 'otherwise', [{ pointer: '/decision', op: 'eq', value: 'yes', goTo: 'if' }]),
        node('otherwise'),
        node('if'),
      ],
    };

    const { container } = render(
      <MemoryRouter>
        <WorkflowInstanceView workflowId="workflow-1" instanceId="run-1" />
      </MemoryRouter>
    );

    expect(
      Array.from(container.querySelectorAll('.workspace-panel ol > li')).map(
        (item) => item.textContent
      )
    ).toEqual(['Step 1: first', 'Step 2: if', 'Step 3: otherwise']);
  });
});
