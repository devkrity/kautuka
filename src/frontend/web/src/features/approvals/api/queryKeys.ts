/**
 * Approvals feature query key factory
 */

export const approvalsQueryKeys = {
  all: ['approvals'] as const,
  pending: (scope: 'mine' | 'all' = 'mine') => [...approvalsQueryKeys.all, 'pending', scope] as const,
  history: (scope: 'mine' | 'all' = 'mine') => [...approvalsQueryKeys.all, 'history', scope] as const,
  bySession: (sessionId: string) => [...approvalsQueryKeys.all, 'session', sessionId] as const,
  byId: (approvalId: string) => [...approvalsQueryKeys.all, approvalId] as const,
  stats: (filters: Record<string, string | number | boolean | undefined>) =>
    [...approvalsQueryKeys.all, 'stats', filters] as const,
  denied: (filters: Record<string, string | number | boolean | undefined>) =>
    [...approvalsQueryKeys.all, 'stats', 'denied', filters] as const,
};
