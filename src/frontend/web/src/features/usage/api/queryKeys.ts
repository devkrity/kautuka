import type { UsageRunsQuery, UsageSummaryQuery } from './usage';

export const usageQueryKeys = {
  runs: (query: UsageRunsQuery) => ['usage', 'runs', query] as const,
  summary: (query: UsageSummaryQuery) => ['usage', 'summary', query] as const,
};