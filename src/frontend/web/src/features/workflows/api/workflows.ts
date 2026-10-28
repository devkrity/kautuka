import { apiClient } from '@/lib/apiClient';
import {
  workflowDefinitionSchema,
  workflowInstanceSchema,
  type SaveWorkflowRequest,
  type WorkflowDefinition,
  type WorkflowInstance,
} from '../schemas/workflowSchema';

export type { SaveWorkflowRequest };

export async function listWorkflows(archived = false): Promise<WorkflowDefinition[]> {
  const response = await apiClient.get('/workflows', {
    params: archived ? { archived: true } : undefined,
  });
  return (Array.isArray(response.data) ? response.data : []).map((item: unknown) =>
    workflowDefinitionSchema.parse(item)
  );
}

export async function archiveWorkflow(workflowId: string): Promise<void> {
  await apiClient.post(`/workflows/${workflowId}/archive`, {});
}

export async function restoreWorkflow(workflowId: string): Promise<void> {
  await apiClient.post(`/workflows/${workflowId}/unarchive`, {});
}

export async function deleteWorkflow(workflowId: string): Promise<void> {
  await apiClient.delete(`/workflows/${workflowId}`);
}

export async function createWorkflow(request: SaveWorkflowRequest): Promise<WorkflowDefinition> {
  const response = await apiClient.post('/workflows', request);
  return workflowDefinitionSchema.parse(response.data);
}

export async function getWorkflow(workflowId: string): Promise<WorkflowDefinition> {
  const response = await apiClient.get(`/workflows/${workflowId}`);
  return workflowDefinitionSchema.parse(response.data);
}

export async function updateWorkflow(
  workflowId: string,
  request: SaveWorkflowRequest
): Promise<WorkflowDefinition> {
  const response = await apiClient.put(`/workflows/${workflowId}`, request);
  return workflowDefinitionSchema.parse(response.data);
}

export async function listWorkflowInstances(workflowId?: string): Promise<WorkflowInstance[]> {
  const path = workflowId ? `/workflows/${workflowId}/instances` : '/workflows/instances';
  const response = await apiClient.get(path, { params: { limit: 50 } });
  return (Array.isArray(response.data) ? response.data : []).map((item: unknown) =>
    workflowInstanceSchema.parse(item)
  );
}

export async function startWorkflowInstance(
  workflowId: string,
  input: Record<string, unknown>,
  correlationKey?: string,
  files: string[] = []
): Promise<WorkflowInstance> {
  const response = await apiClient.post(`/workflows/${workflowId}/instances`, {
    input,
    correlationKey: correlationKey || undefined,
    files,
  });
  return workflowInstanceSchema.parse(response.data);
}

export async function testWorkflowStep(
  workflowId: string,
  nodeId: string,
  input: Record<string, unknown>
): Promise<{
  results: Record<string, unknown> | null;
  error: string | null;
  droppedKeys: string[];
}> {
  const response = await apiClient.post(
    `/workflows/${workflowId}/steps/${encodeURIComponent(nodeId)}/test`,
    { input }
  );
  return response.data;
}

export async function getWorkflowInstance(
  workflowId: string,
  instanceId: string
): Promise<WorkflowInstance> {
  const response = await apiClient.get(`/workflows/${workflowId}/instances/${instanceId}`);
  return workflowInstanceSchema.parse(response.data);
}

export async function stopWorkflowInstance(
  workflowId: string,
  instanceId: string
): Promise<WorkflowInstance> {
  const response = await apiClient.post(
    `/workflows/${workflowId}/instances/${instanceId}/stop`,
    {}
  );
  return workflowInstanceSchema.parse(response.data);
}

export interface WorkflowJournalEvent {
  type: string;
  eventId: string;
  timestamp: string;
  workflowSequence?: number;
  sessionSequence?: number;
  nodeId?: string;
  interruptId?: string;
  code?: string;
  message?: string;
  agentSessionId?: string;
  agentRunId?: string;
  stepAttempt?: number;
  definitionId?: string;
  envelopeHash?: string;
  envelopeRevision?: number;
  droppedKeys?: string[];
  from?: string;
  to?: string;
  pointer?: string;
  op?: string;
  caseIndex?: number;
}

function readOptionalString(value: unknown): string | undefined {
  return typeof value === 'string' && value.trim() ? value : undefined;
}

function readOptionalNumber(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
}

function readOptionalStringArray(value: unknown): string[] | undefined {
  if (!Array.isArray(value)) {
    return undefined;
  }
  const items = value.filter(
    (item): item is string => typeof item === 'string' && item.trim().length > 0
  );
  return items.length > 0 ? items : undefined;
}

export function parseWorkflowJournalEvent(value: unknown): WorkflowJournalEvent | null {
  if (!value || typeof value !== 'object') {
    return null;
  }
  const record = value as Record<string, unknown>;
  const type = readOptionalString(record.type);
  const eventId = readOptionalString(record.eventId);
  const timestamp = readOptionalString(record.timestamp);
  if (!type || !eventId || !timestamp) {
    return null;
  }

  return {
    type,
    eventId,
    timestamp,
    workflowSequence: readOptionalNumber(record.workflowSequence),
    sessionSequence: readOptionalNumber(record.sessionSequence),
    nodeId: readOptionalString(record.nodeId),
    interruptId: readOptionalString(record.interruptId),
    code: readOptionalString(record.code),
    message: readOptionalString(record.message),
    agentSessionId: readOptionalString(record.agentSessionId),
    agentRunId: readOptionalString(record.agentRunId),
    stepAttempt: readOptionalNumber(record.stepAttempt),
    definitionId: readOptionalString(record.definitionId),
    envelopeHash: readOptionalString(record.envelopeHash),
    envelopeRevision: readOptionalNumber(record.envelopeRevision),
    droppedKeys: readOptionalStringArray(record.droppedKeys),
    from: readOptionalString(record.from),
    to: readOptionalString(record.to),
    pointer: readOptionalString(record.pointer),
    op: readOptionalString(record.op),
    caseIndex: readOptionalNumber(record.caseIndex),
  };
}

export async function listWorkflowJournal(
  workflowId: string,
  instanceId: string
): Promise<WorkflowJournalEvent[]> {
  const response = await apiClient.get(
    `/narad/v1/workflows/${workflowId}/instances/${instanceId}/events`,
    { params: { limit: 200 } }
  );
  const events = Array.isArray(response.data?.events) ? response.data.events : [];
  return events
    .map((item: unknown) => parseWorkflowJournalEvent(item))
    .filter((item: WorkflowJournalEvent | null): item is WorkflowJournalEvent => item !== null);
}
