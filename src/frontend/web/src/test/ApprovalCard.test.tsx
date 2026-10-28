import React from 'react';
import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ApprovalCard } from '@/features/chat/components/ApprovalCard';
import type { PendingApproval } from '@/features/chat/types/chatTypes';

vi.mock('@/features/auth', () => ({
  useCurrentUser: () => ({
    data: { id: 'user-1', username: 'admin', displayName: 'Admin' },
  }),
}));

const pending: PendingApproval = {
  requestId: 'approval-1',
  toolCallId: 'call-1',
  toolName: 'invoke_agent_roller',
  argumentsJson: '{"message":"roll a d111"}',
  status: 'pending',
  runId: 'run-1',
};

describe('ApprovalCard chat-v2 harness contracts', () => {
  it('exposes approve, deny, and stop-run HITL actions only', () => {
    render(
      <ApprovalCard
        approval={pending}
        onDecide={vi.fn()}
        onStopRun={vi.fn()}
      />
    );

    const panel = screen.getByTestId('hitl-approval');
    expect(panel).toHaveAttribute('data-approval-request-id', 'approval-1');
    expect(panel).toHaveAttribute('data-tool-name', 'invoke_agent_roller');
    expect(panel.querySelector('[data-approval-action="approve"]')).not.toBeNull();
    expect(panel.querySelector('[data-approval-action="deny"]')).not.toBeNull();
    expect(panel.querySelector('[data-approval-action="stop_run"]')).not.toBeNull();
    expect(panel.querySelector('[data-approval-action="approve_all"]')).toBeNull();
    expect(panel.querySelector('[data-approval-action="cancel"]')).toBeNull();
  });
});
