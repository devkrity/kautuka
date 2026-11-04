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

  it('refreshes the visible pending approvals without a streaming connection', async () => {
    const intervalSpy = vi.spyOn(window, 'setInterval');
    const { result, unmount } = renderHook(() => useApprovalsPage());
    await waitFor(() => expect(result.current.loading).toBe(false));
    fetchPendingApprovalsMock.mockResolvedValue([{ id: 'new-approval' }]);
    const refresh = intervalSpy.mock.calls.find((call) => call[1] === 5000)?.[0];
    expect(typeof refresh).toBe('function');
    await act(async () => { (refresh as () => void)(); });
    await waitFor(() => expect(result.current.pending).toEqual([{ id: 'new-approval' }]));
    unmount();
    intervalSpy.mockRestore();
  });

  it('refreshes the first history page without discarding loaded pages', async () => {
    const intervalSpy = vi.spyOn(window, 'setInterval');
    let updated = false;
    fetchApprovalHistoryMock.mockImplementation(async (_scope: string, limit: number, offset: number) =>
      Array.from({ length: limit }, (_, index) => ({ id: updated && offset === 0 && index === 0 ? 'new' : `item-${offset + index}` })));
    const { result, unmount } = renderHook(() => useApprovalsPage());
    await waitFor(() => expect(result.current.loading).toBe(false));
    act(() => result.current.onChangeTab('history'));
    await waitFor(() => expect(result.current.history).toHaveLength(50));
    act(() => result.current.loadMore());
    await waitFor(() => expect(result.current.history).toHaveLength(100));
    act(() => result.current.loadMore());
    await waitFor(() => expect(result.current.history).toHaveLength(150));
    updated = true;
    const refresh = [...intervalSpy.mock.calls].reverse().find((call) => call[1] === 5000)?.[0];
    await act(async () => { (refresh as () => void)(); });
    await waitFor(() => expect(result.current.history[0]?.id).toBe('new'));
    expect(result.current.history).toHaveLength(150);
    expect(fetchApprovalHistoryMock).toHaveBeenCalledWith('mine', 50, 0);
    expect(result.current.historyHasMore).toBe(true);
    unmount();
    intervalSpy.mockRestore();
  });

  it('refreshes history that was initially empty', async () => {
    const intervalSpy = vi.spyOn(window, 'setInterval');
    const { result, unmount } = renderHook(() => useApprovalsPage());
    await waitFor(() => expect(result.current.loading).toBe(false));
    act(() => result.current.onChangeTab('history'));
    await waitFor(() => expect(result.current.historyLoading).toBe(false));
    fetchApprovalHistoryMock.mockResolvedValue([{ id: 'new-decision' }]);
    const refresh = [...intervalSpy.mock.calls].reverse().find((call) => call[1] === 5000)?.[0];
    await act(async () => { (refresh as () => void)(); });
    await waitFor(() => expect(result.current.history).toEqual([{ id: 'new-decision' }]));
    unmount();
    intervalSpy.mockRestore();
  });

  it('keeps the previously loaded last history item when a new decision arrives', async () => {
    const intervalSpy = vi.spyOn(window, 'setInterval');
    fetchApprovalHistoryMock.mockResolvedValue([{ id: 'older' }]);
    const { result, unmount } = renderHook(() => useApprovalsPage());
    await waitFor(() => expect(result.current.loading).toBe(false));
    act(() => result.current.onChangeTab('history'));
    await waitFor(() => expect(result.current.history).toEqual([{ id: 'older' }]));
    fetchApprovalHistoryMock.mockResolvedValue([{ id: 'new' }, { id: 'older' }]);
    const refresh = [...intervalSpy.mock.calls].reverse().find((call) => call[1] === 5000)?.[0];
    await act(async () => { (refresh as () => void)(); });
    await waitFor(() => expect(result.current.history).toEqual([{ id: 'new' }, { id: 'older' }]));
    expect(result.current.historyHasMore).toBe(false);
    unmount();
    intervalSpy.mockRestore();
  });

  it('does not let a stale all-scope request replace the mine list', async () => {
    let finishAll!: (items: { id: string }[]) => void;
    const allResponse = new Promise<{ id: string }[]>((resolve) => { finishAll = resolve; });
    fetchPendingApprovalsMock.mockImplementation((scope: string) =>
      scope === 'all' ? allResponse : Promise.resolve([{ id: 'mine' }]));
    const { result } = renderHook(() => useApprovalsPage());
    await waitFor(() => expect(result.current.loading).toBe(false));
    act(() => { void result.current.onChangeScope('all'); });
    await act(async () => { await result.current.onChangeScope('mine'); });
    await act(async () => { finishAll([{ id: 'other-user' }]); await allResponse; });
    expect(result.current.pending).toEqual([{ id: 'mine' }]);
  });

  it('does not let a stale all-scope history request replace mine', async () => {
    let finishAll!: (items: { id: string }[]) => void;
    const allResponse = new Promise<{ id: string }[]>((resolve) => { finishAll = resolve; });
    fetchApprovalHistoryMock.mockImplementation((scope: string) =>
      scope === 'all' ? allResponse : Promise.resolve([{ id: 'mine-history' }]));
    const { result } = renderHook(() => useApprovalsPage());
    await waitFor(() => expect(result.current.loading).toBe(false));
    act(() => result.current.onChangeTab('history'));
    await waitFor(() => expect(result.current.history).toEqual([{ id: 'mine-history' }]));
    await act(async () => { await result.current.onChangeScope('all'); });
    await waitFor(() => expect(fetchApprovalHistoryMock).toHaveBeenCalledWith('all', 50, 0));
    await act(async () => { await result.current.onChangeScope('mine'); });
    await waitFor(() => expect(result.current.history).toEqual([{ id: 'mine-history' }]));
    await act(async () => { finishAll([{ id: 'other-history' }]); await allResponse; });
    expect(result.current.history).toEqual([{ id: 'mine-history' }]);
  });

  it('does not restore an approval from a poll started before its resolution', async () => {
    const intervalSpy = vi.spyOn(window, 'setInterval');
    fetchPendingApprovalsMock.mockResolvedValue([{ id: 'approval-1' }]);
    const { result, unmount } = renderHook(() => useApprovalsPage());
    await waitFor(() => expect(result.current.pending).toEqual([{ id: 'approval-1' }]));
    let finishPoll!: (items: { id: string }[]) => void;
    const oldPoll = new Promise<{ id: string }[]>((resolve) => { finishPoll = resolve; });
    fetchPendingApprovalsMock.mockReturnValueOnce(oldPoll);
    const refresh = [...intervalSpy.mock.calls].reverse().find((call) => call[1] === 5000)?.[0];
    act(() => { (refresh as () => void)(); });
    await act(async () => { await result.current.resolveApproval('approval-1', 'approve'); });
    await act(async () => { finishPoll([{ id: 'approval-1' }]); await oldPoll; });
    expect(result.current.pending).toEqual([]);
    unmount();
    intervalSpy.mockRestore();
  });

  it('does not overlap approval refreshes', async () => {
    const intervalSpy = vi.spyOn(window, 'setInterval');
    const { result, unmount } = renderHook(() => useApprovalsPage());
    await waitFor(() => expect(result.current.loading).toBe(false));
    let finishPoll!: (items: { id: string }[]) => void;
    const poll = new Promise<{ id: string }[]>((resolve) => { finishPoll = resolve; });
    fetchPendingApprovalsMock.mockClear();
    fetchPendingApprovalsMock.mockReturnValueOnce(poll);
    const refresh = [...intervalSpy.mock.calls].reverse().find((call) => call[1] === 5000)?.[0];
    act(() => { (refresh as () => void)(); (refresh as () => void)(); });
    expect(fetchPendingApprovalsMock).toHaveBeenCalledTimes(1);
    await act(async () => { finishPoll([]); await poll; });
    unmount();
    intervalSpy.mockRestore();
  });
});
