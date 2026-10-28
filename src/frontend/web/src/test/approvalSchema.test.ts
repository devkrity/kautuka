import { describe, expect, it, vi } from 'vitest';
import { approvalSchema, resolveApproval, toStoredApprovalDecision } from '@/features/approvals';

const resolveInterrupt = vi.hoisted(() => vi.fn());

vi.mock('@/lib/naradCommandClient', () => ({
  naradCommandClient: { resolveInterrupt },
}));

describe('approvalSchema', () => {
  it('accepts nullable checkpoint metadata returned by the backend', () => {
    const approval = approvalSchema.parse({
      id: 'approval-1',
      sessionId: 'session-1',
      runId: 'run-1',
      checkpointId: null,
      agentId: 'agent-1',
      agentName: 'My Assistant',
      userId: 'user-1',
      toolCallId: 'call-1',
      toolName: 'run_skill_script',
      toolType: 'skill',
      mcpServerId: null,
      argumentsJson: '{"script":"roll-die"}',
      requestedAt: '2026-06-06T08:52:18.0000000+00:00',
      decision: null,
      decidedBy: null,
      decidedAt: null,
      reason: null,
      expiresAt: null,
      actions: [
        { label: 'Approve', value: 'approve' },
        { label: 'Deny', value: 'deny' },
      ],
      outerSessionId: null,
      outerAgentId: null,
      outerAgentName: null,
      decidedByDisplayName: null,
      resolutionSource: null,
      callerToolCallId: null,
      callerToolName: null,
      checkpointIndex: null,
      checkpointBoundaryType: null,
      checkpointCreatedAt: null,
      checkpointResumable: null,
    });

    expect(approval.checkpointIndex).toBeUndefined();
    expect(approval.checkpointResumable).toBe(false);
  });

  it('accepts expired approvals as a first-class outcome', () => {
    const approval = approvalSchema.parse({
      id: 'apr-expired',
      toolCallId: 'call-expired',
      toolName: 'autopilot_pre_run',
      toolType: 'autopilot_prerun',
      requestedAt: '2026-06-07T00:00:00.000Z',
      decision: 'expired',
      decidedAt: '2026-06-07T00:05:00.000Z',
      expiresAt: '2026-06-07T00:05:00.000Z',
    });

    expect(approval.decision).toBe('expired');
  });

  it('maps a start-run response that omits the stored decision', async () => {
    resolveInterrupt.mockResolvedValueOnce({ status: 'accepted', decision: undefined });

    await expect(resolveApproval('apr-1', 'approve', 'approvals-pane')).resolves.toEqual({
      resolved: true,
      decision: 'approved',
    });
  });

  it('reports an expired start-run instead of a schema error', async () => {
    resolveInterrupt.mockResolvedValueOnce({ status: 'expired', decision: null, detail: 'Approval request has expired.' });

    await expect(resolveApproval('apr-1', 'approve')).resolves.toEqual({
      resolved: false,
      decision: 'expired',
    });
    expect(toStoredApprovalDecision('approve')).toBe('approved');
  });

  it('rejects unknown approval actions instead of rendering inert controls', () => {
    expect(() =>
      approvalSchema.parse({
        id: 'apr-unknown-action',
        toolCallId: 'call-1',
        toolName: 'run_shell',
        toolType: 'mcp',
        requestedAt: '2026-06-07T00:00:00.000Z',
        actions: [{ label: 'Maybe', value: 'maybe' }],
      })
    ).toThrow();
  });
});
