import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import { ActivityLedger } from '@/features/chat/components/ActivityLedger';
import type { ActivityEntry } from '@/features/chat/types/chatTypes';

describe('ActivityLedger', () => {
  it('renders every projected row without identity-collapse (shared toolCallId ok)', () => {
    const entries: ActivityEntry[] = [
      {
        id: 'apr-1',
        sequence: 1,
        kind: 'approval',
        status: 'approved',
        actionKind: 'agent',
        name: 'invoke_agent_roller',
        toolCallId: 'call-1',
        approvalRequestId: 'apr-1',
        requestedAt: '2026-08-05T00:00:01Z',
        decidedAt: '2026-08-05T00:00:02Z',
      },
      {
        id: 'call-1',
        sequence: 2,
        kind: 'action',
        status: 'started',
        actionKind: 'agent',
        name: 'invoke_agent_roller',
        toolCallId: 'call-1',
        startedAt: '2026-08-05T00:00:03Z',
      },
    ];

    render(<ActivityLedger activityEntries={entries} isLive rootRunLive />);

    const rows = screen.getAllByTestId('activity-ledger-row');
    expect(rows).toHaveLength(2);
    expect(rows.some((row) => row.getAttribute('data-activity-kind') === 'approval')).toBe(true);
    expect(rows.some((row) => row.getAttribute('data-activity-kind') === 'action')).toBe(true);
    expect(screen.getByText(/1 running/i)).toBeTruthy();
  });

  it('spins Working from root run liveness after approve when no started/pending rows', () => {
    // Gate-before-start: interrupt.resolved→approved is terminal on the row; tool.call.started
    // may not have landedyet. Root subtree still running must keep the turn feel live.
    const entries: ActivityEntry[] = [
      {
        id: 'apr-1',
        sequence: 1,
        kind: 'approval',
        status: 'approved',
        actionKind: 'agent',
        name: 'invoke_agent_roller',
        toolCallId: 'call-1',
        approvalRequestId: 'apr-1',
      },
    ];

    render(<ActivityLedger activityEntries={entries} isLive rootRunLive />);

    expect(screen.getByTestId('activity-ledger-live-spinner')).toBeTruthy();
    expect(screen.getByText(/Working · 1 approval/i)).toBeTruthy();
    expect(screen.getByTestId('activity-ledger-row').getAttribute('data-status')).toBe('approved');
  });

  it('renders nested parent and child actions without collapse', () => {
    const entries: ActivityEntry[] = [
      {
        id: 'apr-1',
        sequence: 1,
        kind: 'approval',
        status: 'approved',
        actionKind: 'agent',
        name: 'invoke_agent_roller',
        toolCallId: 'parent-1',
        approvalRequestId: 'apr-1',
      },
      {
        id: 'parent-1',
        sequence: 2,
        kind: 'action',
        status: 'started',
        actionKind: 'agent',
        name: 'invoke_agent_roller',
        toolCallId: 'parent-1',
      },
      {
        id: 'child-1',
        sequence: 3,
        kind: 'action',
        status: 'started',
        actionKind: 'connector',
        name: 'roll_dice',
        toolCallId: 'child-1',
        parentToolCallId: 'parent-1',
      },
    ];

    render(<ActivityLedger activityEntries={entries} isLive rootRunLive />);

    const rows = screen.getAllByTestId('activity-ledger-row');
    expect(rows).toHaveLength(3);
    expect(screen.getByText(/2 running/i)).toBeTruthy();
  });

  it('renders agent turn rows with packed vs sequential tool counts', () => {
    const entries: ActivityEntry[] = [
      {
        id: 'agent-turn:run-1:1',
        sequence: 1,
        kind: 'turn',
        status: 'completed',
        actionKind: 'agent.turn',
        name: 'Agent turn 1',
        resultJson: '3 tool calls',
        startedAt: '2026-08-05T00:00:01Z',
        completedAt: '2026-08-05T00:00:02Z',
      },
      {
        id: 'agent-turn:run-1:2',
        sequence: 2,
        kind: 'turn',
        status: 'started',
        actionKind: 'agent.turn',
        name: 'Agent turn 2',
        startedAt: '2026-08-05T00:00:03Z',
      },
    ];

    render(<ActivityLedger activityEntries={entries} isLive rootRunLive />);

    const rows = screen.getAllByTestId('activity-ledger-row');
    expect(rows).toHaveLength(2);
    expect(rows[0]?.getAttribute('data-activity-kind')).toBe('turn');
    expect(screen.getByText('Agent turn 1')).toBeTruthy();
    expect(screen.getByText('Agent turn 2')).toBeTruthy();
    expect(screen.getByText(/1 running/i)).toBeTruthy();
  });

  it('labels stopped approvals as Stopped, not Denied', () => {
    const entries: ActivityEntry[] = [
      {
        id: 'apr-1',
        sequence: 1,
        kind: 'approval',
        status: 'stopped',
        actionKind: 'connector',
        name: 'kautuka_web_fetch',
        toolCallId: 'call-1',
        approvalRequestId: 'apr-1',
      },
    ];

    render(<ActivityLedger activityEntries={entries} isLive rootRunLive />);

    expect(screen.getByText('Stopped')).toBeTruthy();
    expect(screen.queryByText('Denied')).toBeNull();
  });
});
