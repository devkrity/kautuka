/**
 * @vitest-environment jsdom
 */
import React from 'react';
import { render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { describe, expect, it, vi } from 'vitest';
import { PendingApprovalCard } from '@/features/approvals/components/PendingApprovalCard';
import type { Approval } from '@/features/approvals/schemas/approvalSchema';

const { getWorkflowInstanceMock } = vi.hoisted(() => ({ getWorkflowInstanceMock: vi.fn() }));

vi.mock('@/features/workflows', async () => {
  const actual =
    await vi.importActual<typeof import('@/features/workflows')>('@/features/workflows');
  return { ...actual, getWorkflowInstance: getWorkflowInstanceMock };
});

describe('PendingApprovalCard workflow review', () => {
  it('shows the workflow, step, and labeled results without exposing review JSON', async () => {
    getWorkflowInstanceMock.mockResolvedValue({
      workflowId: 'wf-private-id',
      snapshot: {
        name: 'Invoice checks',
        inputSchema: {},
        workingSchema: { properties: { amount: { title: 'Total due' } } },
        nodes: [
          {
            nodeId: 'root',
            outputSchema: { properties: { amount: { title: 'Invoice total' } } },
            maps: [],
            promptTemplate: 'Check the invoice.',
            switchCases: [
              { pointer: '/decision', op: 'eq', value: 'review', goTo: 'node-private-id' },
            ],
            defaultGoTo: 'otherwise',
          },
          {
            nodeId: 'leaf',
            outputSchema: { properties: {} },
            maps: [],
            promptTemplate: 'Finish.',
            switchCases: [],
            defaultGoTo: null,
          },
          {
            nodeId: 'otherwise',
            outputSchema: { properties: {} },
            maps: [],
            promptTemplate: 'Use the other path.',
            switchCases: [],
            defaultGoTo: 'leaf',
          },
          {
            nodeId: 'node-private-id',
            outputSchema: { properties: { amount: { title: 'Invoice total' } } },
            maps: [{ from: '/amount', to: '/amount' }],
            promptTemplate: 'Review the invoice.',
            switchCases: [],
            defaultGoTo: null,
          },
        ],
      },
      steps: [],
    });
    const item: Approval = {
      id: 'approval-1',
      toolCallId: 'run-1:node-private-id:1',
      toolName: 'workflow_review',
      toolType: 'workflow_review',
      kind: 'workflow_review',
      workflowId: 'wf-private-id',
      instanceId: 'instance-private-id',
      nodeId: 'node-private-id',
      remainingChangeRequests: 2,
      agentName: 'Accounts assistant',
      argumentsJson: JSON.stringify({
        instanceId: 'instance-private-id',
        nodeId: 'node-private-id',
        proposed: { amount: 520 },
      }),
      requestedAt: new Date().toISOString(),
      checkpointResumable: false,
      actions: [
        {
          label: 'Request changes',
          value: 'request_changes',
          style: 'default',
          requiresRunId: false,
        },
      ],
    };

    render(
      <MemoryRouter>
        <PendingApprovalCard item={item} busy={false} onResolve={vi.fn()} />
      </MemoryRouter>
    );

    expect(await screen.findByText(/Invoice checks/)).toBeInTheDocument();
    expect(screen.getByText(/Step 2 \(Accounts assistant\)/)).toBeInTheDocument();
    expect(screen.getByText('Total due')).toBeInTheDocument();
    expect(screen.getByText('520')).toBeInTheDocument();
    expect(screen.getByText('2 more change requests left.')).toBeInTheDocument();
    expect(screen.queryByText('WORKFLOW_REVIEW')).not.toBeInTheDocument();
    expect(screen.queryByText(/instance-private-id|node-private-id/)).not.toBeInTheDocument();
  });
});
