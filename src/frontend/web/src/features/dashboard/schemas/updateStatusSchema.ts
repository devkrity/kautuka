import { z } from 'zod';

export const updateReleaseSchema = z.object({
  releaseId: z.string(),
  version: z.string(),
  channel: z.string(),
  releaseFingerprint: z.string(),
  sourceUrl: z.string().nullable().optional(),
  publishedAtUtc: z.string().nullable().optional(),
  tag: z.string().nullable().optional(),
});

export const updateStatusSchema = z.object({
  ok: z.boolean(),
  message: z.string(),
  state: z.string(),
  effectiveReleaseChannel: z.string().nullable().optional(),
  eligibilityState: z.string().nullable().optional(),
  eligibilityReason: z.string().nullable().optional(),
  currentRelease: updateReleaseSchema.nullable().optional(),
  availableRelease: updateReleaseSchema.nullable().optional(),
  lastKnownGoodRelease: updateReleaseSchema.nullable().optional(),
  lastDiscoveryAttemptUtc: z.string().nullable().optional(),
  lastDiscoverySuccessUtc: z.string().nullable().optional(),
  managerVersion: z.string(),
  hostReady: z.boolean(),
  activeExecCount: z.number(),
  lastError: z.string().nullable().optional(),
});

export type UpdateStatusSummary = z.infer<typeof updateStatusSchema>;
