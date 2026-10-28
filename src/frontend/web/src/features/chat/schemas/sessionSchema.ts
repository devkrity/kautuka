/**
 * Chat feature Zod schemas
 */
import { z } from 'zod';

export const chatResponseSchema = z.object({
  response: z.string().optional().default(''),
  metrics: z.record(z.string(), z.unknown()).optional(),
  agentResponses: z.array(z.unknown()).optional(),
  agent_responses: z.array(z.unknown()).optional(),
  compacted: z.boolean().optional().default(false),
});

export type ChatResponse = z.infer<typeof chatResponseSchema>;

export const conversationAssetSchema = z.object({
  id: z.string(),
  fileName: z.string(),
  contentType: z.string(),
  sizeBytes: z.number(),
  origin: z.enum(['userUpload', 'clipboardPaste', 'agentOutput']),
  disposition: z.enum(['inlinePreview', 'download']),
  downloadUrl: z.string(),
  createdAt: z.string(),
});

export const parsedAssetStatusSchema = z.object({
  assetId: z.string(),
  sessionId: z.string(),
  status: z.string(),
  parserName: z.string(),
  parserVersion: z.string(),
  warning: z.string().nullable().optional(),
  error: z.string().nullable().optional(),
  updatedAt: z.string().nullable().optional(),
});

export const parsedAssetStatusesSchema = z.object({
  assets: z.array(parsedAssetStatusSchema),
});

export const conversationAssetsResponseSchema = z.object({
  assets: z.array(conversationAssetSchema),
});

export const messageSchema = z.object({
  id: z.string(),
  role: z.enum(['user', 'assistant', 'system']),
  content: z.string(),
  sequence: z.number().int().positive().optional(),
  timestamp: z.string(),
  compact: z.boolean().optional().default(false),
  thinking: z.string().nullable().optional(),
  metrics: z.unknown().optional(),
  run: z.unknown().nullable().optional(),
  executedActions: z.array(z.unknown()).nullable().optional(),
  activityEntries: z.array(z.unknown()).nullable().optional(),
  toolCalls: z.array(z.unknown()).nullable().optional(),
  source: z.string().nullable().optional(),
  assets: z.array(conversationAssetSchema).nullable().optional(),
  internal: z.boolean().optional(),
  traceId: z.string().nullable().optional(),
});

export const sessionSchema = z.object({
  id: z.string(),
  targetType: z.literal('agent'),
  targetId: z.string(),
  title: z.string().default(''),
  createdAt: z.string(),
  updatedAt: z.string(),
  messages: z.array(messageSchema).default([]),
  compacted: z.boolean().default(false),
  compactSummary: z.string().nullable().optional(),
  estimatedContextTokens: z.number().int().nullable().optional(),
  lastEstimatedAt: z.string().nullable().optional(),
  lastCompactedAt: z.string().nullable().optional(),
  lastCompactionReason: z.string().nullable().optional(),
  activeRun: z
    .object({
      runId: z.string(),
      sessionId: z.string(),
      targetType: z.literal('agent'),
      targetId: z.string(),
      userId: z.string(),
      message: z.string().nullable().optional(),
      lastSequence: z.number().int().nonnegative().default(0),
      startedAt: z.string(),
      status: z.string(),
    })
    .nullable()
    .optional(),
});

const runStateSchema = z.object({
  phase: z.string(),
  waitStateReason: z.string().nullable().optional(),
  continuationRunId: z.string().nullable().optional(),
});

const activityEntrySchema = z.object({
  id: z.string(),
  sequence: z.number().int(),
  kind: z.enum(['action', 'approval', 'checkpoint']),
  status: z.string(),
  actionKind: z.string().nullable().optional(),
  name: z.string().nullable().optional(),
  toolCallIds: z.array(z.string()).nullable().optional(),
  toolCallId: z.string().nullable().optional(),
  parentToolCallId: z.string().nullable().optional(),
  approvalRequestId: z.string().nullable().optional(),
  argumentsJson: z.string().nullable().optional(),
  resultJson: z.string().nullable().optional(),
  startedAt: z.string().nullable().optional(),
  completedAt: z.string().nullable().optional(),
  requestedAt: z.string().nullable().optional(),
  decidedAt: z.string().nullable().optional(),
  resolvedVia: z.string().nullable().optional(),
  checkpointIndex: z.number().int().nullable().optional(),
  checkpointId: z.string().nullable().optional(),
  checkpointBoundaryType: z.string().nullable().optional(),
  checkpointCreatedAt: z.string().nullable().optional(),
  checkpointResumable: z.boolean().optional().default(false),
});

