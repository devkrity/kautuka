import { z } from 'zod';

const optionalString = z.string().nullable().optional();
const optionalNumber = z.number().nullable().optional();

export const usageScopeSchema = z.enum(['self', 'all']);
export const usageSummaryGroupBySchema = z.enum([
  'none',
  'date',
  'session',
  'model',
  'provider',
  'agent',
  'source',
  'user',
]);

export const usageTotalsSchema = z.object({
  runCount: z.number(),
  providerRequestCount: z.number(),
  assistantTurnCount: z.number(),
  modelTurnCount: z.number(),
  toolCallCount: z.number(),
  tokenIn: z.number(),
  tokenOut: z.number(),
  totalTokens: z.number(),
  totalDurationMs: z.number(),
  avgFirstTokenLatencyMs: optionalNumber,
  inputCost: optionalNumber,
  outputCost: optionalNumber,
  totalCost: optionalNumber,
});

export const usageSummaryBucketSchema = z.object({
  key: z.string(),
  label: z.string(),
  totals: usageTotalsSchema,
});

export const usageRunSchema = z.object({
  runId: z.string(),
  sessionId: optionalString,
  userId: optionalString,
  usernameSnapshot: optionalString,
  userDisplayNameSnapshot: optionalString,
  agentId: optionalString,
  agentNameSnapshot: optionalString,
  modelId: optionalString,
  modelNameSnapshot: optionalString,
  providerId: optionalString,
  providerNameSnapshot: optionalString,
  runStatus: z.string(),
  sourceChannel: optionalString,
  runStartedAt: z.string().datetime({ offset: true }),
  firstModelOutputAt: optionalString,
  runEndedAt: z.string().datetime({ offset: true }),
  totalDurationMs: z.number(),
  firstTokenLatencyMs: optionalNumber,
  tokenIn: optionalNumber,
  tokenOut: optionalNumber,
  totalTokens: optionalNumber,
  providerRequestCount: optionalNumber,
  modelTurnCount: optionalNumber,
  assistantTurnCount: optionalNumber,
  toolCallCount: optionalNumber,
  costBasis: optionalString,
  inputCost: optionalNumber,
  outputCost: optionalNumber,
  totalCost: optionalNumber,
  costCurrency: optionalString,
  pricingSource: optionalString,
  usageDate: z.string(),
});

export const usageRunsResponseSchema = z.object({
  items: z.array(usageRunSchema),
  limit: z.number(),
  offset: z.number(),
  totalCount: z.number(),
  scope: usageScopeSchema,
});

export const usageSummaryResponseSchema = z.object({
  scope: usageScopeSchema,
  groupBy: usageSummaryGroupBySchema,
  totals: usageTotalsSchema,
  buckets: z.array(usageSummaryBucketSchema),
});

export type UsageScope = z.infer<typeof usageScopeSchema>;
export type UsageSummaryGroupBy = z.infer<typeof usageSummaryGroupBySchema>;
export type UsageTotals = z.infer<typeof usageTotalsSchema>;
export type UsageSummaryBucket = z.infer<typeof usageSummaryBucketSchema>;
export type UsageRun = z.infer<typeof usageRunSchema>;
export type UsageRunsResponse = z.infer<typeof usageRunsResponseSchema>;
export type UsageSummaryResponse = z.infer<typeof usageSummaryResponseSchema>;