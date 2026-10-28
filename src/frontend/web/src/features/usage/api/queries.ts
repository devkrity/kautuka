import { useQuery } from '@tanstack/react-query';
import { fetchUsageRuns, fetchUsageSummary, type UsageRunsQuery, type UsageSummaryQuery } from './usage';
import { usageQueryKeys } from './queryKeys';

export function useUsageRuns(query: UsageRunsQuery) {
  return useQuery({
    queryKey: usageQueryKeys.runs(query),
    queryFn: () => fetchUsageRuns(query),
  });
}

export function useUsageSummary(query: UsageSummaryQuery) {
  return useQuery({
    queryKey: usageQueryKeys.summary(query),
    queryFn: () => fetchUsageSummary(query),
  });
}