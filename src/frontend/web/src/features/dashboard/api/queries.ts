/**
 * Dashboard feature TanStack Query hooks
 */
import { useQuery } from '@tanstack/react-query';
import {
  fetchApplianceHealth,
  fetchExecutionReadiness,
  fetchRecentSessions,
  fetchSystemHealth,
  fetchUpdateHistory,
} from './dashboard';
import { dashboardQueryKeys } from './queryKeys';

export function useRecentSessions(limit = 20) {
  return useQuery({
    queryKey: dashboardQueryKeys.recentSessions(),
    queryFn: () => fetchRecentSessions(limit),
  });
}

export function useSystemHealth() {
  return useQuery({
    queryKey: dashboardQueryKeys.systemHealth(),
    queryFn: fetchSystemHealth,
  });
}

export function useExecutionReadiness(enabled: boolean) {
  return useQuery({
    queryKey: dashboardQueryKeys.executionReadiness(),
    queryFn: fetchExecutionReadiness,
    enabled,
    refetchInterval: 10_000,
  });
}

export function useUpdateHistory() {
  return useQuery({
    queryKey: dashboardQueryKeys.updateHistory(),
    queryFn: fetchUpdateHistory,
    staleTime: 30_000,
  });
}

export function useApplianceHealth() {
  return useQuery({
    queryKey: dashboardQueryKeys.applianceHealth(),
    queryFn: fetchApplianceHealth,
    staleTime: 30_000,
  });
}
