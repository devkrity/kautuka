/**
 * Approvals feature Zod schemas
 */
import { z } from 'zod';

const optionalString = z.preprocess(
  (value) => (value === null ? undefined : value),
  z.string().optional()
);

const optionalIsoDateTime = z.preprocess(
  (value) => (value === null ? undefined : value),
  z.string().datetime({ offset: true }).optional()
);

const optionalDecision = z.preprocess(
  (value) => (value === null ? undefined : value),
  z.enum(['approved', 'denied', 'cancelled', 'stopped', 'expired', 'request_changes']).optional()
);

const optionalInteger = z.preprocess(
  (value) => (value === null ? undefined : value),
  z.number().int().optional()
);

const optionalBooleanWithDefault = (defaultValue: boolean) =>
  z.preprocess(
    (value) => (value === null ? undefined : value),
    z.boolean().optional().default(defaultValue)
  );

export const approvalActionSchema = z.object({
  label: z.string(),
  value: z.enum(['approve', 'deny', 'stop_run', 'request_changes']),
  style: z.enum(['default', 'primary', 'success', 'danger']).optional().default('default'),
  requiresRunId: z.boolean().optional().default(false),
});

export const approvalSchema = z.object({
  id: z.string(),
  sessionId: optionalString,
  runId: optionalString,
  checkpointId: optionalString,
  agentId: optionalString,
  agentName: optionalString,
  userId: optionalString,
  toolCallId: z.string(),
  toolName: z.string(),
  toolType: z.string(),
  mcpServerId: optionalString,
  argumentsJson: optionalString,
  requestedAt: z.string().datetime({ offset: true }),
  decision: optionalDecision,
  decidedBy: optionalString,
  decidedAt: optionalIsoDateTime,
  reason: optionalString,
  expiresAt: optionalIsoDateTime,
  actions: z.array(approvalActionSchema).default([]),
  outerSessionId: optionalString,
  outerAgentId: optionalString,
  outerAgentName: optionalString,
  decidedByDisplayName: optionalString,
  resolutionSource: optionalString,
  callerToolCallId: optionalString,
  callerToolName: optionalString,
  checkpointIndex: optionalInteger,
  checkpointBoundaryType: optionalString,
  checkpointCreatedAt: optionalIsoDateTime,
  checkpointResumable: optionalBooleanWithDefault(false),
  kind: optionalString,
  workflowId: optionalString,
  instanceId: optionalString,
  nodeId: optionalString,
  remainingChangeRequests: optionalInteger,
});

export const resolveApprovalResponseSchema = z.object({
  resolved: z.boolean(),
  decision: z.enum(['approved', 'denied', 'request_changes', 'stopped', 'expired']),
});

export const hitlApprovalStatItemSchema = z.object({
  toolName: z.string(),
  toolType: z.string(),
  approved: z.number().int(),
  denied: z.number().int(),
  agentCount: z.number().int(),
  mcpServerId: optionalString,
  skillId: optionalString,
  lastDeniedAt: optionalString,
  agentDefinitionRevision: optionalInteger,
});

export const hitlApprovalStatsResponseSchema = z.object({
  windowDays: z.number().int(),
  since: z.string(),
  items: z.array(hitlApprovalStatItemSchema),
});

export type ApprovalAction = z.infer<typeof approvalActionSchema>;
export type Approval = z.infer<typeof approvalSchema>;
export type ResolveApprovalResponse = z.infer<typeof resolveApprovalResponseSchema>;
export type HitlApprovalStatItem = z.infer<typeof hitlApprovalStatItemSchema>;
export type HitlApprovalStatsResponse = z.infer<typeof hitlApprovalStatsResponseSchema>;
