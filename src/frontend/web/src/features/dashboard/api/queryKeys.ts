/**
 * Dashboard feature query key factory
 */

export const dashboardQueryKeys = {
  all: ['dashboard'] as const,
  recentSessions: () => [...dashboardQueryKeys.all, 'recentSessions'] as const,
  systemHealth: () => [...dashboardQueryKeys.all, 'systemHealth'] as const,
  executionReadiness: () => [...dashboardQueryKeys.all, 'executionReadiness'] as const,
  updateHistory: () => [...dashboardQueryKeys.all, 'updateHistory'] as const,
  applianceHealth: () => [...dashboardQueryKeys.all, 'applianceHealth'] as const,
};
