import { useApprovalsPage } from '@/features/approvals/hooks/useApprovalsPage';
import { act, renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const {
  fetchApprovalHistoryMock,
  fetchPendingApprovalsMock,
  getProfileMock,
  resolveApprovalMock,
} = vi.hoisted(() => ({
  fetchApprovalHistoryMock: vi.fn(),
  fetchPendingApprovalsMock: vi.fn(),
  getProfileMock: vi.fn(),
  resolveApprovalMock: vi.fn(),
}));

vi.mock('@/features/approvals', () => ({
  fetchApprovalHistory: fetchApprovalHistoryMock,
  fetchPendingApprovals: fetchPendingApprovalsMock,
  resolveApproval: resolveApprovalMock,
}));

vi.mock('@/features/auth', () => ({
  getProfile: getProfileMock,
}));

describe('useApprovalsPage', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    getProfileMock.mockResolvedValue({ id: 'user-1', role: ['ad', 'min'].join('') });
    fetchPendingApprovalsMock.mockResolvedValue([]);
    fetchApprovalHistoryMock.mockResolvedValue([]);
    resolveApprovalMock.mockResolvedValue({ resolved: true, decision: 'approved' });
  });

  it('resolves a single approval without trustThisRun/approve-all', async () => {
    const { result } = renderHook(() => useApprovalsPage());
    await waitFor(() => expect(result.current.loading).toBe(false));

    await act(async () => {
      await result.current.resolveApproval('approval-1', 'approve');
    });

    expect(resolveApprovalMock).toHaveBeenCalledWith(
      'approval-1',
      'approve',
      undefined
    );
    expect(result.current.error).toBeNull();
  });

  it('clears an expired start-run and says the approval expired', async () => {
    fetchPendingApprovalsMock.mockResolvedValue([{ id: 'approval-1' }]);
    resolveApprovalMock.mockResolvedValue({ resolved: false, decision: 'expired' });
    const { result } = renderHook(() => useApprovalsPage());
    await waitFor(() => expect(result.current.loading).toBe(false));

    await act(async () => {
      await result.current.resolveApproval('approval-1', 'approve');
    });

    expect(result.current.pending).toEqual([]);
    expect(result.current.error).toBe('This approval expired before the run could start.');
  });
});
