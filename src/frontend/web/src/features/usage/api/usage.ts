import { apiClient } from '@/lib/apiClient';
import {
  usageRunsResponseSchema,
  usageSummaryResponseSchema,
  type UsageRunsResponse,
  type UsageScope,
  type UsageSummaryGroupBy,
  type UsageSummaryResponse,
} from '../schemas/usageSchema';

export interface UsageRunsQuery {
  scope?: UsageScope;
  userId?: string;
  sessionId?: string;
  agentId?: string;
  modelId?: string;
  providerId?: string;
  source?: string;
  fromDate?: string;
  toDate?: string;
  fromTime?: string;
  toTime?: string;
  limit?: number;
  offset?: number;
}

export interface UsageSummaryQuery extends UsageRunsQuery {
  groupBy?: UsageSummaryGroupBy;
  bucketLimit?: number;
}

function toParams(query: UsageRunsQuery | UsageSummaryQuery): Record<string, string | number> {
  return Object.fromEntries(
    Object.entries(query).filter(([, value]) => value !== undefined && value !== '')
  ) as Record<string, string | number>;
}

export async function fetchUsageRuns(query: UsageRunsQuery): Promise<UsageRunsResponse> {
  const response = await apiClient.get('/runtime/usage/runs', {
    params: toParams(query),
  });

  return usageRunsResponseSchema.parse(response.data);
}

export async function fetchUsageSummary(query: UsageSummaryQuery): Promise<UsageSummaryResponse> {
  const response = await apiClient.get('/runtime/usage/summary', {
    params: toParams(query),
  });

  return usageSummaryResponseSchema.parse(response.data);
}