const executedActionSchema = z.object({
  id: z.string(),
  kind: z.string(),
  name: z.string(),
  status: z.string(),
  toolCallIds: z.array(z.string()).nullable().optional(),
  approvalRequestId: z.string().nullable().optional(),
  continuationRunId: z.string().nullable().optional(),
  argumentsJson: z.string().nullable().optional(),
  resultJson: z.string().nullable().optional(),
  startedAt: z.string().nullable().optional(),
  completedAt: z.string().nullable().optional(),
});

const approvalActionSchema = z.object({
  label: z.string(),
  value: z.string(),
});

const approvalResponseSchema = z.object({
  id: z.string(),
  sessionId: z.string().nullable().optional(),
  runId: z.string().nullable().optional(),
  checkpointId: z.string().nullable().optional(),
  agentId: z.string().nullable().optional(),
  agentName: z.string().nullable().optional(),
  userId: z.string().nullable().optional(),
  toolCallId: z.string(),
  toolName: z.string(),
  toolType: z.string(),
  mcpServerId: z.string().nullable().optional(),
  argumentsJson: z.string().nullable().optional(),
  requestedAt: z.string(),
  decision: z.string().nullable().optional(),
  decidedBy: z.string().nullable().optional(),
  decidedAt: z.string().nullable().optional(),
  reason: z.string().nullable().optional(),
  expiresAt: z.string().nullable().optional(),
  actions: z.array(approvalActionSchema).default([]),
  outerSessionId: z.string().nullable().optional(),
  outerAgentId: z.string().nullable().optional(),
  outerAgentName: z.string().nullable().optional(),
  decidedByDisplayName: z.string().nullable().optional(),
  resolutionSource: z.string().nullable().optional(),
  callerToolCallId: z.string().nullable().optional(),
  callerToolName: z.string().nullable().optional(),
  checkpointIndex: z.number().int().nullable().optional(),
  checkpointBoundaryType: z.string().nullable().optional(),
  checkpointCreatedAt: z.string().nullable().optional(),
  checkpointResumable: z.boolean().optional().default(false),
});

const turnMessageSchema = z.object({
  id: z.string(),
  role: z.enum(['user', 'assistant', 'system']),
  content: z.string(),
  timestamp: z.string(),
  thinking: z.string().nullable().optional(),
  assets: z.array(conversationAssetSchema).nullable().optional(),
});

const chatTurnSchema = z.object({
  id: z.string(),
  sequence: z.number().int(),
  user: turnMessageSchema.nullable().optional(),
  assistant: turnMessageSchema.nullable().optional(),
  run: runStateSchema.nullable().optional(),
  activityEntries: z.array(activityEntrySchema).nullable().optional(),
  executedActions: z.array(executedActionSchema).nullable().optional(),
  metrics: z.unknown().nullable().optional(),
  traceId: z.string().nullable().optional(),
  rootRunId: z.string().nullable().optional(),
});

export const chatSessionViewSchema = z.object({
  session: z.object({
    id: z.string(),
    targetType: z.literal('agent'),
    targetId: z.string(),
    title: z.string(),
    createdAt: z.string(),
    updatedAt: z.string(),
    compacted: z.boolean(),
    compactSummary: z.string().nullable().optional(),
    estimatedContextTokens: z.number().int().nullable().optional(),
    lastEstimatedAt: z.string().nullable().optional(),
    lastCompactedAt: z.string().nullable().optional(),
    lastCompactionReason: z.string().nullable().optional(),
  }),
  runStatus: z.object({
    phase: z.string(),
    label: z.string(),
    isActive: z.boolean(),
    activeRunId: z.string().nullable().optional(),
    lastSequence: z.number().int().nonnegative().default(0),
    startedAt: z.string().nullable().optional(),
    waitStateReason: z.string().nullable().optional(),
    composerLockReason: z.string().nullable().optional(),
  }),
  turns: z.array(chatTurnSchema).default([]),
  pendingApprovals: z.array(approvalResponseSchema).default([]),
});

export type Message = z.infer<typeof messageSchema>;
export type Session = z.infer<typeof sessionSchema>;
export type ChatSessionView = z.infer<typeof chatSessionViewSchema>;
export type ChatSessionViewTurn = z.infer<typeof chatTurnSchema>;
export type ChatSessionViewApproval = z.infer<typeof approvalResponseSchema>;
export type ConversationAssetPayload = z.infer<typeof conversationAssetSchema>;
export type ParsedAssetStatusPayload = z.infer<typeof parsedAssetStatusSchema>;
export type ParsedAssetStatusesPayload = z.infer<typeof parsedAssetStatusesSchema>;
