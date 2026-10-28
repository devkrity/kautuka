import { z } from 'zod';

export const autopilotScheduleSchema = z.object({
  id: z.string(),
  ownerUserId: z.string(),
  targetType: z.string(),
  targetId: z.string(),
  cronExpression: z.string(),
  timeZoneId: z.string(),
  mode: z.string(),
  requiresPreRunApproval: z.boolean(),
  schedulePromptContext: z.string(),
  workspaceFilePaths: z.array(z.string()).default([]),
  notifyUiInbox: z.boolean(),
  notifyChannels: z.boolean(),
  preRunApprovalExpiryPolicy: z.enum(['next_occurrence', 'duration', 'never']),
  preRunApprovalTimeoutSeconds: z.number().int().nullable().optional(),
  overlapPolicy: z.enum(['skip_and_notify', 'skip_silently']),
  isPaused: z.boolean(),
  createdAt: z.string(),
  updatedAt: z.string(),
  nextDueAt: z.string().nullable().optional(),
  lastEvaluatedAt: z.string().nullable().optional(),
});

export const autopilotRunSchema = z.object({
  id: z.string(),
  scheduleId: z.string(),
  ownerUserId: z.string(),
  targetType: z.string(),
  targetId: z.string(),
  status: z.string(),
  triggerType: z.string(),
  scheduledFor: z.string(),
  sessionId: z.string().nullable().optional(),
  runtimeRunId: z.string().nullable().optional(),
  preRunApprovalRequestId: z.string().nullable().optional(),
  inputJson: z.string().nullable().optional(),
  outputJson: z.string().nullable().optional(),
  error: z.string().nullable().optional(),
  stateCategory: z.enum(['pending_approval', 'executing', 'terminal']),
  skipReason: z.string().nullable().optional(),
  blockingRunId: z.string().nullable().optional(),
  blockingRunStatusSnapshot: z.string().nullable().optional(),
  blockingRunStateCategory: z.enum(['pending_approval', 'executing', 'terminal']).nullable().optional(),
  blockedAt: z.string().nullable().optional(),
  createdAt: z.string(),
  updatedAt: z.string(),
  startedAt: z.string().nullable().optional(),
  endedAt: z.string().nullable().optional(),
});

export const autopilotInboxItemSchema = z.object({
  id: z.string(),
  scheduledRunId: z.string(),
  ownerUserId: z.string(),
  channelType: z.string(),
  destination: z.string(),
  status: z.string(),
  payloadJson: z.string().nullable().optional(),
  createdAt: z.string(),
  sentAt: z.string().nullable().optional(),
  error: z.string().nullable().optional(),
  isRead: z.boolean(),
  readAt: z.string().nullable().optional(),
});

export type AutopilotSchedule = z.infer<typeof autopilotScheduleSchema>;
export type AutopilotRun = z.infer<typeof autopilotRunSchema>;
export type AutopilotInboxItem = z.infer<typeof autopilotInboxItemSchema>;
