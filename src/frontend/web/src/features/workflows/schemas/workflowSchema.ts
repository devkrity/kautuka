import { z } from 'zod';

const jsonValueSchema: z.ZodType<unknown> = z.lazy(() =>
  z.union([
    z.string(),
    z.number(),
    z.boolean(),
    z.null(),
    z.array(jsonValueSchema),
    z.record(z.string(), jsonValueSchema),
  ])
);

export const workflowMapSchema = z.object({
  from: z.string(),
  to: z.string(),
});

export const workflowSwitchCaseSchema = z.object({
  pointer: z.string(),
  op: z.string(),
  value: z.string().nullable().optional(),
  goTo: z.string(),
});

export const workflowNodeSchema = z.object({
  nodeId: z.string(),
  agentId: z.string(),
  promptTemplate: z.string(),
  outputKind: z.string(),
  outputSchema: z.record(z.string(), jsonValueSchema).nullable().optional(),
  maps: z.array(workflowMapSchema),
  sideEffects: z.string(),
  memoryWrites: z.boolean(),
  requiresReview: z.boolean(),
  maxRetries: z.number().int(),
  switchCases: z.array(workflowSwitchCaseSchema).optional().nullable(),
  defaultGoTo: z.string().optional().nullable(),
});

export const workflowDefinitionSchema = z.object({
  id: z.string(),
  ownerUserId: z.string(),
  name: z.string(),
  governed: z.boolean(),
  revision: z.number().int(),
  inputSchema: z.record(z.string(), jsonValueSchema),
  workingSchema: z.record(z.string(), jsonValueSchema),
  outputSchema: z.record(z.string(), jsonValueSchema),
  nodes: z.array(workflowNodeSchema),
  createdAt: z.string(),
  updatedAt: z.string(),
  archived: z.boolean().optional(),
  hasRuns: z.boolean().optional(),
  diagram: z.string().nullable().optional(),
});

export const workflowStepAttemptSchema = z.object({
  id: z.string(),
  instanceId: z.string(),
  nodeId: z.string(),
  attempt: z.number().int(),
  runId: z.string(),
  sessionId: z.string().nullable().optional(),
  status: z.string(),
  failureCode: z.string().nullable().optional(),
  startedAt: z.string().nullable().optional(),
  endedAt: z.string().nullable().optional(),
  createdAt: z.string(),
  reviewNote: z.string().nullable().optional(),
});

export const workflowInstanceSchema = z.object({
  id: z.string(),
  workflowId: z.string(),
  definitionRevision: z.number().int(),
  ownerUserId: z.string(),
  correlationKey: z.string().nullable().optional(),
  status: z.string(),
  stopRequested: z.boolean(),
  currentNodeId: z.string().nullable().optional(),
  envelope: z.record(z.string(), jsonValueSchema),
  envelopeHash: z.string(),
  envelopeRevision: z.number().int(),
  snapshot: workflowDefinitionSchema,
  steps: z.array(workflowStepAttemptSchema),
  failureCode: z.string().nullable().optional(),
  error: z.string().nullable().optional(),
  createdAt: z.string(),
  updatedAt: z.string(),
  startedAt: z.string().nullable().optional(),
  endedAt: z.string().nullable().optional(),
});

export type WorkflowMap = z.infer<typeof workflowMapSchema>;
export type WorkflowNode = z.infer<typeof workflowNodeSchema>;
export type WorkflowDefinition = z.infer<typeof workflowDefinitionSchema>;
export type WorkflowStepAttempt = z.infer<typeof workflowStepAttemptSchema>;
export type WorkflowInstance = z.infer<typeof workflowInstanceSchema>;

export interface SaveWorkflowRequest {
  name: string;
  governed: boolean;
  inputSchema: Record<string, unknown>;
  workingSchema: Record<string, unknown>;
  outputSchema: Record<string, unknown>;
  nodes: Array<{
    nodeId: string;
    agentId: string;
    promptTemplate: string;
    outputKind: string;
    outputSchema?: Record<string, unknown> | null;
    maps: WorkflowMap[];
    sideEffects: string;
    memoryWrites: boolean;
    requiresReview: boolean;
    maxRetries: number;
    switchCases?: Array<{ pointer: string; op: string; value?: string | null; goTo: string }>;
    defaultGoTo?: string | null;
  }>;
}

export const defaultWorkflowSchema: Record<string, unknown> = {
  type: 'object',
  required: ['title'],
  properties: {
    title: { type: 'string' },
  },
};
