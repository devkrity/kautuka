/**
 * Approvals feature TanStack Query hooks
 */
import { useQuery, useMutation, type UseQueryOptions } from '@tanstack/react-query';
import { queryClient } from '@/lib/queryClient';
import {
  fetchPendingApprovals,
  fetchApprovalHistory,
  fetchApprovalsBySession,
  fetchApprovalById,
  fetchHitlDeniedCalls,
  fetchHitlStats,
  resolveApproval,
  type HitlStatsQuery,
} from './approvals';
import { type Approval, type HitlApprovalStatsResponse } from '../schemas/approvalSchema';
import { approvalsQueryKeys } from './queryKeys';

export type { HitlStatsQuery };

export function usePendingApprovals(
  scope: 'mine' | 'all' = 'mine',
  options?: Omit<UseQueryOptions<Approval[]>, 'queryKey' | 'queryFn'>
) {
  return useQuery({
    queryKey: approvalsQueryKeys.pending(scope),
    queryFn: () => fetchPendingApprovals(scope),
    ...options,
  });
}

export function useApprovalHistory(scope: 'mine' | 'all' = 'mine', limit = 50, offset = 0) {
  return useQuery({
    queryKey: [...approvalsQueryKeys.history(scope), limit, offset],
    queryFn: () => fetchApprovalHistory(scope, limit, offset),
  });
}

export function useApprovalsBySession(
  sessionId: string | null,
  options?: Omit<UseQueryOptions<Approval[]>, 'queryKey' | 'queryFn'>
) {
  return useQuery({
    queryKey: sessionId ? approvalsQueryKeys.bySession(sessionId) : ['approvals', 'no-session'],
    queryFn: () =>
      sessionId ? fetchApprovalsBySession(sessionId) : Promise.reject(new Error('No session ID')),
    enabled: !!sessionId,
    ...options,
  });
}

export function useApprovalById(approvalId: string | null) {
  return useQuery({
    queryKey: approvalId ? approvalsQueryKeys.byId(approvalId) : ['approvals', 'no-id'],
    queryFn: () =>
      approvalId ? fetchApprovalById(approvalId) : Promise.reject(new Error('No approval ID')),
    enabled: !!approvalId,
  });
}

export function useResolveApproval() {
  return useMutation({
    mutationFn: ({
      approvalId,
      reason,
      action,
    }: {
      approvalId: string;
      reason?: string;
      action: string;
    }) => resolveApproval(approvalId, action, reason),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: approvalsQueryKeys.all });
    },
  });
}

export function useHitlStats(
  query: HitlStatsQuery,
  options?: Omit<UseQueryOptions<HitlApprovalStatsResponse>, 'queryKey' | 'queryFn'>
) {
  const enabled =
    Boolean(query.agentId || query.mcpServerId || query.skillId) && options?.enabled !== false;
  return useQuery({
    queryKey: approvalsQueryKeys.stats(query),
    queryFn: () => fetchHitlStats(query),
    ...options,
    enabled,
  });
}

export function useHitlDeniedCalls(
  query: HitlStatsQuery,
  options?: Omit<UseQueryOptions<Approval[]>, 'queryKey' | 'queryFn'>
) {
  const enabled =
    Boolean(query.agentId || query.mcpServerId || query.skillId) && options?.enabled !== false;
  return useQuery({
    queryKey: approvalsQueryKeys.denied(query),
    queryFn: () => fetchHitlDeniedCalls(query),
    ...options,
    enabled,
  });
}
