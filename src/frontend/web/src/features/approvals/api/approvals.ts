/**
 * Approvals feature API operations
 */
import { apiClient } from '@/lib/apiClient';
import { naradCommandClient } from '@/lib/naradCommandClient';
import {
  approvalSchema,
  hitlApprovalStatsResponseSchema,
  resolveApprovalResponseSchema,
  type Approval,
  type HitlApprovalStatsResponse,
  type ResolveApprovalResponse,
} from '../schemas/approvalSchema';

function parseApprovalArray(data: unknown): Approval[] {
  if (!Array.isArray(data)) {
    throw new Error('Gateway approval response must be an array.');
  }
  return data.map((approval) => approvalSchema.parse(approval));
}

export async function fetchPendingApprovals(scope: 'mine' | 'all' = 'mine'): Promise<Approval[]> {
  const response = await apiClient.get('/approvals/pending', {
    params: scope === 'all' ? { scope: 'all' } : undefined,
  });
  return parseApprovalArray(response.data);
}

export async function fetchApprovalHistory(
  scope: 'mine' | 'all' = 'mine',
  limit = 50,
  offset = 0
): Promise<Approval[]> {
  const response = await apiClient.get('/approvals/history', {
    params: { ...(scope === 'all' ? { scope: 'all' } : {}), limit, offset },
  });
  return parseApprovalArray(response.data);
}

export async function fetchApprovalsBySession(sessionId: string): Promise<Approval[]> {
  const response = await apiClient.get(`/approvals/session/${sessionId}`);
  return parseApprovalArray(response.data);
}

export async function fetchApprovalById(approvalId: string): Promise<Approval> {
  const response = await apiClient.get(`/approvals/${approvalId}`);
  return approvalSchema.parse(response.data);
}

/** Command responses use either the stored decision or the action verb. */
export function toStoredApprovalDecision(decision: string | null | undefined): ResolveApprovalResponse['decision'] | null {
  if (decision === 'approve') return 'approved';
  if (decision === 'deny') return 'denied';
  if (decision === 'stop_run') return 'stopped';
  if (
    decision === 'approved' ||
    decision === 'denied' ||
    decision === 'request_changes' ||
    decision === 'stopped' ||
    decision === 'expired'
  ) {
    return decision;
  }
  return null;
}

export async function resolveApproval(
  approvalId: string,
  action: string,
  reason?: string
): Promise<ResolveApprovalResponse> {
  const sent = action;
  const result = await naradCommandClient.resolveInterrupt({
    interruptId: approvalId,
    decision: sent,
    reason,
  });
  const status = typeof result.status === 'string' ? result.status : '';
  const decision =
    toStoredApprovalDecision(result.decision) ??
    (status === 'expired' ? 'expired' : status === 'accepted' || status === 'already_applied' ? toStoredApprovalDecision(sent) : null);
  if (decision == null) {
    throw new Error(result.detail?.trim() || 'Could not resolve the approval.');
  }
  if (status !== 'accepted' && status !== 'already_applied' && status !== 'expired') {
    throw new Error(result.detail?.trim() || 'Could not resolve the approval.');
  }
  return resolveApprovalResponseSchema.parse({
    resolved: status === 'accepted' || status === 'already_applied',
    decision,
  });
}

export type HitlStatsWindow = 7 | 30 | 'all';

export type HitlStatsQuery = {
  agentId?: string;
  mcpServerId?: string;
  skillId?: string;
  toolName?: string;
  days?: HitlStatsWindow;
  scope?: 'mine' | 'all';
  agentDefinitionRevision?: number;
  ungrouped?: boolean;
};

export function buildHitlStatsRequest(query: HitlStatsQuery): {
  path: string;
  params: Record<string, string | number>;
} {
  const params: Record<string, string | number> = {};
  if (query.toolName) params.toolName = query.toolName;
  if (query.days === 'all') params.days = 'all';
  else if (query.days != null) params.days = query.days;
  if (query.scope === 'all') params.scope = 'all';
  if (query.ungrouped) params.ungrouped = 1;
  else if (query.agentDefinitionRevision != null) {
    params.agentDefinitionRevision = query.agentDefinitionRevision;
  }

  if (query.agentId) {
    if (query.skillId) params.skillId = query.skillId;
    if (query.mcpServerId) params.mcpServerId = query.mcpServerId;
    return { path: `/agents/${encodeURIComponent(query.agentId)}/stats`, params };
  }
  if (query.mcpServerId) {
    return { path: `/connectors/${encodeURIComponent(query.mcpServerId)}/stats`, params };
  }
  if (query.skillId) {
    return { path: `/skills/${encodeURIComponent(query.skillId)}/stats`, params };
  }
  throw new Error('agentId, mcpServerId, or skillId is required');
}

export async function fetchHitlStats(query: HitlStatsQuery): Promise<HitlApprovalStatsResponse> {
  const request = buildHitlStatsRequest(query);
  const response = await apiClient.get(request.path, { params: request.params });
  return hitlApprovalStatsResponseSchema.parse(response.data);
}

export async function fetchHitlDeniedCalls(query: HitlStatsQuery): Promise<Approval[]> {
  const request = buildHitlStatsRequest(query);
  const response = await apiClient.get(`${request.path}/denied`, {
    params: { ...request.params, limit: 20 },
  });
  return parseApprovalArray(response.data);
}

/** Existence probe only — keep approvals chunk free of the chat feature graph. */
export async function probeSessionExists(sessionId: string): Promise<boolean> {
  try {
    await apiClient.get(`/sessions/${encodeURIComponent(sessionId)}`);
    return true;
  } catch {
    return false;
  }
}
