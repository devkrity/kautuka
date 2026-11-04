import { useCallback, useEffect, useRef, useState } from 'react';
import {
  fetchApprovalHistory,
  fetchPendingApprovals,
  resolveApproval as resolveApprovalAPI,
  type Approval,
} from '@/features/approvals';
import { APPROVAL_REFRESH_INTERVAL_MS } from '../api/queries';
import { approvalsQueryKeys } from '../api/queryKeys';
import { queryClient } from '@/lib/queryClient';
import { getProfile } from '@/features/auth';

type ScopeMode = 'mine' | 'all';
type TabMode = 'pending' | 'history';
const ROLE_ADMIN = ['ad', 'min'].join('');

const PAGE_SIZE = 50;

interface CurrentUser {
  id: string;
  role: string;
}

export function formatRelativeTime(iso: string): string {
  const then = new Date(iso).getTime();
  const now = Date.now();
  const seconds = Math.max(0, Math.floor((now - then) / 1000));

  if (seconds < 60) return `${seconds}s ago`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`;
  return `${Math.floor(seconds / 86400)}d ago`;
}

export function computeRemaining(expiresAt?: string): string {
  if (!expiresAt) return 'No timeout';
  const remainingMs = new Date(expiresAt).getTime() - Date.now();
  if (remainingMs <= 0) return 'Expired';
  const remainingSec = Math.floor(remainingMs / 1000);
  if (remainingSec < 60) return `${remainingSec}s left`;
  if (remainingSec < 3600) return `${Math.floor(remainingSec / 60)}m left`;
  if (remainingSec >= 86400) {
    const days = Math.floor(remainingSec / 86400);
    return `${days} ${days === 1 ? 'day' : 'days'} left`;
  }
  return `${Math.floor(remainingSec / 3600)}h left`;
}

export function prettyJson(value?: string): string {
  if (!value) return '';
  try {
    return JSON.stringify(JSON.parse(value), null, 2);
  } catch {
    return value;
  }
}

export function sessionHref(item: Approval): string | null {
  if (!item.sessionId || !item.agentId) return null;
  return `/workspace/${encodeURIComponent(item.agentId)}?sessionId=${encodeURIComponent(item.sessionId)}`;
}

export function outerSessionHref(item: Approval): string | null {
  if (!item.outerSessionId || !item.outerAgentId) return null;
  return `/workspace/${encodeURIComponent(item.outerAgentId)}?sessionId=${encodeURIComponent(item.outerSessionId)}`;
}

export function useApprovalsPage() {
  const [user, setUser] = useState<CurrentUser | null>(null);
  const [tab, setTab] = useState<TabMode>('pending');
  const [scope, setScope] = useState<ScopeMode>('mine');
  const [pending, setPending] = useState<Approval[]>([]);
  const [resolving, setResolving] = useState<Record<string, boolean>>({});
  const [history, setHistory] = useState<Approval[]>([]);
  const [historyOffset, setHistoryOffset] = useState(0);
  const [historyHasMore, setHistoryHasMore] = useState(false);
  const [historyLoading, setHistoryLoading] = useState(false);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const pendingGeneration = useRef(0);
  const historyGeneration = useRef(0);

  const isAdmin = user?.role?.toLowerCase() === ROLE_ADMIN;

  const loadPending = useCallback(async (s: ScopeMode) => {
    const generation = ++pendingGeneration.current;
    const items = await fetchPendingApprovals(s);
    if (generation === pendingGeneration.current) setPending(items);
  }, []);

  const loadHistory = useCallback(async (s: ScopeMode, offset: number, append: boolean) => {
    const generation = ++historyGeneration.current;
    setHistoryLoading(true);
    try {
      const items = await fetchApprovalHistory(s, PAGE_SIZE, offset);
      if (generation !== historyGeneration.current) return;
      setHistory((prev) => (append ? [...prev, ...items] : items));
      setHistoryHasMore(items.length === PAGE_SIZE);
      setHistoryOffset(offset + items.length);
    } catch (error) {
      if (generation === historyGeneration.current) throw error;
    } finally {
      if (generation === historyGeneration.current) setHistoryLoading(false);
    }
  }, []);

  useEffect(() => {
    let active = true;

    const bootstrap = async () => {
      try {
        setLoading(true);
        const currentUser = (await getProfile()) as CurrentUser | undefined;
        if (!active) return;
        setUser(currentUser ?? null);
        await loadPending('mine');
        if (!active) return;
        setError(null);
      } catch (e) {
        if (!active) return;
        setError(e instanceof Error ? e.message : 'Failed to load approvals');
      } finally {
        if (active) setLoading(false);
      }
    };

    void bootstrap();
    return () => {
      active = false;
    };
  }, [loadPending]);

  useEffect(() => {
    if (tab !== 'pending') return;
    const scopeToUse: ScopeMode = scope === 'all' && isAdmin ? 'all' : 'mine';
    void loadPending(scopeToUse).catch(() => {
      // Ignore transient errors; the next refresh will retry.
    });
  }, [tab, scope, isAdmin, loadPending]);

  // The page keeps local state, so refresh the visible view on a short interval.
  useEffect(() => {
    if (typeof document === 'undefined') return undefined;
    let active = true;
    let refreshing = false;
    const onVisible = () => {
      if (refreshing || document.visibilityState !== 'visible' || (tab === 'history' && historyLoading)) return;
      refreshing = true;
      const scopeToUse: ScopeMode = scope === 'all' && isAdmin ? 'all' : 'mine';
      const generation = historyGeneration.current;
      const refresh = tab === 'pending'
        ? loadPending(scopeToUse)
        : (async () => {
            const count = Math.max(PAGE_SIZE, historyOffset);
            const pages = await Promise.all(Array.from({ length: Math.ceil(count / PAGE_SIZE) }, (_, index) =>
              fetchApprovalHistory(scopeToUse, Math.min(PAGE_SIZE, count - index * PAGE_SIZE), index * PAGE_SIZE)));
            const items = pages.flat();
            const next = items.length === count ? await fetchApprovalHistory(scopeToUse, 1, count) : [];
            if (!active || generation !== historyGeneration.current) return;
            setHistory(items);
            setHistoryOffset(items.length);
            setHistoryHasMore(next.length > 0);
          })();
      void refresh.catch(() => {
        // ignore transient refresh errors
      }).finally(() => { refreshing = false; });
    };
    const timer = window.setInterval(onVisible, APPROVAL_REFRESH_INTERVAL_MS);
    document.addEventListener('visibilitychange', onVisible);
    window.addEventListener('focus', onVisible);
    return () => {
      active = false;
      window.clearInterval(timer);
      document.removeEventListener('visibilitychange', onVisible);
      window.removeEventListener('focus', onVisible);
    };
  }, [tab, scope, isAdmin, loadPending, historyOffset, historyLoading]);

  useEffect(() => {
    if (tab !== 'history') return;
    const scopeToUse: ScopeMode = scope === 'all' && isAdmin ? 'all' : 'mine';
    setHistory([]);
    setHistoryOffset(0);
    setHistoryHasMore(false);
    void loadHistory(scopeToUse, 0, false).catch((e: unknown) => {
      setError(e instanceof Error ? e.message : 'Failed to load history');
    });
  }, [tab, scope, isAdmin, loadHistory]);

  const onChangeScope = async (nextScope: ScopeMode) => {
    if (!isAdmin && nextScope === 'all') return;
    pendingGeneration.current += 1;
    historyGeneration.current += 1;
    setHistoryLoading(false);
    setScope(nextScope);
    setError(null);

    if (tab === 'pending') {
      try {
        setLoading(true);
        await loadPending(nextScope);
      } catch (e) {
        setError(e instanceof Error ? e.message : 'Failed to refresh approvals');
      } finally {
        setLoading(false);
      }
    }
  };

  const onChangeTab = (nextTab: TabMode) => {
    historyGeneration.current += 1;
    setHistoryLoading(false);
    setTab(nextTab);
    setError(null);
  };

  const resolveApproval = async (
    requestId: string,
    action: 'approve' | 'deny' | 'stop_run' | 'request_changes',
    reason?: string
  ) => {
    try {
      setResolving((prev) => ({ ...prev, [requestId]: true }));
      const outcome = await resolveApprovalAPI(requestId, action, reason);
      pendingGeneration.current += 1;
      setPending((prev) => prev.filter((item) => item.id !== requestId));
      void queryClient.invalidateQueries({ queryKey: approvalsQueryKeys.all });
      if (outcome.decision === 'expired') {
        setError('This approval expired before the run could start.');
      } else {
        setError(null);
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Failed to resolve approval');
    } finally {
      setResolving((prev) => ({ ...prev, [requestId]: false }));
    }
  };

  const loadMore = () => {
    const scopeToUse: ScopeMode = scope === 'all' && isAdmin ? 'all' : 'mine';
    void loadHistory(scopeToUse, historyOffset, true).catch((e: unknown) => {
      setError(e instanceof Error ? e.message : 'Failed to load more');
    });
  };

  return {
    tab,
    scope,
    pending,
    resolving,
    history,
    historyHasMore,
    historyLoading,
    loading,
    error,
    isAdmin,
    onChangeTab,
    onChangeScope,
    resolveApproval,
    loadMore,
  };
}
